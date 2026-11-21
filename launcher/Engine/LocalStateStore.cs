using System.Text.Json;

namespace Dnf90.Launcher;

// Owns the only persistent local state the launcher is allowed to keep:
//   identity.json - account + compliance consent
//   anchor.json   - anti-rollback sequence anchors + repeated-failure guard
//
// Design rule (see 登录器架构重构设计.md): configuration, versions, manifests,
// news and feature flags are NEVER persisted here. They arrive gateway-signed
// on every start and live in memory only.
//
// A one-shot migration converts the legacy preferences.json/bootstrap.json
// into the two files above and leaves a timestamped backup before removal.
public sealed class LocalStateStore
{
    private readonly string _identityPath;
    private readonly string _anchorPath;

    public IdentityState Identity { get; private set; } = new();
    public AnchorState Anchor { get; private set; } = new();

    public LocalStateStore(string stateRoot)
    {
        _identityPath = Path.Combine(stateRoot, "identity.json");
        _anchorPath = Path.Combine(stateRoot, "anchor.json");
    }

    public void Load()
    {
        SafeFiles.NoLinks(_identityPath);
        SafeFiles.NoLinks(_anchorPath);
        if (File.Exists(_identityPath))
        {
            var state = JsonSerializer.Deserialize(File.ReadAllBytes(_identityPath), WireJson.Default.IdentityState);
            if (state is not null && state.V == 1) Identity = state;
        }
        if (File.Exists(_anchorPath))
        {
            var state = JsonSerializer.Deserialize(File.ReadAllBytes(_anchorPath), WireJson.Default.AnchorState);
            if (state is not null && state.V == 1) Anchor = state;
        }
    }

    public void SaveIdentity() => SafeFiles.Atomic(_identityPath, JsonSerializer.SerializeToUtf8Bytes(Identity, WireJson.Default.IdentityState));
    public void SaveAnchor() => SafeFiles.Atomic(_anchorPath, JsonSerializer.SerializeToUtf8Bytes(Anchor, WireJson.Default.AnchorState));

    public void SetAccount(string account, bool remember)
    {
        Identity = Identity with { Account = remember ? account.Trim() : "", RememberAccount = remember };
        SaveIdentity();
    }

    public void SetConsent(int antiCheat, int diagnostics, string problemType)
    {
        Identity = Identity with { Consent = new ConsentState(antiCheat, diagnostics, problemType) };
        SaveIdentity();
    }

    private static long SourceSeq(Bootstrap b) => b.Content?.SourceSequence ?? 0;

    public bool AntiRollbackAccept(Bootstrap next) =>
        next.Sequence >= Anchor.Sequence && SourceSeq(next) >= Anchor.ContentSourceSequence;

    public void AdvanceAnchor(Bootstrap next)
    {
        Anchor = Anchor with
        {
            Sequence = next.Sequence,
            ContentSourceSequence = SourceSeq(next),
            Attempt = new UpdateAttempt()
        };
        SaveAnchor();
    }

    public bool IsThrottled(string target, int threshold = 3) =>
        Anchor.Attempt is { } a && a.Failures >= threshold && string.Equals(a.Target, target, StringComparison.Ordinal);

    public void RecordFailure(string target)
    {
        var a = Anchor.Attempt ?? new UpdateAttempt();
        var failures = string.Equals(a.Target, target, StringComparison.Ordinal) ? a.Failures + 1 : 1;
        Anchor = Anchor with { Attempt = new UpdateAttempt(target, failures, DateTimeOffset.UtcNow.ToString("O")) };
        SaveAnchor();
    }

    public void ResetFailures()
    {
        if (Anchor.Attempt is { Failures: > 0 } || Anchor.Attempt is not null)
        {
            Anchor = Anchor with { Attempt = new UpdateAttempt() };
            SaveAnchor();
        }
    }

    // Convert the legacy preferences.json + published bootstrap into the new
    // two-file layout. Idempotent: if identity.json already exists, no-op.
    // Returns true when a migration actually happened.
    public bool MigrateFromLegacy(string stateRoot, Func<Bootstrap?> signedBootstrap)
    {
        if (File.Exists(_identityPath)) return false;
        var preferencesPath = Path.Combine(stateRoot, "preferences.json");
        Preferences? pref = null;
        if (File.Exists(preferencesPath))
        {
            try { pref = JsonSerializer.Deserialize(File.ReadAllBytes(preferencesPath), WireJson.Default.Preferences); }
            catch { /* corrupt legacy preference - migrate anchors only */ }
        }
        if (pref is not null)
        {
            Identity = new IdentityState(
                1,
                pref.RememberAccount ? (pref.Account ?? "").Trim() : "",
                pref.RememberAccount,
                new ConsentState(pref.AntiCheatConsentVersion, pref.Diagnostics?.HasConsent == true ? 1 : 0, pref.Diagnostics?.ProblemType ?? "auto"));
            SaveIdentity();
        }
        else
        {
            SaveIdentity();
        }
        try
        {
            var boot = signedBootstrap();
            if (boot is not null)
                Anchor = new AnchorState(1, boot.Sequence, SourceSeq(boot), new UpdateAttempt());
        }
        catch { /* legacy bootstrap invalid - start anchors at 0 */ }
        SaveAnchor();

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var backupDir = Path.Combine(stateRoot, "legacy-backup-" + stamp);
        Directory.CreateDirectory(backupDir);
        foreach (var name in new[] { "preferences.json", "bootstrap.json", "content-check.txt" })
        {
            var p = Path.Combine(stateRoot, name);
            if (File.Exists(p))
                try { File.Move(p, Path.Combine(backupDir, name)); } catch { }
        }
        foreach (var name in new[] { "downloads", "bundle-downloads", "diagnostics" })
        {
            var d = Path.Combine(stateRoot, name);
            if (Directory.Exists(d))
                try { Directory.Move(d, Path.Combine(backupDir, name)); } catch { }
        }
        return true;
    }
}
