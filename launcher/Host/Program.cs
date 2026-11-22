using System.Text.Json;
using ServerS5A3;
using Dnf90.Launcher;
using Dnf90.Launcher.Diagnostics;

if (args.Length > 0 && args[0] == "--diagnostic-observer") { Environment.Exit(await DiagnosticObserver.Run(args)); return; }

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

using var profileStream = typeof(Program).Assembly.GetManifestResourceStream("profile.json")!;
var profile = JsonSerializer.Deserialize(profileStream, CoreJson.Default.PlayerProfile)!;
if (args.Length == 1 && args[0] == "--build-flavor") {
    profile.AntiCheat?.Validate();
#if !ANTICHEAT
    if (profile.AntiCheat is { Enabled: true }) throw new InvalidDataException("Enabled AntiCheat policy requires --anticheat build.");
#endif
    Console.WriteLine(LauncherBuild.Flavor); return;
}
#if ANTICHEAT
if (args.Length > 0 && args[0] == "--anticheat-guard") { Environment.Exit(await Dnf90.Launcher.AntiCheat.AntiCheatGuard.Run(args, profile)); return; }
#endif
if (args.Length > 0 && args[0] == "--anticheat-guard") { Environment.Exit(2); return; }
if (args.Length == 2 && args[0] == "--apply-bundle")
{
    var job = JsonSerializer.Deserialize(File.ReadAllBytes(args[1]), WireJson.Default.BundleJob) ?? throw new InvalidDataException();
    Environment.Exit(await BundleInstaller.Apply(job, profile)); return;
}
if (args.Length == 2 && args[0] == "--recover-bundle") { BundleInstaller.Recover(Path.GetFullPath(args[1])); return; }
// No HTTP listener or credentials in command-line arguments. Flutter is the sole
// parent and sends bounded newline JSON through redirected process pipes.
var data = args.Length == 2 && args[0] == "--state-directory"
    ? Path.GetFullPath(args[1])
    : LaunchData.Resolve(LauncherBuild.AntiCheat);
