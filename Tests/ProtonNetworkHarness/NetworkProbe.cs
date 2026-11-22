using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using WolvenKit.App.Helpers;

internal static class NetworkProbe
{
    private static object Endpoint(Uri uri) => new { scheme = uri.Scheme, host = uri.IdnHost, port = uri.Port };

    // Deliberately exclude arbitrary messages, stacks and Exception.Data.
    private static object[] Errors(Exception error)
    {
        var errors = new List<object>();
        void Add(Exception ex)
        {
            errors.Add(new { type = ex.GetType().FullName, hresult = ex.HResult,
                httpRequestError = (ex as HttpRequestException)?.HttpRequestError.ToString(),
                cancellation = ex is OperationCanceledException ? "OperationCanceled" : null });
            if (ex is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) Add(inner);
            else if (ex.InnerException is { } inner) Add(inner);
        }
        Add(error);
        return errors.ToArray();
    }
    // A bounded GET-only wrapper linking the real production factory/connector.
    // Address injection affects DNS results, not Host, SNI or certificate validation.
    public static async Task<int> RunAsync(string[] args)
    {
        var output = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["runtime"] = Environment.Version.ToString(),
            ["os"] = Environment.OSVersion.ToString(),
            ["compatibilityFlag"] = Environment.GetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT")
        };
        var watch = Stopwatch.StartNew();
        var dns = new List<object>();
        try
        {
            if (args.Length < 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new ArgumentException("Usage: --probe URL [--addresses=IP,IP] [--timeout-seconds=20] [--cancel-ms=0]");
            output["endpoint"] = Endpoint(uri);
            string? Option(string name) => args.Skip(2).FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?.Split('=', 2)[1];
            var seconds = int.Parse(Option("--timeout-seconds") ?? "20");
            if (seconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
            var cancelMs = int.Parse(Option("--cancel-ms") ?? "0");
            if (cancelMs < 0) throw new ArgumentOutOfRangeException(nameof(cancelMs));
            var addresses = Option("--addresses")?.Split(',').Select(IPAddress.Parse).ToArray();
            output["injectedDns"] = addresses?.Select(a => a.ToString()).ToArray();
            var connector = new ProtonAddressConnector(async (host, ct) =>
            {
                var resolved = addresses ?? await Dns.GetHostAddressesAsync(host, ct);
                lock (dns) dns.Add(new { host, addresses = resolved.Select(a => a.ToString()).ToArray() });
                return resolved;
            });
            using var client = ProtonHttpClientFactory.CreateHttpClient(Environment.GetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT"), connector);
            client.Timeout = TimeSpan.FromSeconds(seconds);
            output["timeoutSeconds"] = seconds;
            output["requestVersion"] = client.DefaultRequestVersion.ToString();
            output["versionPolicy"] = client.DefaultVersionPolicy.ToString();
            output["defaultProxyEndpoint"] = HttpClient.DefaultProxy.GetProxy(uri) is { } proxyUri
                ? Endpoint(proxyUri) : null;
            output["defaultProxyBypassed"] = HttpClient.DefaultProxy.IsBypassed(uri);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            if (cancelMs > 0) cancel.CancelAfter(Math.Min(cancelMs, seconds * 1000));
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            output["status"] = (int)response.StatusCode;
            output["responseVersion"] = response.Version.ToString();
            output["finalEndpoint"] = response.RequestMessage?.RequestUri is { } finalUri ? Endpoint(finalUri) : null;
            output["headersElapsedMs"] = watch.ElapsedMilliseconds;
            response.EnsureSuccessStatusCode();
            // ResponseHeadersRead's request timeout does not bound subsequent reads.
            using var bodyBudget = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            bodyBudget.CancelAfter(TimeSpan.FromSeconds(seconds));
            await using var stream = await response.Content.ReadAsStreamAsync(bodyBudget.Token);
            using var body = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, bodyBudget.Token)) > 0)
            {
                if (body.Length + read > 64 * 1024 * 1024) throw new IOException("Probe body exceeded 64 MiB bound.");
                body.Write(buffer, 0, read);
            }
            output["bytes"] = body.Length;
            output["sha256"] = Convert.ToHexString(SHA256.HashData(body.GetBuffer().AsSpan(0, (int)body.Length))).ToLowerInvariant();
            if (uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                body.Position = 0;
                using var zip = new ZipArchive(body, ZipArchiveMode.Read, leaveOpen: true);
                output["zipEntries"] = zip.Entries.Count;
            }
            output["success"] = true;
            return 0;
        }
        catch (Exception ex)
        {
            output["success"] = false;
            output["errors"] = Errors(ex);
            return 1;
        }
        finally
        {
            lock (dns) output["dns"] = dns.ToArray();
            output["elapsedMs"] = watch.ElapsedMilliseconds;
            Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
