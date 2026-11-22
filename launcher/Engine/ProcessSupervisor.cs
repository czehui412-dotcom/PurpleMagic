using System.ComponentModel;
using System.Diagnostics;

namespace Dnf90.Launcher;

// Unified lifetime owner for game processes started by this launcher.
//
// Tracks (PID, creation ticks) instead of holding Process objects: anti-cheat
// games are attached via Process.GetProcessById, where .SafeHandle/.ExitCode
// throw "Process was not started by this object". All stops go through
// BundleProcesses.StopByIdentity so both direct and guarded games behave alike.
internal sealed class ProcessSupervisor
{
    private readonly record struct Child(int Pid, long Started);
    private readonly object gate = new();
    private readonly List<Child> children = [];
    public bool ShuttingDown { get; private set; }

    internal void Track(Process process)
    {
        if (process is null) return;
        long started;
        try { started = process.StartTime.ToUniversalTime().Ticks; }
        catch (InvalidOperationException) { return; } // attached: read via kernel handle instead.
        if (started <= 0 && process.Id > 0)
            try { started = BundleProcesses.CreatedTicks(process.Id); } catch { }
        lock (gate)
        {
            if (ShuttingDown) { _ = BundleProcesses.StopByIdentity(process.Id, started); return; }
            children.RemoveAll(c => c.Pid == process.Id);
            children.Add(new(process.Id, started));
        }
    }

    internal void Forget(Process process)
    {
        if (process is null) return;
        lock (gate) children.RemoveAll(c => c.Pid == process.Id);
    }

    // Stops and forgets every tracked game WITHOUT latching the shutdown gate.
    // Maintenance is temporary: once maintenance ends the user must be able to
    // launch a new game. Latching here made Track() kill every later game, which
    // surfaced as "客户端提前退出（代码 -1）".
    internal async Task StopAllForMaintenanceAsync(Action<string, int>? log = null)
    {
        List<Child> snapshot;
        lock (gate)
        {
            snapshot = [.. children];
            children.Clear();
        }
        var stopped = await StopAll(snapshot);
        log?.Invoke("maintenance_stop_games", stopped);
    }
    internal void StopAllForMaintenanceSync() => StopAllForMaintenanceAsync().GetAwaiter().GetResult();

    // Real launcher exit: latch the gate so any late Track() is killed too.
    internal async Task ShutdownAsync(Action<string, int>? log = null)
    {
        List<Child> snapshot;
        lock (gate)
        {
            if (ShuttingDown) return;
            ShuttingDown = true;
            snapshot = [.. children];
            children.Clear();
        }
        var stopped = await StopAll(snapshot);
        log?.Invoke("shutdown_stop_games", stopped);
    }
    internal void ShutdownSync() => ShutdownAsync().GetAwaiter().GetResult();

    private static async Task<int> StopAll(List<Child> snapshot)
    {
        var stopped = 0;
        foreach (var child in snapshot)
        {
            try { await BundleProcesses.StopByIdentity(child.Pid, child.Started); stopped++; }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or TimeoutException) { stopped++; }
        }
        return stopped;
    }
}
