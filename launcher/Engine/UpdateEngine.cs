using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dnf90.Launcher.Diagnostics;
using ServerS5A3;

namespace Dnf90.Launcher;

// Unified update engine (设计文档 §7): compare launcher home against the
// gateway-signed Game artifact, produce a minimal action set, and run every
// strategy through one Download → Verify → Stage → Commit → Activate pipeline.
// There are no per-channel "presence implies skip" rules; the Delivery.Strategy
// in the signed artifact is the only dispatch input.
public sealed class UpdateEngine
{
    private readonly string home;
    private readonly string tmpRoot;
    private readonly DiagnosticService diagnostics;
    private readonly Action<string, string, long, long> emit;
    private readonly HttpClient archiveHttp;
    private readonly LauncherBundleDownloader blobDownloader;
    private readonly PlayerProfile profile;

    internal UpdateEngine(string home, string stateRoot, PlayerProfile profile, DiagnosticService diagnostics,
        Action<string, string, long, long> emit, HttpClient archiveHttp, LauncherBundleDownloader blobDownloader)
    {
        this.home = Path.GetFullPath(home);
        SafeFiles.NoLinks(this.home);
        this.profile = profile;
        this.diagnostics = diagnostics;
        this.emit = emit;
        this.archiveHttp = archiveHttp;
        this.blobDownloader = blobDownloader;
        tmpRoot = Path.Combine(stateRoot, "tmp");
    }

    public sealed record Change(string Path, long Length, string Sha256, string Reason);
    public sealed record Plan(ArtifactFile[] Files, string Strategy, string Target);

