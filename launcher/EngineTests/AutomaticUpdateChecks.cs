using System.Text.Json;
using Dnf90.Launcher;
using ServerS5A3;

// New architecture (§5/§7): no local client directory, no preferences.json
// config, no three channels. Automatic update is policy-driven from the signed
// profile; identity.json is the only account/consent store.
internal static class AutomaticUpdateChecks
{
    internal static async Task Run(string temp, PlayerProfile profile, Action<bool, string> check)
    {
        check(LauncherEngine.MaintenanceStatusUrl("https://124.221.247.221:8443") == "https://124.221.247.221/status/maintenance.json",
            "maintenance status URL derives from API host on default 443 (not gateway 8443)");

        var state = Path.Combine(temp, "auto-state");
        using (var engine = new LauncherEngine(profile, state))
        {
            using var serialized = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(engine.State, WireJson.Default.State));
            check(serialized.RootElement.GetProperty("launcherVersion").GetString() == LauncherEngine.VersionText,
                "wire state reports running launcher version independently of game version");
            await engine.SavePreferences(new(Account: "new-choice", RememberAccount: true, AntiCheatConsentVersion: 1));
        }
        using (var engine = new LauncherEngine(profile, state))
        {
            check(engine.State.Preferences.Account == "new-choice" && engine.State.Preferences.AntiCheatConsentVersion == 1,
                "identity account and consent survive core restart");
        }

        // Manual client-directory override: auto-detect is the default, but a
        // user-picked directory is persisted and becomes authoritative.
        var manual = Path.Combine(temp, "manual-client");
        Directory.CreateDirectory(manual);
        File.WriteAllBytes(Path.Combine(manual, "DNF.exe"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(manual, "Script.pvf"), [4, 5, 6]);
        using (var engine = new LauncherEngine(profile, state))
        {
            await engine.SavePreferences(new Preferences(ClientDirectory: manual));
            check(engine.State.LauncherHome == manual && engine.State.Preferences.ClientDirectory == manual,
                "manual directory choice updates core home and mirrors to UI");
        }
        using (var engine = new LauncherEngine(profile, state))
            check(engine.State.LauncherHome == manual, "manual directory choice survives core restart");
        var bogus = Path.Combine(temp, "bogus-client"); Directory.CreateDirectory(bogus);
        using (var engine = new LauncherEngine(profile, state))
        {
            try { await engine.SavePreferences(new Preferences(ClientDirectory: bogus)); throw new Exception("bogus directory accepted"); }
            catch (InvalidOperationException) { check(true, "directory without DNF.exe/Script.pvf is rejected"); }
            check(engine.State.LauncherHome == manual, "rejected directory does not change home");
        }

        // Off policy is gateway-signed; no metadata fetch, no installer.
        var offProfile = profile with { UpdatePolicy = "off" };
        using (var off = new LauncherEngine(offProfile, Path.Combine(temp, "auto-off")))
        {
            check(!off.State.Preferences.AutoUpdate, "gateway updatePolicy=off disables the automatic switch");
            check(!await off.AutomaticUpdate(-1), "off policy does not start automatic update");
            check(off.State.Phase == "ready" && !File.Exists(Path.Combine(temp, "auto-off", "bootstrap.json")),
                "off policy finishes ready without contacting metadata or caching bootstrap");
        }

        // A developer may be playing while running this suite. Preserve the process guard.
        var games = System.Diagnostics.Process.GetProcessesByName("DNF");
        var gameRunning = games.Length != 0;
        foreach (var game in games) game.Dispose();
        if (gameRunning)
        {
            using var guarded = new LauncherEngine(profile, Path.Combine(temp, "auto-running-game"));
            try { await guarded.AutomaticUpdate(-1); throw new Exception("running game was ignored"); }
            catch (InvalidOperationException error)
            {
                check(error.Message == "更新或恢复文件前请退出所有游戏客户端。", "running game blocks automatic update before network or file installation");
            }
        }
    }
}
