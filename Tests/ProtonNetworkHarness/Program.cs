using System.Reflection;
using System.Net.Http;
using System.Net;
using System.Diagnostics;
using System.Collections.Concurrent;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

if (args.Length > 0 && args[0] == "--probe") return await NetworkProbe.RunAsync(args);

try
{
var original = Environment.GetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT");
try
{
    var factory = Assembly.GetExecutingAssembly().GetType("WolvenKit.App.Helpers.ProtonHttpClientFactory");
    Check(factory is not null, "Missing production ProtonHttpClientFactory");
    var create = factory!.GetMethod("CreateHttpClient", Type.EmptyTypes)!;
    foreach (var flag in new string?[] { null, "", "true", "01", "1 ", "0", "1" })
    {
        Environment.SetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT", flag);
        using var client = (HttpClient)create.Invoke(null, null)!;
        var handler = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client);
        Check(client.Timeout == TimeSpan.FromSeconds(100), "Default request timeout changed");
        Check(client.DefaultRequestVersion == new Version(1, 1), "Default HTTP version changed");
        Check(client.DefaultRequestHeaders.Count() == 0, "Unexpected default headers");
        if (flag == "1")
        {
            Check(handler is SocketsHttpHandler { UseProxy: true, ConnectCallback: not null }, "Exact flag must opt into connector without disabling proxies");
            var sockets = (SocketsHttpHandler)handler!;
            Check(sockets.SslOptions.RemoteCertificateValidationCallback is null, "Certificate validation overridden");
            Check(sockets.ConnectTimeout > TimeSpan.Zero && sockets.ConnectTimeout <= TimeSpan.FromSeconds(15), "Connection timeout unbounded");
        }
        else Check(handler is HttpClientHandler, "Normal mode must be identical to new HttpClient()");
    }
    Console.WriteLine("PASS ExactOptInPreservesProxyTlsAndDefaults");
}
finally { Environment.SetEnvironmentVariable("WOLVENKIT_PROTON_COMPAT", original); }

var connectorType = Assembly.GetExecutingAssembly().GetType("WolvenKit.App.Helpers.ProtonAddressConnector");
Check(connectorType is not null, "Missing production bounded address connector");
var first = IPAddress.Parse("192.0.2.1");
var second = IPAddress.Loopback;
var calls = new ConcurrentQueue<IPAddress>();
var winningStream = new MemoryStream(new byte[] { 42 });
Func<string, CancellationToken, Task<IPAddress[]>> resolver = (_, _) => Task.FromResult(new[] { first, second });
Func<IPEndPoint, CancellationToken, Task<Stream>> dialer = async (endpoint, ct) =>
{
    calls.Enqueue(endpoint.Address);
    if (endpoint.Address.Equals(first)) await Task.Delay(Timeout.Infinite, ct);
    return winningStream;
};
var connector = Activator.CreateInstance(connectorType!, resolver, dialer, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2))!;
var watch = Stopwatch.StartNew();
using var result = await (Task<Stream>)connectorType!.GetMethod("ConnectAsync")!.Invoke(connector, new object[] { new DnsEndPoint("test.invalid", 443), CancellationToken.None })!;
Check(ReferenceEquals(result, winningStream), "Winning stream not returned to caller");
Check(result.ReadByte() == 42, "Winning stream disposed before ownership transfer");
Check(calls.SequenceEqual(new[] { first, second }), "DNS candidates not tried in order");
Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Unreachable first address blocked fallback");
Console.WriteLine("PASS UnreachableFirstFallsBackAndTransfersWinner");
await ConnectorTests.RunAsync();
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
    return 1;
}