    // Diff (§8.1): every signed file is checked by final SHA-256 and length.
    // Missing/mismatched files are the only ones scheduled for installation.
    public async Task<Change[]> Diff(GameArtifact game, CancellationToken cancel)
    {
        var changes = new List<Change>();
        foreach (var file in game.Files)
        {
            cancel.ThrowIfCancellationRequested();
            var target = SafeFiles.Target(home, file.Path);
            if (!File.Exists(target)) { changes.Add(new(file.Path, file.Length, file.Sha256, "文件缺失")); continue; }
            await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var length = stream.Length;
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel)).ToLowerInvariant();
            if (length != file.Length || !actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                changes.Add(new(file.Path, file.Length, file.Sha256, length != file.Length ? "大小不符" : "摘要不符"));
        }
        return [.. changes];
    }

    public static string TargetId(GameArtifact game) => game.Delivery.Strategy == "archive"
        ? "archive:" + game.Delivery.PackageSha256
        : "blob:" + game.Version + ":" + game.Sequence;

    // Execute (§7.3): one pipeline for archive and blob. VerifyContent always
    // checks the final ArtifactFile.SHA256; transport digests are only integrity.
    public async Task<bool> Run(Plan plan, GameArtifact game, CancellationToken cancel)
    {
        var strategy = game.Delivery.Strategy;
        var total = plan.Files.Sum(f => f.Length);
        var done = 0L;
        var flux = new Progress<long>(bytes => emit("downloading", "正在下载游戏资源…", done + bytes, total));
        emit("checking", "正在准备更新事务…", 0, total);
        using var tx = new FileTransaction(home);
        tx.Recover();
        try
        {
            if (strategy == "archive")
            {
                var package = await DownloadArchive(game, flux, cancel);
                using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
                for (var i = 0; i < plan.Files.Length; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var file = plan.Files[i];
                    var entry = zip.GetEntry(file.Path.Replace('\\', '/')) ?? throw new InvalidDataException("资源包缺少文件：" + file.Path);
                    var stage = tx.Stage(i);
                    using (var src = entry.Open())
                    using (var dst = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None))
                        await src.CopyToAsync(dst, cancel);
                    VerifyContent(stage, file);
                    done += file.Length;
                    emit("installing", "正在校验 " + file.Path, done, total);
                }
            }
            else
            {
                var cache = Path.Combine(tmpRoot, "blobs");
                for (var i = 0; i < plan.Files.Length; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var file = plan.Files[i];
                    var stage = tx.Stage(i);
                    await DownloadBlob(game, file, cache, stage, bytes => emit("downloading", "正在下载 " + file.Path, done + bytes, total), cancel);
                    VerifyContent(stage, file);
                    done += file.Length;
                    emit("installing", "正在校验 " + file.Path, done, total);
                }
            }
            cancel.ThrowIfCancellationRequested();
            emit("installing", "正在提交更新…", total, total);
            tx.Commit(plan.Files.Select(f => f.Path).ToArray(), System.Text.Encoding.UTF8.GetBytes(game.Version));
            diagnostics.Event("update_complete", number: plan.Files.Length);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            diagnostics.Failure("update_engine", error);
            emit("error", "更新失败：" + error.Message, done, total);
            return false;
        }
    }

    private static void VerifyContent(string stage, ArtifactFile file)
    {
        if (new FileInfo(stage).Length != file.Length) throw new InvalidDataException("文件内容长度不符：" + file.Path);
        using var stream = File.OpenRead(stage);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"文件校验失败：{file.Path}（期望 {file.Sha256}，实际 {actual}）");
    }

    private async Task<byte[]> DownloadArchive(GameArtifact game, IProgress<long> flux, CancellationToken cancel)
    {
        var d = game.Delivery;
        var urls = d.URLs ?? [];
        var target = d.PackageSha256.ToLowerInvariant();
        Exception? last = null;
        Directory.CreateDirectory(tmpRoot);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var url = attempt < 2 ? urls[0] : urls[1];
            if (string.IsNullOrEmpty(url)) continue;
            var part = Path.Combine(tmpRoot, "package-" + target[..16] + "-" + attempt + ".part");
            try
            {
                if (File.Exists(part) && await SafeFiles.Matches(part, d.PackageSize, target, cancel))
                { flux.Report(d.PackageSize); return File.ReadAllBytes(part); }
                long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (offset >= d.PackageSize) { offset = 0; File.Delete(part); }
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                idle.CancelAfter(TimeSpan.FromSeconds(5));
                HttpResponseMessage response;
                try { response = await archiveHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token); }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new IOException("资源直链连接超时"); }
                using (response)
                {
                    if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent) offset = 0;
                    if (offset == 0 && !response.IsSuccessStatusCode) throw new IOException($"资源直链返回 {(int)response.StatusCode}");
                    response.EnsureSuccessStatusCode();
                    using var output = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                    using var input = await response.Content.ReadAsStreamAsync(cancel);
                    var buffer = new byte[64 * 1024]; var read = offset;
                    while (true)
                    {
                        int n;
                        try { n = await input.ReadAsync(buffer, idle.Token); }
                        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new IOException("资源直链传输超时"); }
                        if (n == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, n), cancel);
                        read += n; idle.CancelAfter(TimeSpan.FromSeconds(5));
                        if (read > d.PackageSize) throw new InvalidDataException("资源包超过签名大小。");
                        flux.Report(read);
                    }
                }
                if (!await SafeFiles.Matches(part, d.PackageSize, target, cancel))
                { File.Delete(part); throw new InvalidDataException("资源包摘要不匹配，将尝试下一直链。"); }
                return File.ReadAllBytes(part);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { last = error; }
        }
        throw new IOException("所有资源直链均失败：" + (last?.Message ?? "未知错误"));
    }

    private async Task DownloadBlob(GameArtifact game, ArtifactFile file, string cache, string stage, Action<long> progress, CancellationToken cancel)
    {
        var hash = file.Compression == "gzip" ? file.BlobSha256 : file.Sha256;
        var size = file.Compression == "gzip" ? file.BlobLength : file.Length;
        var part = Path.Combine(cache, hash.ToLowerInvariant() + ".part");
        await blobDownloader.Download(game, file, part, progress, cancel);
        SafeFiles.NoLinks(stage);
        try
        {
            await using var input = File.OpenRead(part);
            await using var output = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            using var gzip = file.Compression == "gzip" ? new GZipStream(input, CompressionMode.Decompress, true) : null;
            Stream plain = (Stream?)gzip ?? input;
            var buffer = new byte[65536]; long written = 0; int read;
            while ((read = await plain.ReadAsync(buffer, cancel)) != 0)
            {
                written += read;
                if (written > file.Length) throw new InvalidDataException("解压内容超过签名大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            }
            if (written != file.Length) throw new InvalidDataException("解压后长度不符：" + file.Path);
        }
        catch { File.Delete(stage); throw; }
    }
}
