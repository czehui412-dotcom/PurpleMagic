using System.Net.Http.Headers;
using System.Text;
using System.Security.Cryptography;
using System.Net.Security;
using System.Text.Json;

namespace Dnf90.Launcher;

// Long-lived SSE client for the gateway's maintenance push endpoint. There is
// no timer-based maintenance polling: this connection is opened once, reconnects
// on failure, and applies every "maintenance" frame immediately. Certificate
// pinning matches CheckMaintenance (host + profile SHA-256, no port comparison).
internal sealed class MaintenanceStream : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly string url;
    private readonly string certSha;
    private readonly Action<bool, string> onState;
    private readonly CancellationTokenSource cts = new();
    private readonly CancellationToken token;
    private readonly Task loop;
    private bool disposed;

    internal MaintenanceStream(string apiUrl, string certSha, Action<bool, string> onState)
    {
        this.certSha = certSha;
        this.onState = onState;
        url = StreamUrl(apiUrl);
        token = cts.Token;
        loop = Task.Run(Run);
    }

    internal static string StreamUrl(string apiUrl)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) return "";
        try
        {
            var uri = new Uri(apiUrl);
            return $"{uri.Scheme}://{uri.Authority}/launcher/v2/maintenance/stream";
        }
        catch { return ""; }
    }

    private async Task Run()
    {
        if (url.Length == 0) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
                    ServerCertificateCustomValidationCallback = (req, cert, _, errors) =>
                        req.RequestUri is { } r && cert is not null && r.Host == new Uri(url).Host
                        && errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) == false
                        && Convert.ToHexString(SHA256.HashData(cert.RawData))
                            .Equals(certSha, StringComparison.OrdinalIgnoreCase)
                };
                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"maintenance stream {(int)response.StatusCode}");
                using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    if (token.IsCancellationRequested) break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var payload = line.AsSpan(5).Trim().ToString();
                    if (payload.Length == 0 || payload == "ping") continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        var root = doc.RootElement;
                        var active = root.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                        var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "") : "";
                        onState(active, msg);
                    }
                    catch (JsonException) { }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { /* transient network failure: reconnect below */ }
            if (token.IsCancellationRequested) break;
            try { await Task.Delay(RetryDelay, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
    }
}