SafeFiles.NoLinks(data);
Directory.CreateDirectory(data);
using var instance = new FileStream(Path.Combine(data, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
using var engine = new LauncherEngine(profile, data);
var output = new object();
void Respond(Response response) { lock (output) Console.WriteLine(JsonSerializer.Serialize(response, WireJson.Default.Response)); }
engine.Changed += state => { lock (output) Console.WriteLine(JsonSerializer.Serialize(new Event("state", state), WireJson.Default.Event)); };
var jobs = new List<Task>();
async Task Dispatch(Request request)
{
    try
    {
        switch (request.Method)
        {
            case "initialize": break;
            case "health": engine.ReportHealthy(request.Args.GetProperty("path").GetString()!); break;
            case "check": await engine.Check(); break;
            case "autoUpdate":
                var restart = await engine.AutomaticUpdate(request.Args.GetProperty("uiPid").GetInt32());
                Respond(new(request.Id, true, State: engine.State, Restart: restart)); return;
            case "update": await engine.UpdateContent(); break;
            case "checkMaintenance":
                var maint = await engine.CheckMaintenance();
                Respond(new(request.Id, true, State: engine.State, Maintenance: maint)); return;
            case "pause": engine.Pause(); break;
            case "preferences": await engine.SavePreferences(request.Args.Deserialize(WireJson.Default.Preferences) ?? throw new InvalidDataException()); break;
            case "register": await engine.Register(request.Args.Deserialize(WireJson.Default.Credentials) ?? throw new InvalidDataException()); break;
            case "redeemCdk":
                var cdkResult = await engine.RedeemCdk(request.Args.Deserialize(WireJson.Default.CdkInput) ?? throw new InvalidDataException());
                Respond(new(request.Id, true, State: engine.State, CdkResult: cdkResult)); return;
            case "cdkCharacters":
                var cdkCharacters = await engine.CdkCharacters(request.Args.Deserialize(WireJson.Default.CdkInput) ?? throw new InvalidDataException());
                Respond(new(request.Id, true, State: engine.State, CdkCharacters: new CdkCharactersReply(true, null, cdkCharacters))); return;
            case "login": await engine.Login(request.Args.Deserialize(WireJson.Default.Credentials) ?? throw new InvalidDataException()); break;
            case "bindingCode":
            case "changePassword":
                var accountResult = await engine.AccountSecurity(request.Method == "bindingCode" ? "binding-code" : "password", request.Args.Deserialize(WireJson.Default.PasswordChange) ?? throw new InvalidDataException());
                Respond(new(request.Id, true, State: engine.State, AccountResult: accountResult)); return;
            case "diagnosticExport":
                var export = await engine.ExportDiagnostics(request.Args.GetProperty("sessionId").GetString()!, request.Args.GetProperty("path").GetString()!);
                Respond(new(request.Id, true, State: engine.State, DiagnosticExport: export)); return;
            case "diagnosticExportBundle":
                var bundleExport = await engine.ExportDiagnosticBundle(request.Args.Deserialize(WireJson.Default.ContractBundleExportInput) ?? throw new InvalidDataException());
                Respond(new(request.Id, true, State: engine.State, DiagnosticExport: bundleExport)); return;
            case "diagnosticSetIncident":
                var incidentId = await engine.SetDiagnosticIncident(request.Args.Deserialize(WireJson.Default.ContractIncidentInput) ?? new());
                Respond(new(request.Id, true, State: engine.State, DiagnosticIncidentId: incidentId)); return;
            case "diagnosticAssociateCharacter":
                await engine.AssociateDiagnosticCharacter(request.Args.Deserialize(WireJson.Default.ContractCharacterInput) ?? throw new InvalidDataException());
                break;
            case "diagnosticMarkEntry":
                var entryId = await engine.MarkDiagnosticEntry(request.Args.Deserialize(WireJson.Default.ContractEntryInput) ?? throw new InvalidDataException());
                Respond(new(request.Id, true, State: engine.State, DiagnosticEntryId: entryId)); return;
            case "diagnosticReportObservation":
                var observation = await engine.ReportDiagnosticObservation(request.Args.Deserialize(WireJson.Default.ContractObservationInput) ?? throw new InvalidDataException());
                Respond(new(request.Id, true, State: engine.State, DiagnosticObservation: observation)); return;
            case "diagnosticElevate": await engine.ElevateDiagnostics(request.Args.GetProperty("sessionId").GetString()!); break;
            case "resetGameConfiguration":
                var confirmed = request.Args.TryGetProperty("confirmed", out var confirmation) && confirmation.ValueKind == JsonValueKind.True;
                var reset = await engine.ResetGameConfiguration(confirmed);
                Respond(new(request.Id, true, State: engine.State, ConfigurationReset: reset)); return;
            case "updateLauncher":
                var launcherRestart = await engine.UpdateLauncher(request.Args.GetProperty("uiPid").GetInt32());
                Respond(new(request.Id, true, State: engine.State, Restart: launcherRestart)); return;
            default: throw new InvalidDataException("未知登录器操作。");
        }
        Respond(new(request.Id, true, State: engine.State));
    }
    catch (Exception error)
    {
        engine.RecordDiagnosticError(error);
        var message = error switch {
            HttpRequestException => "无法连接服务器或证书验证失败。请检查网络和后台配置。",
            IOException when error is not InvalidDataException => "文件无法读写，请检查目录权限、剩余空间及游戏是否已退出。",
            InvalidDataException or InvalidOperationException => error.Message,
            OperationCanceledException => "操作已取消或超时。",
            _ => "操作未完成，请重试并联系管理员。"
        };
        Respond(new(request.Id, false, message, engine.State, ErrorCode: (error as AccountApiException)?.Code, RetryAfter: (error as AccountApiException)?.RetryAfter ?? 0));
    }
}
while (await Console.In.ReadLineAsync() is { } line)
{
    if (line.Length > 16384) { Respond(new(0, false, "请求过大。")); continue; }
    try
    {
        var request = JsonSerializer.Deserialize(line, WireJson.Default.Request) ?? throw new InvalidDataException();
        jobs.RemoveAll(t => t.IsCompleted); jobs.Add(Dispatch(request));
    }
    catch (Exception error) when (error is JsonException or InvalidDataException) { Respond(new(0, false, "请求格式无效。")); }
}
engine.Pause();
engine.StopProtectedGames();
await Task.WhenAll(jobs);

internal static class LaunchData
{
    // 绿色模式：便携入口通过 DNF90_LAUNCH_DIRECTORY 传递入口目录，核心数据
    // 跟随入口写入 <入口目录>\DNF90Launcher-Data\state[-anticheat]，更新登录器
    // 或更换数据盘时配置随目录保留。目录不可写或变量缺失时回退 AppData 布局。
    internal static string Resolve(bool anticheat)
    {
        var suffix = anticheat ? "-anticheat" : "";
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DNF90Launcher" + suffix);
        var entry = Environment.GetEnvironmentVariable("DNF90_LAUNCH_DIRECTORY");
        if (string.IsNullOrWhiteSpace(entry) || !Path.IsPathFullyQualified(entry) || !Directory.Exists(entry)) return fallback;
        string state;
        try
        {
            state = Path.Combine(entry, "DNF90Launcher-Data", "state" + suffix);
            SafeFiles.NoLinks(state);
            Directory.CreateDirectory(state);
            var probe = Path.Combine(state, ".write-probe");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.WriteByte(0);
            File.Delete(probe);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return fallback;
        }
        MigrateLegacy(fallback, state);
        return state;
    }

    // 一次性迁移：绿色目录为空时从既有 AppData 布局导入偏好设置；
    // 签名元数据、下载缓存与诊断记录不迁移，避免旧签名阻塞新目录。
    private static void MigrateLegacy(string legacy, string state)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(state).Any()) return;
            var source = Path.Combine(legacy, "preferences.json");
            SafeFiles.NoLinks(source);
            if (!File.Exists(source) || new FileInfo(source).Length > 65536) return;
            SafeFiles.Atomic(Path.Combine(state, "preferences.json"), File.ReadAllBytes(source));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
