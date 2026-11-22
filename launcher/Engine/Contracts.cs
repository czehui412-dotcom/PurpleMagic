using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ServerS5A3;
using Dnf90.Launcher.Diagnostics;

namespace Dnf90.Launcher;

public sealed record News(string Title, string Body, string Category, string Date)
{ public string Format { get; init; } = "text"; }
public sealed record BundleRelease(string Version, string Url, long Size, string Sha256)
{
    public string BuildFlavor { get; set; } = "standard";
}
// Unified game-resource artifact. Delivery.Strategy is "archive" or "blob";
// per-file SHA256 is always authoritative, transport digests are integrity only.
public sealed record ArtifactFile(string Path, long Length, string Sha256, string BlobSha256 = "", long BlobLength = 0, string Compression = "");
public sealed record GameDelivery(string Strategy, string Endpoint = "", string CertificateSha256 = "", string[]? URLs = null, string PackageSha256 = "", long PackageSize = 0);
public sealed record GameArtifact(string Version, string SourceId, long Sequence, ArtifactFile[] Files, GameDelivery Delivery);
// format=2 only. Game is the sole game-resource authority; the legacy Content
// and Archive fields and the format=1 compatibility path were removed.
public sealed record Bootstrap(string Product, int Format, long Sequence, PlayerProfile Profile, News[] News, BundleRelease? Launcher, GameArtifact Game);
// UI view projected from gateway-signed policy + identity.json. It is never a
// source of authority and is never persisted (see 登录器架构重构设计.md).
public sealed record Preferences(string Account = "", bool RememberAccount = true, bool AutoUpdate = true, bool ReducedEffects = false, DiagnosticOptions? Diagnostics = null, int AntiCheatConsentVersion = 0, string ClientDirectory = "");

// Local persistent state: identity.json (account + consent + optional manual
// client-directory override) and anchor.json (anti-rollback + failure guard).
// Manual directory is a user override of the auto-detected launch directory.
public sealed record ConsentState(int AntiCheat = 0, int Diagnostics = 0, string DiagnosticsProblemType = "auto");
public sealed record IdentityState(int V = 1, string Account = "", bool RememberAccount = true, ConsentState? Consent = null, string ClientDirectory = "");
public sealed record UpdateAttempt(string Target = "", int Failures = 0, string LastFailureUtc = "");
public sealed record AnchorState(int V = 1, long Sequence = 0, long GameSourceSequence = 0, UpdateAttempt? Attempt = null);
public sealed record State(string Phase, string Message, double Progress, long Downloaded, long Total, bool CanLogin, bool HasUpdate, bool HasLauncherUpdate, string Source, string Version, Preferences Preferences, News[] News)
{
    public string LauncherVersion => LauncherEngine.VersionText;
    public string ClientVersion { get; init; } = "";
    public string LauncherHome { get; init; } = "";
    public string BuildFlavor => LauncherBuild.Flavor;
    public bool AntiCheatEnabled { get; init; }
    public string MaintenanceUrl { get; init; } = "";
    public string MaintenanceCertSha { get; init; } = "";
    public bool QQBindingEnabled { get; init; }
    public bool RequireQQBinding { get; init; }
    public DiagnosticStatus? Diagnostics { get; init; }
}
public sealed record Request(int Id, string Method, JsonElement Args);
public sealed record Response(int Id, bool Ok, string? Error = null, State? State = null, bool Restart = false, ConfigurationReset? ConfigurationReset = null, DiagnosticExport? DiagnosticExport = null, LoginReply? AccountResult = null, string? ErrorCode = null, long RetryAfter = 0, string? DiagnosticIncidentId = null, string? DiagnosticEntryId = null, ContractObservationItem? DiagnosticObservation = null, CdkReply? CdkResult = null, CdkCharactersReply? CdkCharacters = null, int KilledGames = 0);
public sealed record ConfigurationReset(bool Reset, string? BackupPath);
public sealed record Event(string Type, State State);
public sealed record Credentials(string Account, string Password, string? Invite = null);
public sealed record PasswordChange(string Account, string Password, string NewPassword);
public sealed record CdkInput(string Account, string Password, string Code, string CharacterId);
public sealed record TransactionEntry(string Path, bool HadOriginal);
public sealed record TransactionJournal(string Phase, TransactionEntry[] Entries, string? PreviousReceipt);
public sealed record BundleJob(string InstallRoot, string Package, string Envelope, int UiPid, long UiStart, int CorePid, long CoreStart, string Work, string Phase);
public sealed record BundleOutcome(string Version, bool Succeeded, string Reason)
{
    public string Stage { get; init; } = "";
    public int NativeError { get; init; }
    public string FailedAtUtc { get; init; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Bootstrap))]
