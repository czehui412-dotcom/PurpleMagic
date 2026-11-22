using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ServerS5A3;
using Dnf90.Launcher.Diagnostics;
#if ANTICHEAT
using Dnf90.Launcher.AntiCheat;
#endif

namespace Dnf90.Launcher;

public sealed class LauncherEngine : IDisposable
{
    public const string VersionText = "1.0.37";
    public const string ContentCapabilitiesHeader = "X-DNF90-Content-Capabilities";
    public const string ContentCapabilities = "native-plugins-v1";
    private readonly PlayerProfile root;
    private readonly string stateRoot;
    private string home;
    private readonly string? previousUpdateFailure;
    private readonly BundleOutcome? previousUpdateOutcome;
    private readonly SemaphoreSlim operation = new(1, 1);
    private CancellationTokenSource? download;
    private Bootstrap? bootstrap;
    private byte[]? envelope;
    private UpdateEngine.Change[] changes = [];
    private string clientVersion = "";
    private readonly List<(string Account, Process Process)> games = [];
    private readonly ProcessSupervisor processes = new();
#if ANTICHEAT
    private readonly List<AntiCheatSession> protectedGames = [];
    private readonly object guardLock = new();
    private readonly CancellationTokenSource guardLifetime = new();
    private bool guardClosing;
#endif
    private readonly DiagnosticService diagnostics;
    private readonly LocalStateStore localState;
    private readonly MaintenanceStream? maintenanceStream;
    private bool maintenanceActive;
    private string maintenanceMessage = "";
    public event Action<State>? Changed;
    private State state = new("ready", "正在读取本机状态…", 0, 0, 0, false, false, false, "本服批准源", "", new(), []);
    public State State { get => state with { Diagnostics = diagnostics?.Status }; private set => state = value; }
    private PlayerProfile Profile => bootstrap?.Profile ?? root;

