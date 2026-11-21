namespace Dnf90.Launcher;

internal static class ClientDirectoryDiscovery
{
    internal const string LaunchDirectoryVariable = "DNF90_LAUNCH_DIRECTORY";

    internal static string Find(string saved, string configured, string appDirectory, string? entryDirectory, string workingDirectory)
    {
        // The launcher always lives next to DNF.exe. The portable entry passes
        // that directory in DNF90_LAUNCH_DIRECTORY, so it is authoritative and
        // is never overridden by a saved selection or a guessed path. This is
        // the core of the "no local client directory" design; the remaining
        // fallbacks are transition compatibility for standard builds/tests and
        // are removed in P5.
        var fromEntry = Inspect(entryDirectory);
        if (fromEntry != "") return fromEntry;
        var selected = Inspect(saved);
        if (selected != "") return selected;
        var app = Path.TrimEndingDirectorySeparator(appDirectory);
        var install = Path.GetFileName(app).Equals("app", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(app) : null;
        foreach (var root in new[] { entryDirectory, install, appDirectory, workingDirectory, configured })
        {
            selected = Inspect(root);
            if (selected != "") return selected;
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var child in new[] { ".client", "client" }) {
                selected = Inspect(Path.Combine(root, child));
                if (selected != "") return selected;
            }
        }
        return "";
    }

    private static string Inspect(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) return "";
        try
        {
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            foreach (var name in new[] { "DNF.exe", "Script.pvf" })
            {
                var path = Path.Combine(directory, name);
                SafeFiles.NoLinks(path);
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return "";
            }
            // Discovery also accepts an older installation so it can be updated.
            // Signed content and VerifyClient retain the full version checks.
            return directory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return "";
        }
    }
}
