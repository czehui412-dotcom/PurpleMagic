using System.Reflection;
using Dnf90.Launcher;
using ServerS5A3;

// Regression: maintenance kill must not flip the one-way launcher-shutdown
// guard, otherwise every login after maintenance ends reports 「登录器已关闭。」.
internal static class MaintenanceChecks
{
    internal static void Run(string temp, PlayerProfile profile, Action<bool, string> check)
    {
        using var engine = new LauncherEngine(profile, Path.Combine(temp, "maintenance-guard"));
        var type = typeof(LauncherEngine);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var stop = type.GetMethod("StopProtectedGames", flags, [typeof(bool)])!;
        var closing = type.GetField("guardClosing", flags)!;
        var lifetime = type.GetField("guardLifetime", flags)!;

        stop.Invoke(engine, [false]);
        check(closing.GetValue(engine) is false, "maintenance kill does not latch launcher-shutdown guard");
        check(!((CancellationTokenSource)lifetime.GetValue(engine)!).IsCancellationRequested,
            "maintenance kill keeps anti-cheat lifetime usable for next login");

        stop.Invoke(engine, [true]);
        check(closing.GetValue(engine) is true, "real shutdown still closes the guard");
    }
}
