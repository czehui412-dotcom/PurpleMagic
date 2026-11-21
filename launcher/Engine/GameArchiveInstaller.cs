using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dnf90.Launcher.Diagnostics;

namespace Dnf90.Launcher;

// Downloads a single whole-archive zip from operator-supplied direct links,
// verifies the signed digest, then atomically installs the files. The URLs are
// untrusted transport; the signed GameArchive digests are the only authority.
// Primary link is tried first with a short idle timeout; on any failure the
// fallback link is used. Both downloads resume via Range and are hash-checked.
public sealed class GameArchiveInstaller
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);
    private static readonly int MaxAttempts = 3; // 0,1 -> primary; 2 -> fallback
    private static readonly long MaxZipBytes = 8L * 1024 * 1024 * 1024;

    private readonly HttpClient http;
    private readonly DiagnosticService diagnostics;
    private readonly Action<string> emit;

    internal GameArchiveInstaller(HttpClient http, DiagnosticService diagnostics, Action<string> emit)
    {
        this.http = http;
        this.diagnostics = diagnostics;
        this.emit = emit;
    }

    private string UrlFor(GameArchive archive, int attempt)
    {
        var useFallback = attempt >= 2 || string.IsNullOrEmpty(archive.PrimaryUrl);
        var url = useFallback ? archive.FallbackUrl : archive.PrimaryUrl;
        if (string.IsNullOrEmpty(url)) url = useFallback ? archive.PrimaryUrl : archive.FallbackUrl;
        return url;
    }

    // Downloads the zip with A/B failover + Range resume, verifies its digest,
    // then installs files atomically. Returns true on success.
    public async Task<bool> Install(string clientDirectory, GameArchive archive, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var targetHash = archive.Sha256.ToLowerInvariant();
        var bytes = await DownloadWithFailover(archive, targetHash, archive.Size, progress, cancellationToken);
        if (!string.Equals(Trust.Digest(bytes), targetHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源包校验失败，已放弃更新。");

        SafeFiles.NoLinks(clientDirectory);
        using var tx = new FileTransaction(clientDirectory);
        tx.Recover();
        var extracted = new List<(ArchiveFile file, string stage)>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            for (int i = 0; i < archive.Files.Length; i++)
            {
                var file = archive.Files[i];
                var entry = zip.GetEntry(file.Path.Replace('\\', '/'));
                if (entry is null) throw new InvalidDataException("资源包缺少文件：" + file.Path);
                var stage = tx.Stage(i);
                using (var src = entry.Open())
                using (var dst = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None))
                    await src.CopyToAsync(dst, cancellationToken);
                if (new FileInfo(stage).Length != file.Length)
                    throw new InvalidDataException("资源包文件大小不符：" + file.Path);
                var staged = File.ReadAllBytes(stage);
                if (!string.Equals(Trust.Digest(staged), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("资源包文件校验失败：" + file.Path);
                extracted.Add((file, stage));
            }
            // Extra entries in the zip are ignored; only signed files are installed.
            var installed = JsonSerializer.SerializeToUtf8Bytes(new { archive.Sha256, archive.Files.Length, InstalledAt = DateTimeOffset.UtcNow });
            tx.Commit(archive.Files.Select(f => f.Path).ToArray(), installed);
            emit("资源包更新完成，正在重启登录器。");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            diagnostics.Failure("game_archive_install", error);
            emit("资源包更新失败：" + error.Message);
            return false;
        }
    }

    private async Task<byte[]> DownloadWithFailover(GameArchive archive, string sha256, long size, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancel.CancelAfter(TimeSpan.FromMinutes(30));
        Exception? last = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var url = UrlFor(archive, attempt);
            if (string.IsNullOrEmpty(url)) continue;
            diagnostics.Event("game_archive_attempt", number: attempt, exception: null);
            try
            {
                return await DownloadOne(url, sha256, size, attempt, progress, cancel.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { last = new IOException("直链响应超时"); }
            catch (Exception error) { last = error; }
        }
        throw new IOException("所有直链均下载失败：" + (last?.Message ?? "未知错误"));
    }

    private async Task<byte[]> DownloadOne(string url, string sha256, long size, int slot, IProgress<double>? progress, CancellationToken token)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"dnf90-archive-{sha256[..16]}-{slot}.part");
        long offset = 0;
        if (File.Exists(temp))
        {
            var localBytes = File.ReadAllBytes(temp);
            if (string.Equals(Trust.Digest(localBytes), sha256, StringComparison.OrdinalIgnoreCase)) { progress?.Report(1); return localBytes; }
            offset = localBytes.Length;
            if (offset <= 0 || offset >= size) { offset = 0; File.Delete(temp); }
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        // 5s to connect and receive response headers; a slow A link fails fast
        // so the failover loop can try B without user-visible delay.
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
        connect.CancelAfter(IdleTimeout);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("直链连接超时"); }
        using (response)
        {
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent) { offset = 0; }
            if (offset == 0 && !response.IsSuccessStatusCode) throw new IOException($"直链返回 {(int)response.StatusCode}");
            response.EnsureSuccessStatusCode();
            var total = offset + (response.Content.Headers.ContentLength ?? 0);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(IdleTimeout);
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            using (var file = new FileStream(temp, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
            using (var src = await response.Content.ReadAsStreamAsync(idle.Token))
            {
                var buffer = new byte[64 * 1024];
                long read = offset;
                while (true)
                {
                    int n;
                    try { n = await src.ReadAsync(buffer, idle.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("直链传输超时"); }
                    if (n == 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, n), token);
                    read += n;
                    idle.CancelAfter(IdleTimeout); // idle timeout resets on progress
                    if (total > 0) progress?.Report(Math.Clamp((double)read / total, 0, 1));
                }
            }
        }
        var bytes = File.ReadAllBytes(temp);
        // Trust.Digest already computes SHA-256; hashing the digest again here
        // made every package fail verification and re-download forever.
        if (bytes.Length > MaxZipBytes || !string.Equals(Trust.Digest(bytes), sha256, StringComparison.OrdinalIgnoreCase))
        { File.Delete(temp); throw new InvalidDataException("下载内容校验失败，将尝试下一直链。"); }
        return bytes;
    }
}
