using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dnf90.Launcher;

// Unified lifetime owner for game processes started by this launcher.
//
// Why only games: the guard, diagnostic observer and update helper all have
// their own parent-death / pipe-disconnect protocols and, crucially, the
// helper is intentionally cross-lifecycle (it finishes a bundle apply after
// the core has exited). Killing those here would break updates. The real gap
// was the standard (non-anticheat) DNF.exe: Dispose() only released the handle
// and left the game running after the launcher closed.
//
// Children are tracked by (PID, creation ticks) so a recycled PID can never be
// stopped by mistake. Shutdown uses the kernel-handle wait in BundleProcesses
// so file handles are actually released before the launcher exits.
internal sealed class ProcessSupervisor
{
    private sealed record Child(Process Process, long Started);
    private readonly object gate = new();
    private readonly List<Child> children = [];
    public bool ShuttingDown { get; private set; }

    internal void Track(Process process)
    {
        if (process is null || process.HasExited) return;
        long started;
        try { started = process.StartTime.ToUniversalTime().Ticks; }
        catch (InvalidOperationException) { return; }
        lock (gate)
        {
            if (ShuttingDown) { SafeKill(process); return; }
            children.RemoveAll(c => c.Process.Id == process.Id && c.Started == started);
            children.Add(new(process, started));
        }
    }

    internal void Forget(Process process)
    {
        lock (gate) children.RemoveAll(c => c.Process.Id == process.Id);
    }

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
        var stopped = 0;
        foreach (var child in snapshot)
        {
            try
            {
                await BundleProcesses.StopLaunched(child.Process);
                stopped++;
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or TimeoutException)
            {
                SafeKill(child.Process);
                stopped++;
            }
            catch (Exception error) when (error is ArgumentException) { /* PID already recycled. */ }
            finally { child.Process.Dispose(); }
        }
        log?.Invoke("shutdown_stop_games", stopped);
    }

    internal void ShutdownSync() => ShutdownAsync().GetAwaiter().GetResult();

    private static void SafeKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}
