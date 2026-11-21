using System.Text.Json;
using Dnf90.Launcher;
using Dnf90.Launcher.Diagnostics;
using ServerS5A3;

// Verifies the minimal-local-state store: identity + anchor only, with
// anti-rollback sequence guards and repeated-failure throttling.
internal static class LocalStateStoreChecks
{
    internal static void Run(string temp, Action<bool, string> check)
    {
        var root = Path.Combine(temp, "local-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var store = new LocalStateStore(root);
        store.Load();
        check(store.Identity.Account == "" && store.Identity.Consent is null, "fresh identity is empty");
        check(store.Anchor.Sequence == 0 && store.Anchor.GameSourceSequence == 0, "fresh anchor is zero");

        store.SetAccount("  player01  ", true);
        store.SetConsent(1, 0, "auto");
        var reloaded = new LocalStateStore(root);
        reloaded.Load();
        check(reloaded.Identity.Account == "player01" && reloaded.Identity.Consent?.AntiCheat == 1,
            "identity persists account and consent across restart");
        check(File.Exists(Path.Combine(root, "identity.json")), "identity file written");

        reloaded.SetAccount("temp-player", false);
        var noRemember = new LocalStateStore(root);
        noRemember.Load();
        check(noRemember.Identity.Account == "", "forgotten account is not persisted");

        // Anti-rollback: sequence and game source sequence must not go backwards.
        var baseProfile = new PlayerProfile("https://example.test", "cert", "key", "", "game", 8080, "2551b8a7", "4e4020fa", "");
        GameArtifact Game(long seq) => new("1.2.27", "src", seq,
            [new("DNF.exe", 10, baseProfile.ExeSha256), new("Script.pvf", 20, baseProfile.PvfSha256)],
            new GameDelivery("archive", URLs: ["http://a/x", "http://b/y"], PackageSha256: new string('1', 64), PackageSize: 1024));
        var boot10 = new Bootstrap("DNF90-Flutter", 2, 10, baseProfile, [], null, Game(32));
        var boot9 = boot10 with { Sequence = 9 };
        var boot11 = boot10 with { Sequence = 11 };
        noRemember.AdvanceAnchor(boot10);
        check(!noRemember.AntiRollbackAccept(boot9), "lower sequence rejected as rollback");
        check(noRemember.AntiRollbackAccept(boot10) && noRemember.AntiRollbackAccept(boot11), "equal or higher sequence accepted");

        var lowerSource = boot11 with { Game = Game(5) };
        noRemember.AdvanceAnchor(boot11 with { Game = Game(32) });
        check(!noRemember.AntiRollbackAccept(lowerSource), "lower game source sequence rejected");

        // Repeated-failure guard: same target throttles at 3, reset clears it.
        var target = "c1b420834a1a";
        check(!noRemember.IsThrottled(target), "no throttle before failures");
        noRemember.RecordFailure(target);
        noRemember.RecordFailure(target);
        check(!noRemember.IsThrottled(target), "not throttled at 2 failures");
        noRemember.RecordFailure(target);
        check(noRemember.IsThrottled(target), "throttled at 3 failures on same target");
        var reloadedThrottle = new LocalStateStore(root);
        reloadedThrottle.Load();
        check(reloadedThrottle.IsThrottled(target), "failure count persists across restart");
        check(!reloadedThrottle.IsThrottled("another-target"), "throttle is per-target");
        reloadedThrottle.ResetFailures();
        check(!reloadedThrottle.IsThrottled(target), "manual retry resets throttle");

        // One-shot migration from legacy preferences.json -> identity.json.
        var legacyRoot = Path.Combine(temp, "legacy-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacyRoot);
        var legacyPref = new Preferences(Account: "legacy-user", RememberAccount: true,
            AntiCheatConsentVersion: 7, Diagnostics: new DiagnosticOptions(true, "full"));
        File.WriteAllBytes(Path.Combine(legacyRoot, "preferences.json"),
            JsonSerializer.SerializeToUtf8Bytes(legacyPref, WireJson.Default.Preferences));
        File.WriteAllText(Path.Combine(legacyRoot, "bootstrap.json"), "stale metadata");
        var migrating = new LocalStateStore(legacyRoot);
        migrating.Load();
        var didMigrate = migrating.MigrateFromLegacy(legacyRoot, () => null);
        check(didMigrate, "legacy migration runs once");
        check(migrating.Identity.Account == "legacy-user" && migrating.Identity.Consent?.AntiCheat == 7,
            "migration converts account and consent");
        check(!migrating.MigrateFromLegacy(legacyRoot, () => null), "migration is idempotent");
        check(!File.Exists(Path.Combine(legacyRoot, "preferences.json")) && !File.Exists(Path.Combine(legacyRoot, "bootstrap.json")),
            "legacy config files removed after migration");
        check(Directory.GetDirectories(legacyRoot).Any(d => Path.GetFileName(d).StartsWith("legacy-backup-")),
            "legacy backup kept before removal");
    }
}
