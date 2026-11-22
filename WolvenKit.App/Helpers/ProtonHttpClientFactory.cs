using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace WolvenKit.App.Helpers;

/// <summary>
/// Opt-in transport for content downloads only. Normal mode is the framework default.
/// The handler retains responsibility for proxies, HTTP, TLS and certificate validation.
/// </summary>
public static class ProtonHttpClientFactory
{
    public static HttpClient CreateHttpClient() =>
        CreateHttpClient(Environment.GetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT"), null);

    // An internal seam for deterministic DNS/dialer tests and the private probe.
    internal static HttpClient CreateHttpClient(string? compatibilityFlag, ProtonAddressConnector? connector)
    {
        if (compatibilityFlag != "1")
        {
            return new HttpClient();
        }

        return new HttpClient(CreateCompatibilityHandler(connector ?? new ProtonAddressConnector()));
    }

    internal static SocketsHttpHandler CreateCompatibilityHandler(ProtonAddressConnector connector) => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = (context, ct) => new ValueTask<Stream>(connector.ConnectAsync(context.DnsEndPoint, ct))
    };
}