[JsonSerializable(typeof(Preferences))]
[JsonSerializable(typeof(IdentityState))]
[JsonSerializable(typeof(ConsentState))]
[JsonSerializable(typeof(AnchorState))]
[JsonSerializable(typeof(UpdateAttempt))]
[JsonSerializable(typeof(GameArtifact))]
[JsonSerializable(typeof(GameDelivery))]
[JsonSerializable(typeof(ArtifactFile))]
[JsonSerializable(typeof(State))]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(Event))]
[JsonSerializable(typeof(Credentials))]
[JsonSerializable(typeof(PasswordChange))]
[JsonSerializable(typeof(CdkInput))]
[JsonSerializable(typeof(CdkReply))]
[JsonSerializable(typeof(CdkCharacter))]
[JsonSerializable(typeof(CdkCharactersReply))]
[JsonSerializable(typeof(LoginReply))]
[JsonSerializable(typeof(TransactionJournal))]
[JsonSerializable(typeof(BundleJob))]
[JsonSerializable(typeof(BundleOutcome))]
[JsonSerializable(typeof(ContractSessionStatus))]
[JsonSerializable(typeof(ContractObservationItem))]
[JsonSerializable(typeof(ContractCharacterInput))]
[JsonSerializable(typeof(ContractObservationInput))]
[JsonSerializable(typeof(ContractIncidentInput))]
[JsonSerializable(typeof(ContractEntryInput))]
[JsonSerializable(typeof(ContractBundleExportInput))]
public partial class WireJson : JsonSerializerContext { }