    public LauncherEngine(PlayerProfile trusted, string dataDirectory)
    {
        root = trusted; stateRoot = dataDirectory; SafeFiles.NoLinks(stateRoot); Directory.CreateDirectory(stateRoot);
        root.AntiCheat?.Validate();
        localState = new LocalStateStore(stateRoot);
        localState.Load();
        home = ResolveLauncherHome(localState.Identity.ClientDirectory);
        var install = Path.GetDirectoryName(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar))!;
        previousUpdateFailure = BundleInstaller.PreviousFailure(install);
        previousUpdateOutcome = BundleInstaller.ReadOutcome(install);
        diagnostics = new(stateRoot);
        diagnostics.Changed += () => Changed?.Invoke(State);
        RecycleStalePortablePayloads();
        RefreshClientVersion();
        Emit("ready", previousUpdateFailure ?? "客户端已就绪。");
        // Gateway pushes maintenance changes over SSE; no client-side polling.
        maintenanceStream = new MaintenanceStream(root.ApiUrl, root.CertificateSha256, (active, message) => ApplyMaintenance(active, message));
    }

    // A user-picked directory (persisted in identity.json) overrides the
    // auto-detected launch directory. Absent/invalid manual choice falls back
    // to the entry EXE directory, which is the normal case: the launcher lives
    // next to DNF.exe. See 登录器架构重构设计.md §5.1.
    private static string ResolveLauncherHome(string manual)
    {
        if (!string.IsNullOrWhiteSpace(manual) && Path.IsPathFullyQualified(manual) && Directory.Exists(manual) &&
            File.Exists(Path.Combine(manual, "DNF.exe")) && File.Exists(Path.Combine(manual, "Script.pvf")))
        { SafeFiles.NoLinks(manual); return Path.GetFullPath(manual); }
        var entry = Environment.GetEnvironmentVariable("DNF90_LAUNCH_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(entry) && Path.IsPathFullyQualified(entry) && Directory.Exists(entry))
        { SafeFiles.NoLinks(entry); return Path.GetFullPath(entry); }
        var install = Path.GetDirectoryName(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar))!;
        for (var dir = install; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "DNF.exe")) && File.Exists(Path.Combine(dir, "Script.pvf")))
            { SafeFiles.NoLinks(dir); return dir; }
        return install;
    }

    private void Emit(string phase, string message, long current = 0, long total = 0)
    {
        var busy = phase is "checking" or "downloading" or "installing" or "logging-in" or "resetting";
        var policy = Profile;
        var view = new Preferences(
            localState.Identity.RememberAccount ? localState.Identity.Account : "",
            localState.Identity.RememberAccount,
            policy.UpdatePolicy != "off",
            policy.ReducedEffects,
            new DiagnosticOptions
            {
                Enabled = policy.DiagnosticsPolicy != "off",
                ConsentVersion = localState.Identity.Consent?.Diagnostics is > 0 ? DiagnosticOptions.CurrentConsent : 0,
                ProblemType = string.IsNullOrEmpty(localState.Identity.Consent?.DiagnosticsProblemType) ? "auto" : localState.Identity.Consent!.DiagnosticsProblemType
            },
            localState.Identity.Consent?.AntiCheat ?? 0,
            home);
        State = new(phase, message, total == 0 ? 0 : Math.Clamp((double)current / total, 0, 1), current, total,
            !busy && phase != "paused" && changes.Length == 0, changes.Length != 0,
            bootstrap?.Launcher is { } b && b.BuildFlavor == LauncherBuild.Flavor && Version.Parse(b.Version) > Version.Parse(VersionText),
            bootstrap?.Game.SourceId ?? "本服批准源", bootstrap?.Game.Version ?? "", view, bootstrap?.News ?? [])
        {
            ClientVersion = clientVersion,
            LauncherHome = home,
            MaintenanceUrl = MaintenanceStatusUrl(root.ApiUrl),
            MaintenanceActive = maintenanceActive,
            MaintenanceMessage = maintenanceMessage,
            QQBindingEnabled = root.EnableQQBinding,
            RequireQQBinding = Profile.RequireQQBinding,
            AntiCheatEnabled = LauncherBuild.AntiCheat && Profile.AntiCheat is { Enabled: true }
        };
        Changed?.Invoke(State);
    }
    private void RefreshClientVersion() => clientVersion = InstalledClientVersion.Read(home, bootstrap);
    // 维护状态由 nginx edge 托管，与 bootstrap 同源。取 API URL 的 origin + /status/maintenance.json。
    internal static string MaintenanceStatusUrl(string apiUrl)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) return "";
        try
        {
            var uri = new Uri(apiUrl);
            // edge 在 443，ApiUrl 指向网关 8443；用 host（不含端口）+ 默认 443。
            var scheme = uri.Scheme == "http" ? "http" : "https";
            return $"{scheme}://{uri.Host}/status/maintenance.json";
        }
        catch { return ""; }
    }
    // 由 Flutter 定时通过 RPC 调用：读取签名信任根认可的 edge 静态维护状态；
    // 证书校验复用下载器的信任根逻辑（root.CertificateSha256）。维护开启时
    // 关闭本登录器启动的游戏进程，释放 Script.pvf 文件句柄以便替换新 PVF。
    public async Task<MaintenanceReply> CheckMaintenance()
    {
        var url = MaintenanceStatusUrl(root.ApiUrl);
        if (string.IsNullOrEmpty(url)) return new(false, "");
        try
        {
            // 不能用 CreateHandler：其 CertificateAllowed 要求 uri.Port == ApiUrl.Port(8443)，
            // 而维护状态在 edge 443，端口不等会退化成系统 CA 校验 → 自签名证书失败 →
            // 异常被吞、永远 active:false（服务端踢人成功但客户端不退出、不显示维护）。
            // 这里直接 pin 到 profile 信任根，只校验 host + 证书 SHA-256，不比端口。
            var uri = new Uri(url);
            using var handler = new HttpClientHandler
            {
                UseProxy = false, AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (req, cert, _, errors) =>
                    req.RequestUri is { } r && cert is not null && r.Host == uri.Host
                    && errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == false
                    && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.RawData))
                        .Equals(root.CertificateSha256, StringComparison.OrdinalIgnoreCase)
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return new(false, "");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var el = doc.RootElement;
            var active = el.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            var msg = el.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "") : "";
            var killed = ApplyMaintenance(active, msg);
            return new(active, msg, killed);
        }
        catch { return new(false, ""); }
    }
    private bool maintenanceKilled;
    // Applies a maintenance state from the gateway SSE push (primary) or the
    // one-shot file check (fallback). Game kill is idempotent via maintenanceKilled.
    private int ApplyMaintenance(bool active, string message)
    {
        var killed = 0;
        if (active && !maintenanceKilled)
        {
            killed = KillGamesForMaintenance();
            maintenanceKilled = true;
        }
        else if (!active) maintenanceKilled = false;
        maintenanceActive = active;
        maintenanceMessage = active && !string.IsNullOrWhiteSpace(message) ? message : "";
        State = State with { MaintenanceActive = maintenanceActive, MaintenanceMessage = maintenanceMessage };
        Changed?.Invoke(State);
        return killed;
    }
    public void Pause() => download?.Cancel();
    internal static bool SupportsContent(PlayerProfile profile) =>
        (string.IsNullOrEmpty(profile.MinimumLauncherVersion) || Version.Parse(VersionText) >= Version.Parse(profile.MinimumLauncherVersion)) &&
        NativeChannelHistory.SupportsProfile(profile);
    internal static void RequireContentSupport(PlayerProfile profile)
    {
        if (!SupportsContent(profile)) throw new InvalidOperationException("请先更新登录器，再安装资源或登录游戏。");
    }
    public void ReportHealthy(string path)
    {
        var install = Path.GetDirectoryName(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar))!;
        var full = Path.GetFullPath(path); var work = Path.GetDirectoryName(full)!;
        if (Path.GetFileName(full) != "ready" || Path.GetDirectoryName(work) != Path.Combine(install, ".updates") || !Guid.TryParseExact(Path.GetFileName(work), "N", out _)) throw new InvalidDataException("就绪回执路径无效。");
        SafeFiles.NoLinks(full);
        var job = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(install, ".launcher-update.json")), WireJson.Default.BundleJob);
        if (job?.Work != work || job.Phase != "candidate-installed") throw new InvalidDataException("就绪回执与更新事务不匹配。");
        SafeFiles.Atomic(full, "ready"u8.ToArray());
    }

    private UpdateEngine NewEngine() => new(home, stateRoot, Profile, diagnostics,
        (phase, message, current, total) => Emit(phase, message, current, total),
        new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
        new LauncherBundleDownloader(Profile));

    public async Task<bool> UpdateLauncher(int uiPid)
    {
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在进行。");
        try { return await UpdateLauncherCore(uiPid); }
        catch (OperationCanceledException) { Emit("ready", "登录器更新已取消，可手动重试。"); return false; }
        catch { Emit("error", "登录器更新未完成，请查看提示。"); throw; }
        finally { download = null; operation.Release(); }
    }
    private async Task<bool> UpdateLauncherCore(int uiPid)
    {
        RequireNoGames();
        var release = bootstrap?.Launcher ?? throw new InvalidOperationException("尚未发布登录器更新。");
        LauncherBuild.RequireFlavor(release.BuildFlavor);
        if (Version.Parse(release.Version) <= Version.Parse(VersionText)) throw new InvalidOperationException("当前登录器已是最新版本。");
        var app = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var install = Path.GetDirectoryName(app)!; BundleInstaller.ValidateRoot(install);
        if (Path.GetFileName(app) != "app") throw new InvalidDataException("请从完整安装目录启动登录器。");
        using var ui = Process.GetProcessById(uiPid);
        if (!Path.GetFullPath(ui.MainModule!.FileName).Equals(Path.Combine(app, "DNF90.exe"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("登录器父进程不匹配。");
        // BundleInstaller.ValidateJob（及 ReportHealthy）要求 work 位于 install\.updates\<guid> 下；
        // 放在 stateRoot\tmp\bundle 会导致 Apply 抛「更新事务目录无效」且跳过失败恢复。
        var work = Path.Combine(install, ".updates", Guid.NewGuid().ToString("N")); SafeFiles.NoLinks(work); Directory.CreateDirectory(work);
        var package = Path.Combine(work, "bundle.zip");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30)); download = timeout;
        Emit("downloading", "正在下载登录器完整更新包…", 0, release.Size);
        using var delivery = new LauncherBundleDownloader(root);
        var cachedPackage = await delivery.Download(release, Path.Combine(stateRoot, "tmp", "downloads"), count => Emit("downloading", "正在下载登录器完整更新包…", count, release.Size), timeout.Token);
        File.Copy(cachedPackage, package, false);
        download = null;
        RequireNoGames();
        using var own = Process.GetCurrentProcess();
        // GetProcessById/GetCurrentProcess are attached objects: their .StartTime
        // can throw "Process was not started by this object". Use kernel handles.
        var uiStarted = BundleProcesses.CreatedTicks(ui.Id);
        var ownStarted = BundleProcesses.CreatedTicks(own.Id);
        var job = new BundleJob(install, package, Convert.ToBase64String(envelope!), ui.Id, uiStarted, own.Id, ownStarted, work, "downloaded");
        var jobFile = Path.Combine(work, "job.json"); SafeFiles.Atomic(jobFile, JsonSerializer.SerializeToUtf8Bytes(job, WireJson.Default.BundleJob));
        var helper = Path.Combine(install, "update-helper.exe"); SafeFiles.NoLinks(helper); File.Copy(Environment.ProcessPath!, helper, true);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = install };
        start.ArgumentList.Add("--apply-bundle"); start.ArgumentList.Add(jobFile);
        using var helperProcess = Process.Start(start) ?? throw new InvalidOperationException("更新助手启动失败。");
        Emit("installing", "登录器更新已准备完成，正在重启…");
        return true;
    }

    // Unified automatic update (§7.2). Fetch signed state → Diff → guard → run.
    public async Task<bool> AutomaticUpdate(int uiPid)
    {
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在进行。");
        try
        {
            if (Profile.UpdatePolicy == "off") { Emit("ready", "本服已关闭自动更新。"); return false; }
            RequireNoGames();
            await RefreshMetadata();
            if (State.HasLauncherUpdate)
            {
                if (BundleInstaller.SuppressAutomaticRetry(previousUpdateOutcome, bootstrap!.Launcher!.Version)) { Emit("ready", previousUpdateFailure!); return false; }
                return await UpdateLauncherCore(uiPid);
            }
            await RefreshChanges();
            if (changes.Length == 0) { Emit("ready", "客户端资源已是本服批准版本。"); return false; }
            var target = UpdateEngine.TargetId(bootstrap!.Game);
            if (localState.IsThrottled(target))
            { Emit("ready", "游戏资源更新多次失败，已暂停自动重试；请手动重试或联系服主。"); return false; }
            var plan = new UpdateEngine.Plan(changes.Select(c => bootstrap.Game.Files.First(f => f.Path == c.Path)).ToArray(), bootstrap.Game.Delivery.Strategy, target);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30)); download = cancel;
            bool ok;
            try { ok = await NewEngine().Run(plan, bootstrap.Game, cancel.Token); }
            finally { download = null; }
            if (ok) { localState.ResetFailures(); clientVersion = ""; await RefreshChanges(); Emit("ready", "游戏资源更新完成。"); }
            else { localState.RecordFailure(target); Emit("error", "游戏资源更新未完成，可手动重试。"); }
            return false;
        }
        catch (OperationCanceledException) { Emit("ready", "自动更新已暂停，可手动重试。"); return false; }
        catch (Exception error) { diagnostics.Failure("auto_update", error); Emit("error", "自动更新未完成：" + error.Message); throw; }
        finally { download = null; operation.Release(); }
    }

    public async Task SavePreferences(Preferences value)
    {
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("当前操作完成后再修改设置。");
        try
        {
            // Only identity + an explicit client-directory choice are persisted;
            // auto-update/effects/diagnostics enabled are gateway policy (§5.4).
            localState.SetAccount(value.Account, value.RememberAccount);
            localState.SetConsent(value.AntiCheatConsentVersion, value.Diagnostics?.HasConsent == true ? 1 : 0, value.Diagnostics?.ProblemType ?? "auto");
            var requested = value.ClientDirectory?.Trim() ?? "";
            if (requested.Length != 0)
            {
                if (!Path.IsPathFullyQualified(requested) || !Directory.Exists(requested) ||
                    !File.Exists(Path.Combine(requested, "DNF.exe")) || !File.Exists(Path.Combine(requested, "Script.pvf")))
                    throw new InvalidOperationException("所选目录不包含 DNF.exe 和 Script.pvf。");
                SafeFiles.NoLinks(requested);
                var next = Path.GetFullPath(requested);
                localState.SetClientDirectory(next);
                if (!string.Equals(next, home, StringComparison.OrdinalIgnoreCase))
                { home = next; RefreshClientVersion(); }
            }
            Emit("ready", "设置已保存。");
        }
        finally { operation.Release(); }
    }

    public Task<ConfigurationReset> ResetGameConfiguration(bool confirmed) => ResetGameConfiguration(confirmed, () => GameConfiguration.Reset(confirmed));
    internal async Task<ConfigurationReset> ResetGameConfiguration(bool confirmed, Func<ConfigurationReset> reset)
    {
        if (!confirmed) throw new InvalidOperationException("请先确认重置本机游戏配置。");
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("当前操作完成后再重置游戏配置。");
        try
        {
            Emit("resetting", "正在备份并重置本机游戏配置…");
            var result = reset();
            Emit("ready", result.Reset ? "本机游戏配置已备份并重置，请重新登录。" : "未发现本机游戏配置，无需重置。");
            return result;
        }
        catch { Emit("error", "游戏配置未重置，请查看提示。"); throw; }
        finally { operation.Release(); }
    }

    private async Task RefreshChanges()
    {
        if (bootstrap is null) { changes = []; return; }
        changes = await NewEngine().Diff(bootstrap.Game, CancellationToken.None);
    }

    public async Task Check()
    {
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在进行。");
        try
        {
            localState.ResetFailures();
            await RefreshMetadata();
            await RefreshChanges();
            Emit("ready", changes.Length == 0 ? "客户端资源与本服批准版本一致。" : $"发现 {changes.Length} 个待更新文件，请点击安装更新。");
        }
        catch { Emit("error", "检查未完成，可重试或查看错误提示。"); throw; }
        finally { operation.Release(); }
    }

    private void RecycleStalePortablePayloads()
    {
        try
        {
            var app = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetFileName(app), "app", StringComparison.OrdinalIgnoreCase)) return;
            var install = Path.GetDirectoryName(app);
            var cacheRoot = install is null ? null : Path.GetDirectoryName(install);
            if (install is null || cacheRoot is null) return;
            var current = Path.GetFileName(install);
            if (!IsSha256Name(current)) return;
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(cacheRoot) ?? ""), "portable", StringComparison.OrdinalIgnoreCase)) return;
            SafeFiles.NoLinks(cacheRoot);
            foreach (var dir in Directory.EnumerateDirectories(cacheRoot))
            {
                var name = Path.GetFileName(dir);
                if (!IsSha256Name(name) || string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            foreach (var file in Directory.EnumerateFiles(cacheRoot, ".*.prepare.lock"))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception error) { diagnostics.Failure("portable_recycle_error", error); }
    }
    private static bool IsSha256Name(string name) =>
        name.Length == 64 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task RefreshMetadata()
    {
        var metadataClock = Stopwatch.StartNew();
        Emit("checking", "正在读取本服签名发布信息…");
        using var api = new PlayerApi(root);
        api.Http.Timeout = TimeSpan.FromSeconds(90);
        using var metadataTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var traceId = "http-" + Guid.NewGuid().ToString("N")[..16];
        using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, root.ApiUrl.TrimEnd('/') + LauncherBuild.ChannelPath + "bootstrap.json");
        metadataRequest.Headers.TryAddWithoutValidation("X-PurpleMagic-Trace", traceId);
        metadataRequest.Headers.TryAddWithoutValidation(ContentCapabilitiesHeader, ContentCapabilities);
        using var response = await api.Http.SendAsync(metadataRequest, HttpCompletionOption.ResponseHeadersRead, metadataTimeout.Token);
        var serverDate = response.Headers.Date?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        diagnostics.RecordHttpTrace(new(traceId, "bootstrap_metadata", (int)response.StatusCode, Math.Round(metadataClock.Elapsed.TotalMilliseconds, 3), DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), Stopwatch.GetTimestamp(), serverDate));
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(metadataTimeout.Token);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, metadataTimeout.Token)) != 0) { if (output.Length + read > Trust.MaxMetadata) throw new InvalidDataException("发布数据超过限制。"); output.Write(buffer, 0, read); }
        var raw = output.ToArray(); var next = Trust.Verify(raw, root);
        if (bootstrap is not null && envelope is not null) Trust.CheckSequence(next, raw, bootstrap, envelope);
        if (localState.Anchor.Sequence > 0 && !localState.AntiRollbackAccept(next)) throw new InvalidDataException("发布版本出现回滚，已拒绝。");
        bootstrap = next; envelope = raw;
        localState.AdvanceAnchor(next);
        diagnostics.Configure(State.Preferences.Diagnostics ?? new(), home, Profile.PvfSha256, bootstrap.Sequence, Profile, bootstrap.Game.Version, bootstrap.Game.SourceId);
        diagnostics.Event("metadata_verified", metadataClock.Elapsed.TotalMilliseconds, bootstrap.Sequence);
        Emit("checking", "发布信息已验证。");
    }

    public async Task UpdateContent()
    {
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在进行。");
        try
        {
            RequireNoGames(); await RefreshMetadata(); await RefreshChanges();
            if (changes.Length == 0) { Emit("ready", "客户端资源已与本服批准版本一致。"); return; }
            var target = UpdateEngine.TargetId(bootstrap!.Game);
            if (localState.IsThrottled(target)) { Emit("ready", "游戏资源更新多次失败，请手动重试或联系服主。"); return; }
            var plan = new UpdateEngine.Plan(changes.Select(c => bootstrap.Game.Files.First(f => f.Path == c.Path)).ToArray(), bootstrap.Game.Delivery.Strategy, target);
            using var cancel = new CancellationTokenSource(TimeSpan.FromHours(6)); download = cancel;
            var ok = await NewEngine().Run(plan, bootstrap.Game, cancel.Token);
            if (ok) { localState.ResetFailures(); clientVersion = ""; await RefreshChanges(); Emit("ready", "更新完成，客户端资源已通过摘要校验。"); }
            else localState.RecordFailure(target);
        }
        catch (OperationCanceledException) { Emit("paused", "下载已暂停，点击继续会保留已下载的断点。"); }
        catch (Exception error) { diagnostics.Failure("update_error", error); Emit("error", "更新未完成，已保留当前版本与可恢复记录。"); throw; }
        finally { download = null; operation.Release(); }
    }

    public async Task<LoginReply> AccountSecurity(string action, PasswordChange input)
    {
        if (action is not ("binding-code" or "password")) throw new ArgumentException(nameof(action));
        if (action is "binding-code" && !root.EnableQQBinding) throw new InvalidOperationException("此登录器未启用 QQ 绑定功能。");
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在进行。");
        try {
            ValidateCredentials(new(input.Account, input.Password));
            if (action == "password") ValidateCredentials(new(input.Account, input.NewPassword));
            using var api = new PlayerApi(Profile);
            return await api.Submit(action, input.Account.Trim(), input.Password, Device(), input.NewPassword);
        } finally { operation.Release(); }
    }
    public async Task<CdkReply> RedeemCdk(CdkInput input)
    {
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在进行。");
        try
        {
            ValidateCredentials(new(input.Account, input.Password));
            if (string.IsNullOrWhiteSpace(input.Code)) throw new InvalidOperationException("请输入兑换码。");
            using var api = new PlayerApi(Profile);
            var reply = await api.RedeemCdk(input.Account.Trim(), input.Password, input.Code.Trim(), input.CharacterId ?? "");
            if (!reply.Ok) throw new InvalidOperationException(reply.Message ?? "兑换失败。");
            return reply;
        }
        finally { operation.Release(); }
    }
    public async Task<CdkCharacter[]> CdkCharacters(CdkInput input)
    {
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在进行。");
        try
        {
            ValidateCredentials(new(input.Account, input.Password));
            using var api = new PlayerApi(Profile);
            return await api.ListCdkCharacters(input.Account.Trim(), input.Password);
        }
        finally { operation.Release(); }
    }
    public async Task Register(Credentials input)
    {
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在进行。");
        try { ValidateCredentials(input); Emit("logging-in", "正在注册本服账号…"); using var api = new PlayerApi(Profile); await api.Submit("register", input.Account.Trim(), input.Password, Device(), invite: input.Invite); Emit("ready", "注册成功，请登录。"); }
        catch { Emit("error", "注册未完成，请查看提示。"); throw; }
        finally { operation.Release(); }
    }
    public async Task Login(Credentials input)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!await operation.WaitAsync(0)) throw new InvalidOperationException("已有操作正在进行。");
