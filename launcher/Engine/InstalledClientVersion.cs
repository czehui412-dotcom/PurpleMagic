using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Dnf90.Launcher;

internal static class InstalledClientVersion
{
    // V numbers come from the signed game artifact, not the EXE's Windows file
    // version. Match the installed bytes before showing a release label.
    public static string Read(string directory, Bootstrap? current)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        try
        {
            var path = Path.Combine(directory, "DNF.exe");
            SafeFiles.NoLinks(path);
            if (!File.Exists(path)) return "";
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (current?.Game is not { } game) return "";
            var exe = game.Files.FirstOrDefault(f => f.Path.Equals("DNF.exe", StringComparison.OrdinalIgnoreCase));
            var label = Label(game.Version);
            if (label == "" || exe is null || exe.Length != file.Length) return "";
            var hash = Convert.ToHexString(SHA256.HashData(file));
            return exe.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase) ? label : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    private static string Label(string? version)
    {
        var match = Regex.Match(version ?? "", @"^(?:client[-_])?[vV]([1-9][0-9]{0,5})(?:$|[-_])", RegexOptions.CultureInvariant);
        return match.Success ? "V" + match.Groups[1].Value : "";
    }
}