public static class Trust
{
    public const int MaxMetadata = 8 * 1024 * 1024;
    public static bool Hash(string? hash) => hash is not null && Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$");
    public static string Digest(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
    public static Bootstrap Verify(byte[] envelope, PlayerProfile root)
    {
        if (envelope.Length > MaxMetadata) throw new InvalidDataException("发布清单超过限制。");
        var e = JsonSerializer.Deserialize(envelope, CoreJson.Default.SignedEnvelope) ?? throw new InvalidDataException("发布清单无效。");
        var raw = Convert.FromBase64String(e.Payload);
        using var rsa = RSA.Create(); rsa.ImportFromPem(root.UpdatePublicKey);
        if (!rsa.VerifyData(raw, Convert.FromBase64String(e.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new InvalidDataException("本服发布签名不匹配。");
        var b = JsonSerializer.Deserialize(raw, WireJson.Default.Bootstrap) ?? throw new InvalidDataException("发布清单无效。");
        if (b.Product != "DNF90-Flutter" || b.Format != 2 || b.Sequence < 1 || b.Profile is null || b.News is null || b.News.Length > 30 || b.Game is null)
            throw new InvalidDataException("发布产品、格式或序号无效。");
        // Download source changes never redirect account credentials or replace trust roots.
        if (b.Profile.ApiUrl.TrimEnd('/') != root.ApiUrl.TrimEnd('/') || b.Profile.UpdatePublicKey != root.UpdatePublicKey ||
            !Hash(b.Profile.CertificateSha256) || !Hash(b.Profile.ExeSha256) || !Hash(b.Profile.PvfSha256)) throw new InvalidDataException("账号入口或信任根发生不允许的变化。");
        _ = GameContract.Arguments("de509f65000000000000000000000000", b.Profile.GameHost, b.Profile.BootstrapPort);
        ChannelProbe.ValidateConfiguration(b.Profile);
        b.Profile.AntiCheat?.Validate();
        if (!string.IsNullOrEmpty(b.Profile.MinimumLauncherVersion) &&
            (!Regex.IsMatch(b.Profile.MinimumLauncherVersion, @"^\d+\.\d+\.\d+$") || !Version.TryParse(b.Profile.MinimumLauncherVersion, out _)))
            throw new InvalidDataException("最低登录器版本无效。");
        if (b.Profile.ChannelHistory is { Adapter.Length: > 0 } history &&
            !Regex.IsMatch(history.Adapter, "^[a-z0-9-]{1,64}$")) throw new InvalidDataException("频道兼容方案标识无效。");
        if (b.Profile.SecondClient is { } secondClient && !secondClient.IsWellFormed()) throw new InvalidDataException("双开兼容配置无效。");
        if (b.Game.Delivery is null || b.Game.Files is null || b.Game.Files.Length is < 1 or > 2000)
            throw new InvalidDataException("游戏资源制品信息无效。");
        var strategy = b.Game.Delivery.Strategy;
        if (strategy is not ("archive" or "blob")) throw new InvalidDataException("游戏资源投递策略无效。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in b.Game.Files)
        {
            SafeFiles.ValidateRelative(file.Path);
            var allowed = file.Path.Equals("DNF.exe", StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals("Script.pvf", StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals("dstr.dat", StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals("CombatPower.dll", StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals("DpsMeter.dll", StringComparison.OrdinalIgnoreCase) ||
                (file.Path.StartsWith("ImagePacks2/", StringComparison.OrdinalIgnoreCase) && file.Path.EndsWith(".npk", StringComparison.OrdinalIgnoreCase));
            if (!allowed || !seen.Add(file.Path) || !Hash(file.Sha256) || file.Length is < 0 or > 8L * 1024 * 1024 * 1024 || file.Compression is not ("" or "none" or "gzip"))
                throw new InvalidDataException("游戏资源制品文件清单无效。");
            if (strategy == "blob" && file.Compression == "gzip" && (!Hash(file.BlobSha256) || file.BlobLength is < 1 or > 8L * 1024 * 1024 * 1024))
                throw new InvalidDataException("游戏资源压缩信息无效。");
        }
        foreach (var (name, hash) in new[] { ("DNF.exe", b.Profile.ExeSha256), ("Script.pvf", b.Profile.PvfSha256) })
            if (!b.Game.Files.Any(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase) && f.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("游戏资源制品与本服 EXE/PVF 指纹不一致。");
        var d = b.Game.Delivery;
        if (strategy == "archive")
        {
            var urls = d.URLs ?? [];
            if (urls.Length != 2 || !ValidArchiveUrl(urls[0]) || !ValidArchiveUrl(urls[1]) || (urls[0] != "" && urls[0] == urls[1]))
                throw new InvalidDataException("游戏资源直链地址无效。");
            if (!Hash(d.PackageSha256) || d.PackageSize is < 1 or > 8L * 1024 * 1024 * 1024)
                throw new InvalidDataException("游戏资源包摘要或大小无效。");
        }
        else
        {
            SignedUpdate.RequireHttps(d.Endpoint);
            if (new Uri(d.Endpoint).AbsolutePath != "/" || (d.CertificateSha256 != "" && !Hash(d.CertificateSha256)))
                throw new InvalidDataException("游戏资源分文件源无效。");
        }
        if (b.Launcher is { } bundle)
        {
            SignedUpdate.RequireHttps(bundle.Url);
            if (!Version.TryParse(bundle.Version, out _) || !Hash(bundle.Sha256) || bundle.Size is < 1 or > SignedUpdate.MaxPackageBytes || bundle.BuildFlavor is not ("standard" or "anticheat")) throw new InvalidDataException("登录器发布信息无效。");
        }
        return b;
    }
    private static bool ValidArchiveUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == "http" || uri.Scheme == "https") && !string.IsNullOrEmpty(uri.Host) &&
               string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
    }
    public static void CheckSequence(Bootstrap next, byte[] nextRaw, Bootstrap previous, byte[] previousRaw)
    {
        // Envelope signature can be randomized; compare exact signed payload bytes.
        static string Payload(byte[] raw) => JsonSerializer.Deserialize(raw, CoreJson.Default.SignedEnvelope)!.Payload;
        if (next.Sequence < previous.Sequence || next.Sequence == previous.Sequence && Payload(nextRaw) != Payload(previousRaw)) throw new InvalidDataException("发布序号倒退或同序号内容发生变化。");
        if (next.Game.Sequence < previous.Game.Sequence) throw new InvalidDataException("游戏资源版本倒退。");
    }
}
