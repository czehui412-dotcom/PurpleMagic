using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dnf90.Launcher;
using ServerS5A3;

#if ANTICHEAT
if (args.Length > 0 && args[0] == "--anticheat-guard") { var code = await Dnf90.Launcher.AntiCheat.AntiCheatGuard.Run(args, AntiCheatUacChecks.Profile()); File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"uac-guard-exit.txt"),code.ToString()); Environment.Exit(code); return; }
if (args.Length == 4 && args[0] == "--anticheat-uac-check") { await AntiCheatUacChecks.Run(args[1], args[2], args[3]); return; }
if (args.Length == 2 && args[0] == "--anticheat-channel-check") {
    Directory.CreateDirectory(args[1]);
    var channelRoot = new PlayerProfile("https://fixture.test", new('a',64), "", "", "192.0.2.1", 7001, new('b',64), new('c',64), "");
    void ChannelChecked(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    await LauncherBundleDownloadChecks.Run(args[1], channelRoot, ChannelChecked);
    await AutomaticUpdateChecks.Run(args[1], channelRoot, ChannelChecked); return;
}
if (args.Length == 3 && args[0] == "--anticheat-check") {
    await AntiCheatChecks.Native(args[1],args[2],(ok,name)=>{if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);});return;
}
#endif

if (args.Length > 0 && args[0] == "--diagnostic-observer") { Environment.Exit(await Dnf90.Launcher.Diagnostics.DiagnosticObserver.Run(args)); return; }
if (args.Length == 2 && args[0] == "--diagnostic-crash-check") {
    Directory.CreateDirectory(args[1]);
    await CrashDiagnosticsChecks.Run(args[1], (ok, name) => { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }); return;
}
if (args.Length == 2 && args[0] == "--diagnostic-context-check") {
    Directory.CreateDirectory(args[1]);
    await DiagnosticContextChecks.Run(args[1], (ok, name) => { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }); return;
}