#if ANTICHEAT
        AntiCheatSession? guard = null;
#endif
        try
        {
            diagnostics.Event("login_started");
            var stageClock = Stopwatch.StartNew();
            RequireContentSupport(Profile);
            ValidateCredentials(input); Emit("checking", "正在验证客户端与本服版本…");
            using var clientLock = new FileTransaction(home);
            if (File.Exists(Path.Combine(home, ".dnf90-update", "journal.json"))) RequireNoGames();
            clientLock.Recover();
            if (bootstrap is { } boot)
            {
                var pending = await NewEngine().Diff(boot.Game, CancellationToken.None);
                if (pending.Length != 0) throw new InvalidOperationException("请先完成客户端资源更新。");
            }
            await GameContract.VerifyClient(home, Profile);
            diagnostics.Event("client_verified", stageClock.Elapsed.TotalMilliseconds); stageClock.Restart();
            if (NativeGuildCompatibility.Prepare(home, Profile))
                Emit("checking", "公会兼容补丁已应用，原版客户端已备份。");
            games.RemoveAll(g => g.Process.HasExited);
            if (games.Any(g => g.Account.Equals(input.Account.Trim(), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("该账号已经启动，请使用另一个账号。");
            if (Profile.AntiCheat is { Enabled: true }) {
#if ANTICHEAT
                lock (guardLock) {
                    if (guardClosing) throw new InvalidOperationException("登录器已关闭。");
                    foreach (var finished in protectedGames.Where(g => g.Completed).ToArray()) { finished.Dispose(); protectedGames.Remove(finished); }
                }
                Emit("checking", "正在请求管理员授权以启动反作弊保护；退出登录器将结束本次游戏…");
                guard = await AntiCheatSession.Start(home, envelope, stateRoot, message => Emit("error", message), guardLifetime.Token);
                lock (guardLock) {
                    if (guardClosing) throw new InvalidOperationException("登录器已关闭。");
                    protectedGames.Add(guard);
                }
#else
                throw new InvalidOperationException("AC00：本服已要求反作弊保护，请使用 anticheat 发行包。");
#endif
            }
            var channels = await ChannelProbe.Check(Profile, m => Emit("checking", m));
            diagnostics.Event("channel_verified", stageClock.Elapsed.TotalMilliseconds, channels.ServerId); stageClock.Restart();
            Emit("logging-in", "正在验证账号…"); using var api = new PlayerApi(Profile, status => diagnostics.Event("account_http_response", number: status), trace => diagnostics.RecordHttpTrace(trace));
            var correlation = State.Preferences.Diagnostics is { Enabled: true, HasConsent: true } ? Guid.NewGuid().ToString("N") : null;
            var reply = await api.Submit("login", input.Account.Trim(), input.Password, Device(), diagnosticCorrelationId: correlation);
            if (correlation is not null) diagnostics.LoginContext(Profile, correlation, reply.Diagnostics);
            diagnostics.Event("account_verified", stageClock.Elapsed.TotalMilliseconds); stageClock.Restart();
            NativeChannelHistory.Prepare(Profile, channels);
            var executable = Path.Combine(home, "DNF.exe");
            var running = Process.GetProcessesByName("DNF");
            try
            {
                if (running.Length >= 2) throw new InvalidOperationException("最多同时运行两个游戏客户端。");
                if (running.Length == 1) NativeClientGate.PrepareSecondClient(running[0], executable, Profile);
            }
            finally { foreach (var process in running) process.Dispose(); }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = home, Arguments = GameContract.Arguments(reply.Token ?? "", Profile.GameHost, Profile.BootstrapPort) };
            Process game;
            string handoffMode;
#if ANTICHEAT
            handoffMode = guard is null ? "direct_child" : "anticheat_guard_child";
            game = guard is null ? Process.Start(start) ?? throw new InvalidOperationException("游戏进程创建失败。") : await guard.Launch(executable, start.Arguments);
#else
            handoffMode = "direct_child";
            game = Process.Start(start) ?? throw new InvalidOperationException("游戏进程创建失败。");
#endif
            games.Add((input.Account.Trim(), game));
            processes.Track(game);
            diagnostics.Track(game, executable, handoffMode);
            diagnostics.Event("process_started", stageClock.Elapsed.TotalMilliseconds);
            localState.SetAccount(input.Account, State.Preferences.RememberAccount);
            Emit("logging-in", "游戏进程已创建，正在等待客户端启动…");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await game.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            if (game.HasExited)
            {
                // game may be an anti-cheat attach (GetProcessById); its .ExitCode
                // throws "Process was not started by this object". Read via kernel handle.
                var code = BundleProcesses.ExitCodeByIdentity(game.Id);
                throw new InvalidOperationException($"客户端提前退出（代码 {code}），请等待约 20 秒后重试。");
            }
#if ANTICHEAT
            guard = null;
#endif
            Emit("ready", channels.Message + " 客户端正在运行。");
        }
        catch (Exception error) { diagnostics.Failure("login_error", error); Emit("error", "登录未完成，请查看提示。"); throw; }
        finally {
#if ANTICHEAT
            guard?.Dispose();
#endif
            operation.Release();
        }
    }
    private static void ValidateCredentials(Credentials input)
    { if (input.Account.Trim().Length is < 1 or > 64 || input.Password.Length is < 1 or > 64) throw new InvalidDataException("账号或密码长度无效。"); }
    private static string Device()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var identity = machine?.GetValue("MachineGuid") as string ?? Environment.MachineName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ServerS5A3:" + identity))).ToLowerInvariant();
    }
    public static void RequireNoGames()
    {
        var processes = Process.GetProcessesByName("DNF");
        try { if (processes.Length > 0) throw new InvalidOperationException("更新或恢复文件前请退出所有游戏客户端。"); }
        finally { foreach (var p in processes) p.Dispose(); }
    }
    public Task<DiagnosticExport> ExportDiagnostics(string sessionId, string target) => diagnostics.Export(sessionId, target);
    public Task ElevateDiagnostics(string sessionId) => diagnostics.Elevate(sessionId);
    public Task<string> SetDiagnosticIncident(ContractIncidentInput input) => diagnostics.SetIncident(input);
    public Task AssociateDiagnosticCharacter(ContractCharacterInput input) => diagnostics.AssociateCharacter(input);
    public Task<string> MarkDiagnosticEntry(ContractEntryInput input) => diagnostics.MarkEntry(input);
    public Task<ContractObservationItem> ReportDiagnosticObservation(ContractObservationInput input) => diagnostics.ReportObservation(input);
    public Task<DiagnosticExport> ExportDiagnosticBundle(ContractBundleExportInput input) => diagnostics.ExportBundle(input);
    public void RecordDiagnosticError(Exception error) => diagnostics.Failure("operation_error", error);
    public void StopProtectedGames() => StopProtectedGames(shuttingDown: true);
    // shuttingDown=false 仅用于维护踢人：结束当前保护会话以释放 DNF.exe/PVF 句柄，
    // 但不能置 guardClosing 或取消 guardLifetime——那是登录器退出的一次性闸门，
    // 置位后维护结束时所有后续登录都会误报「登录器已关闭。」。
    private void StopProtectedGames(bool shuttingDown)
    {
#if ANTICHEAT
        lock (guardLock)
        {
            if (shuttingDown) { guardClosing = true; guardLifetime.Cancel(); }
            foreach (var session in protectedGames) session.Dispose();
            protectedGames.Clear();
        }
#endif
    }
    // 维护模式：关闭本登录器启动的全部游戏进程（反作弊与普通路径都覆盖），
    // 以及残留的 DNF.exe，释放 Script.pvf 文件句柄以便替换新 PVF。
    public int KillGamesForMaintenance()
    {
        StopProtectedGames(shuttingDown: false);
        var killed = 0;
        // 走统一治理器：优雅关 + 内核句柄等待，确保句柄真正释放后才返回，
        // 否则紧接着登录会撞上残留 journal.json / 存活 DNF.exe 的误报。
        processes.ShutdownSync();
        lock (games) { killed = games.Count; games.Clear(); }
        // 兜底：清理不属于本登录器但残留的 DNF.exe（占用 PVF 文件句柄）。
        foreach (var process in Process.GetProcessesByName("DNF"))
        {
            try { process.Kill(entireProcessTree: true); killed++; }
            catch { /* already gone */ }
            finally { process.Dispose(); }
        }
        diagnostics.Event("maintenance_kill_games", killed);
        return killed;
    }
    public void Dispose() { maintenanceStream?.Dispose(); StopProtectedGames(); processes.ShutdownSync(); download?.Cancel(); diagnostics.Dispose(); operation.Dispose(); }
}
