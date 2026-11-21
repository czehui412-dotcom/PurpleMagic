using System.IO.Compression;
using System.Net;
using Dnf90.Launcher;
using ServerS5A3;

// Regression coverage for the whole-archive download/install pipeline.
// The original bug double-hashed the package digest (Trust.Digest already
// computes SHA-256), so every valid package failed verification and the
// launcher re-downloaded it forever.
internal static class GameArchiveInstallChecks
{
    internal static async Task Run(string temp, Action<bool, string> check)
    {
        // A minimal signed-shaped archive: two files, real zip bytes.
        var files = new Dictionary<string, byte[]>
        {
            ["DNF.exe"] = "fixture exe bytes for archive install"u8.ToArray(),
            ["Script.pvf"] = "fixture pvf bytes for archive install, slightly longer"u8.ToArray(),
        };
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (name, content) in files)
                {
                    var entry = z.CreateEntry(name);
                    using var s = entry.Open();
                    s.Write(content);
                }
            zip = ms.ToArray();
        }
        var zipHash = Trust.Digest(zip);
        var archiveFiles = files.Select(kv => new ArchiveFile(kv.Key, kv.Value.Length, Trust.Digest(kv.Value))).ToArray();
        var archive = new GameArchive("http://fixture.test/a", "", zipHash, zip.Length, archiveFiles);

        var clientDir = Path.Combine(temp, "archive-client");
        Directory.CreateDirectory(clientDir);
        File.WriteAllText(Path.Combine(clientDir, "DNF.exe"), "old exe");
        File.WriteAllText(Path.Combine(clientDir, "Script.pvf"), "old pvf");

        var stateDir = Path.Combine(temp, "archive-state");
        Directory.CreateDirectory(stateDir);
        using var diag = new Dnf90.Launcher.Diagnostics.DiagnosticService(stateDir);

        int downloads = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        })) { Timeout = Timeout.InfiniteTimeSpan };

        var installer = new GameArchiveInstaller(http, diag, _ => { });
        var ok = await installer.Install(clientDir, archive, new Progress<double>(), CancellationToken.None);

        check(ok, "valid archive installs successfully instead of failing its digest check");
        check(downloads == 1, "single download, no re-download loop");
        foreach (var (name, expected) in files)
        {
            var installed = Path.Combine(clientDir, name);
            check(File.Exists(installed) && File.ReadAllBytes(installed).SequenceEqual(expected),
                $"installed {name} matches signed file bytes");
        }

        // A genuinely corrupt package must still be rejected (guard against a
        // fix that simply disables verification). Rebuild a zip whose member
        // bytes differ from the signed file digests but whose package digest
        // matches the (attacker-controlled) archive Sha256, so only the
        // per-file verification can catch it.
        byte[] corrupt;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var entry = z.CreateEntry("DNF.exe");
                using var s = entry.Open();
                s.Write("TAMPERED exe bytes"u8);
            }
            corrupt = ms.ToArray();
        }
        var badArchive = new GameArchive("http://fixture.test/a", "", Trust.Digest(corrupt), corrupt.Length, archiveFiles);
        File.WriteAllText(Path.Combine(clientDir, "DNF.exe"), "old exe");
        using var badHttp = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt) })) { Timeout = Timeout.InfiniteTimeSpan };
        var badInstaller = new GameArchiveInstaller(badHttp, diag, _ => { });
        // Package digest passes, but DNF.exe content no longer matches its
        // signed digest -> Install catches and returns false.
        var badOk = await badInstaller.Install(clientDir, badArchive, new Progress<double>(), CancellationToken.None);
        check(!badOk, "tampered archive member is still rejected");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => Task.FromResult(f(r));
    }
}