if (args.Length == 2 && args[0] == "--news-interop") { await NewsIntegration.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--bundle-lifecycle") { await BundleLifecycleChecks.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--bundle-exit") { await BundleExitChecks.Run(args[1]); return; }
if (args.Length == 1 && args[0] == "--native-plugin-paths") { NativePluginPathChecks.Run(); return; }
if (args.Length == 2 && args[0] == "--diagnostic-benchmark") { await DiagnosticBenchmark.Run(args[1]); return; }
if (args.Length == 1 && args[0] == "--diagnostic-at5t-child") { DiagnosticChecks.MemoryChild(true); return; }
if (args.Length == 1 && args[0] == "--diagnostic-memory-child") { DiagnosticChecks.MemoryChild(); return; }
if (args.Length == 1 && args[0] == "--socket-fixture-child") { await ContractDiagnosticsChecks.SocketFixtureChild(); return; }

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception error) when (error is InvalidDataException or CryptographicException or IOException) { Check(true, name); return; } throw new Exception("Accepted: " + name); }
async Task RejectAsync(Func<Task> action, string name) { try { await action(); } catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException) { Check(true, name); return; } throw new Exception("Accepted: " + name); }
var temp = Path.Combine(Path.GetTempPath(), "dnf90-engine-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
try
{
    AntiCheatChecks.Policy(temp, Check);
    await DiagnosticChecks.Run(temp, Check);
    await DiagnosticContextChecks.Run(temp, Check);
    await ContractDiagnosticsChecks.Run(temp, Check);
    using var key = RSA.Create(2048);
    var root = new PlayerProfile("https://accounts.example.test", new string('a', 64), key.ExportSubjectPublicKeyInfoPem(), "https://accounts.example.test/old-wpf-settings", "192.0.2.1", 7001, new string('b', 64), new string('c', 64), "");
    await CrashDiagnosticsChecks.Consent(temp, root, Check);
    await LauncherBundleDownloadChecks.Run(temp, root, Check);
    await GameConfigurationChecks.Run(temp, root, Check, RejectAsync);
    await AutomaticUpdateChecks.Run(temp, root, Check);
    BundleFailureChecks.Run(temp, Check);
    GameArtifactChecks.Run(root, key, Check);
    LocalStateStoreChecks.Run(temp, Check);
    var game = new GameArtifact("client-v1", "author", 5,
        [new("DNF.exe", 10, root.ExeSha256), new("Script.pvf", 20, root.PvfSha256)],
        new GameDelivery("blob", Endpoint: "https://content.example.test", CertificateSha256: ""));
    var bootstrap = new Bootstrap("DNF90-Flutter", 2, 10, root, [], null, game);
    byte[] Sign(Bootstrap value, RSASignaturePadding? padding = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, WireJson.Default.Bootstrap);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedEnvelope(Convert.ToBase64String(payload), Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, padding ?? RSASignaturePadding.Pss))), CoreJson.Default.SignedEnvelope);
    }
    var envelope = Sign(bootstrap);
    using(var disabledBindingEngine = new LauncherEngine(root,Path.Combine(temp,"binding-build-default"))) {
        Check(!disabledBindingEngine.State.QQBindingEnabled,"open-source build hides binding by default");
        await RejectAsync(()=>disabledBindingEngine.AccountSecurity("binding-code",new("player","fixture-password","")),"disabled build cannot request a binding code");
    }
    using(var optionalBindingEngine = new LauncherEngine(root with {EnableQQBinding=true},Path.Combine(temp,"binding-optional-build")))
        Check(optionalBindingEngine.State.QQBindingEnabled&&!optionalBindingEngine.State.RequireQQBinding,"binding entry is available without mandatory login binding");

    Check(Trust.Verify(envelope, root).Game.Files.Length == 2, "own PSS bootstrap and game contract");
    NativePluginPathChecks.Run();
    var transition = bootstrap with { Profile = root with { ChannelServerId = 1, PreviousChannelServerId = 0 } };
    var migrated = Trust.Verify(Sign(transition), root).Profile;
    Check(migrated.ChannelServerId == 1 && migrated.PreviousChannelServerId == 0, "signed directory transition survives AOT bootstrap JSON");
    Check(Trust.Verify(envelope, root).Profile.ChannelServerId == 0 && Trust.Verify(envelope, root).Profile.PreviousChannelServerId is null, "legacy signed directory defaults remain zero only");
    foreach (var profile in new[] { root with { ChannelServerId = -1 }, root with { ChannelServerId = 250 }, root with { PreviousChannelServerId = 0 }, root with { PreviousChannelServerId = -1 }, root with { PreviousChannelServerId = 250 } })
        Reject(() => Trust.Verify(Sign(bootstrap with { Profile = profile }), root), "signed invalid directory transition rejected before use");
    Reject(() => Trust.Verify(Sign(bootstrap, RSASignaturePadding.Pkcs1), root), "author PKCS1v15 cannot replace own PSS trust");
    using (var foreign = RSA.Create(2048)) Reject(() => Trust.Verify(envelope, root with { UpdatePublicKey = foreign.ExportSubjectPublicKeyInfoPem() }), "foreign signer");
    Reject(() => Trust.Verify(Sign(bootstrap with { Product = "other" }), root), "wrong product");
    Reject(() => Trust.Verify(Sign(bootstrap with { Profile = root with { ApiUrl = "https://content.example.test" } }), root), "account origin cannot move to author source");
    Reject(() => Trust.Verify(Sign(bootstrap with { Game = game with { Files = [game.Files[0] with { Path = "YKCN90Launcher.exe" }, game.Files[1]] } }), root), "foreign launcher cannot enter managed game files");
    Reject(() => Trust.Verify(Sign(bootstrap with { Format = 1 }), root), "format=1 rejected");
    Reject(() => Trust.CheckSequence(bootstrap with { Sequence = 9 }, envelope, bootstrap, envelope), "bootstrap rollback rejected");
    var changed = bootstrap with { News = [new("changed", "body", "更新", "today")] };
    Reject(() => Trust.CheckSequence(changed, Sign(changed), bootstrap, envelope), "same-sequence equivocation rejected");
    var sourceRollback = bootstrap with { Sequence = 11, Game = game with { Sequence = 4 } };
    Reject(() => Trust.CheckSequence(sourceRollback, Sign(sourceRollback), bootstrap, envelope), "game source rollback rejected");
    foreach (var path in new[] { "../file", "x/../file", "/absolute", "C:/x", "x\\file", "NUL.txt", "x/COM1", "x. /file", ".dnf90-update/installed.json", "a\u0000b" })
        Reject(() => SafeFiles.Target(temp, path), "unsafe path " + path.Replace('\0', '?'));
    foreach (var step in new[] { "prepared", "backed-up", "installed", "receipt", "committed" })
    {
        var dir = Path.Combine(temp, step); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "DNF.exe"), "old"); File.WriteAllText(Path.Combine(dir, "user.ini"), "keep");
        Directory.CreateDirectory(Path.Combine(dir, ".dnf90-update")); File.WriteAllText(Path.Combine(dir, ".dnf90-update", "installed.json"), "old-receipt");
        using (var transaction = new FileTransaction(dir))
        {
            File.WriteAllText(transaction.Stage(0), "new"); File.WriteAllText(transaction.Stage(1), "new-file");
            transaction.FaultPoint = (point, _) => { if (point == step) throw new SimulatedPowerLoss(); };
            try { transaction.Commit(["DNF.exe", "dstr.dat"], "new-receipt"u8.ToArray()); } catch (SimulatedPowerLoss) { }
        }
        using (var recovery = new FileTransaction(dir)) { recovery.Recover(); recovery.Recover(); }
        var committed = step == "committed";
        Check(File.ReadAllText(Path.Combine(dir, "DNF.exe")) == (committed ? "new" : "old"), "file recovery " + step);
        Check(File.Exists(Path.Combine(dir, "dstr.dat")) == committed, "new-file recovery " + step);
        Check(File.ReadAllText(Path.Combine(dir, ".dnf90-update", "installed.json")) == (committed ? "new-receipt" : "old-receipt"), "receipt recovery " + step);
        Check(File.ReadAllText(Path.Combine(dir, "user.ini")) == "keep", "unmanaged data retained " + step);
    }
    using (var locked = new FileTransaction(Path.Combine(temp, "exclusive"))) Reject(() => new FileTransaction(Path.Combine(temp, "exclusive")).Dispose(), "concurrent updater lock");
    Console.WriteLine($"{passed} engine checks passed; isolated files only, no production network, no game or GUI.");
}
finally { Directory.Delete(temp, true); }

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request)); }
