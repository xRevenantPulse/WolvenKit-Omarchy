using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WolvenKit.App.Helpers;

internal static class ConnectorTests
{
    private static readonly DnsEndPoint Endpoint = new("test.invalid", 443);
    private static readonly IPAddress Bad = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress[] Pair = [Bad, IPAddress.Loopback];
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static ProtonAddressConnector Make(Func<IPEndPoint, CancellationToken, Task<Stream>> dial,
        IPAddress[]? addresses = null, int attemptMs = 120, int overallMs = 1000) => new(
        (_, _) => Task.FromResult(addresses ?? Pair), dial,
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(attemptMs), TimeSpan.FromMilliseconds(overallMs));

    public static async Task RunAsync()
    {
        var failures = 0;
        foreach (var test in new Func<Task>[] { InterleavesFamilies, Cancellation, DnsFailures,
                     AllFailed, AttemptBudgetAndParallelLimit, OverallBudget, LateLoserDisposal,
                     SimultaneousWinners, RealLoopbackV4, RealLoopbackV6, RealSocketLoserClosed,
                     FactoryWiring, ConfiguredProxy, RejectsUntrustedTls, SynchronousDialThrow, LateDialFault,
                     CancellationAtWinningBoundary,
                     LateDnsTests.TimeoutFault, LateDnsTests.CallerFault,
                     LateDnsTests.TimeoutSuccess, LateDnsTests.CallerSuccess })
        {
            try
            {
                await test().WaitAsync(TimeSpan.FromSeconds(10));
                Console.WriteLine($"PASS {test.Method.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Method.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Check(failures == 0, $"{failures} focused scenarios failed");
    }

    private static async Task InterleavesFamilies()
    {
        foreach (var preferredV6 in new[] { true, false })
        {
            var attempted = new ConcurrentQueue<IPAddress>();
            var first = preferredV6 ? IPAddress.Parse("2001:db8::1") : Bad;
            var second = preferredV6 ? IPAddress.Parse("2001:db8::2") : IPAddress.Parse("192.0.2.2");
            var good = preferredV6 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            var connector = Make(async (ep, ct) =>
            {
                attempted.Enqueue(ep.Address);
                if (!ep.Address.Equals(good)) await Task.Delay(Timeout.Infinite, ct);
                return new MemoryStream();
            }, [first, second, good], attemptMs: 400);
            using var stream = await connector.ConnectAsync(Endpoint, default);
            Check(attempted.Take(2).SequenceEqual(new[] { first, good }), "Must interleave families preserving first DNS family");
        }
    }

    private static async Task Cancellation()
    {
        var dnsCalls = 0;
        var dialCalls = 0;
        var connector = new ProtonAddressConnector((_, _) => { dnsCalls++; return Task.FromResult(Pair); },
            async (_, ct) => { Interlocked.Increment(ref dialCalls); await Task.Delay(Timeout.Infinite, ct); return new MemoryStream(); });
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Throws<OperationCanceledException>(() => connector.ConnectAsync(Endpoint, canceled.Token));
            Check(dnsCalls == 0 && dialCalls == 0, "Canceled call started DNS/TCP work");
        }
        using var cancel = new CancellationTokenSource(50);
        var watch = Stopwatch.StartNew();
        await Throws<OperationCanceledException>(() => connector.ConnectAsync(Endpoint, cancel.Token));
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Cancellation not prompt");
        Check(dialCalls > 0, "In-flight cancellation did not exercise dialer");
    }

    private static async Task DnsFailures()
    {
        var called = false;
        var dnsError = new SocketException((int)SocketError.HostNotFound);
        var failing = new ProtonAddressConnector((_, _) => Task.FromException<IPAddress[]>(dnsError),
            (_, _) => { called = true; return Task.FromResult<Stream>(new MemoryStream()); });
        var actual = await Throws<SocketException>(() => failing.ConnectAsync(Endpoint, default));
        Check(ReferenceEquals(actual, dnsError) && !called, "DNS error must propagate without dialing");
        await Throws<IOException>(() => Make((_, _) => throw new Exception("Must not dial"), []).ConnectAsync(Endpoint, default));
        var stalled = new ProtonAddressConnector((_, _) => new TaskCompletionSource<IPAddress[]>().Task,
            overallTimeout: TimeSpan.FromMilliseconds(70));
        await Throws<TimeoutException>(() => stalled.ConnectAsync(Endpoint, default));
    }

    private static async Task SynchronousDialThrow()
    {
        var calls = 0;
        var connector = Make((_, _) =>
        {
            calls++;
            throw new SocketException((int)SocketError.ConnectionRefused);
        });
        var error = await Throws<IOException>(() => connector.ConnectAsync(Endpoint, default));
        Check(calls == 2 && error.InnerException is AggregateException { InnerExceptions.Count: 2 },
            "Synchronous dial throws did not release attempt slots/retain errors");
    }

    private static async Task LateDialFault()
    {
        foreach (var caller in new[] { false, true })
        {
            var late = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = new CancellationTokenSource();
            var connector = Make((_, _) => late.Task, [Bad], overallMs: caller ? 2000 : 40);
            var connect = connector.ConnectAsync(Endpoint, cancel.Token);
            if (caller) cancel.Cancel();
            if (caller) await Throws<OperationCanceledException>(() => connect);
            else await Throws<TimeoutException>(() => connect);
            late.SetException(new IOException("Synthetic late dial fault"));
            var watch = Stopwatch.StartNew();
            while (!LateDnsTests.IsExceptionObserved(late.Task) && watch.Elapsed < TimeSpan.FromMilliseconds(500))
                await Task.Delay(5);
            Check(LateDnsTests.IsExceptionObserved(late.Task), "Late dial fault not observed");
        }
    }

    private static async Task CancellationAtWinningBoundary()
    {
        using var cancel = new CancellationTokenSource();
        var stream = new TrackedStream();
        var connector = Make((_, _) =>
        {
            cancel.Cancel(); // Success and caller cancellation meet before ownership transfer.
            return Task.FromResult<Stream>(stream);
        }, [Bad]);
        await Throws<OperationCanceledException>(() => connector.ConnectAsync(Endpoint, cancel.Token));
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Check(stream.DisposeCount == 1, "Canceled winning-boundary stream not disposed exactly once");
    }

    private static async Task AllFailed()
    {
        var seen = new List<IPAddress>();
        var connector = Make((ep, _) =>
        {
            seen.Add(ep.Address);
            return Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused));
        }, [Bad, Bad, IPAddress.Loopback]);
        var error = await Throws<IOException>(() => connector.ConnectAsync(Endpoint, default));
        Check(error.InnerException is AggregateException { InnerExceptions.Count: 2 }, "Candidate failures not retained");
        Check(seen.SequenceEqual(Pair), "Must try each distinct candidate exactly once");
    }

    private static async Task AttemptBudgetAndParallelLimit()
    {
        var active = 0;
        var maximum = 0;
        var starts = 0;
        var connector = Make(async (_, ct) =>
        {
            var count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            Interlocked.Increment(ref starts);
            try { await Task.Delay(Timeout.Infinite, ct); return new MemoryStream(); }
            finally { Interlocked.Decrement(ref active); }
        }, Enumerable.Range(1, 6).Select(n => IPAddress.Parse($"192.0.2.{n}")).ToArray(), attemptMs: 70);
        var watch = Stopwatch.StartNew();
        await Throws<IOException>(() => connector.ConnectAsync(Endpoint, default));
        Check(starts == 6 && maximum <= 2 && maximum == 2, "TCP attempt concurrency not bounded to two");
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Per-address timeout not enforced");
    }

    private static async Task OverallBudget()
    {
        var connector = Make(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new MemoryStream(); },
            attemptMs: 2000, overallMs: 70);
        var watch = Stopwatch.StartNew();
        await Throws<TimeoutException>(() => connector.ConnectAsync(Endpoint, default));
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Overall budget not enforced");
    }

    private static async Task LateLoserDisposal()
    {
        var late = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loser = new TrackedStream();
        var winner = new TrackedStream();
        var connector = Make((ep, _) => ep.Address.Equals(Bad) ? late.Task : Task.FromResult<Stream>(winner));
        var returned = await connector.ConnectAsync(Endpoint, default);
        Check(ReferenceEquals(returned, winner) && winner.DisposeCount == 0, "Winner ownership lost");
        late.SetResult(loser); // Simulate dialer completing despite cancellation.
        await loser.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Check(loser.DisposeCount == 1 && winner.DisposeCount == 0, "Late loser must be disposed exactly once, winner retained");
        returned.Dispose();
        Check(winner.DisposeCount == 1, "Caller could not dispose winner");

        var canceledLate = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledStream = new TrackedStream();
        using var cts = new CancellationTokenSource(40);
        await Throws<OperationCanceledException>(() => Make((_, _) => canceledLate.Task, [Bad]).ConnectAsync(Endpoint, cts.Token));
        canceledLate.SetResult(canceledStream);
        await canceledStream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Check(canceledStream.DisposeCount == 1, "Canceled late stream leaked");
    }

    private static async Task SimultaneousWinners()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streams = new[] { new TrackedStream(), new TrackedStream() };
        var count = 0;
        var connector = Make(async (_, _) =>
        {
            var n = Interlocked.Increment(ref count) - 1;
            if (n == 1) bothStarted.SetResult();
            await release.Task;
            return streams[n];
        });
        var connect = connector.ConnectAsync(Endpoint, default);
        await bothStarted.Task;
        release.SetResult();
        using var result = await connect;
        var loser = streams.Single(s => !ReferenceEquals(s, result));
        await loser.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Check(loser.DisposeCount == 1 && ((TrackedStream)result).DisposeCount == 0, "Race must dispose only losing stream");
    }

    private static Task RealLoopbackV4() => RealLoopback(IPAddress.Loopback);
    private static Task RealLoopbackV6() => RealLoopback(IPAddress.IPv6Loopback);
    private static async Task RealLoopback(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            var connector = new ProtonAddressConnector((_, _) => Task.FromResult(new[] { address }));
            using var stream = await connector.ConnectAsync(new DnsEndPoint("test.invalid", ((IPEndPoint)listener.LocalEndpoint).Port), default);
            Check(stream is NetworkStream, "Production dialer did not return raw NetworkStream");
            using var peer = await accept;
            await stream.WriteAsync(new byte[] { 73 });
            var buffer = new byte[1];
            Check(await peer.GetStream().ReadAsync(buffer) == 1 && buffer[0] == 73, "Production socket did not carry bytes");
            stream.Dispose();
            Check(await peer.GetStream().ReadAsync(buffer) == 0, "Disposing winning stream did not close socket");
        }
        finally { listener.Stop(); }
    }

    private static async Task RealSocketLoserClosed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connector = Make(async (ep, _) =>
            {
                if (!ep.Address.Equals(Bad)) { await connected.Task; return new MemoryStream(); }
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(listener.LocalEndpoint);
                connected.SetResult();
                await release.Task;
                return new NetworkStream(socket, ownsSocket: true);
            });
            using var winner = await connector.ConnectAsync(Endpoint, default);
            using var peer = await accept;
            release.SetResult();
            var buffer = new byte[1];
            Check(await peer.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(1)) == 0, "Losing socket remained open");
        }
        finally { listener.Stop(); }
    }

    private static async Task FactoryWiring()
    {
        var method = typeof(ProtonHttpClientFactory).GetMethod("CreateHttpClient",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(string), typeof(ProtonAddressConnector)]);
        Check(method is not null, "Missing factory DNS/dialer injection seam");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var dnsCalls = 0;
            var connector = new ProtonAddressConnector((_, _) => { dnsCalls++; return Task.FromResult(new[] { IPAddress.Loopback }); });
            using var normal = (HttpClient)method!.Invoke(null, new object?[] { "true", connector })!;
            Check(dnsCalls == 0, "Normal mode used connector");
            using var client = (HttpClient)method.Invoke(null, new object?[] { "1", connector })!;
            var server = ServeHttp(listener);
            var body = await client.GetStringAsync($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/content");
            await server;
            Check(body == "ok" && dnsCalls == 1, "Factory did not wire production connector");
        }
        finally { listener.Stop(); }
    }

    private static async Task ConfiguredProxy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var resolved = "";
            var connector = new ProtonAddressConnector((host, _) => { resolved = host; return Task.FromResult(new[] { IPAddress.Loopback }); });
            using var handler = ProtonHttpClientFactory.CreateCompatibilityHandler(connector);
            handler.Proxy = new WebProxy($"http://proxy.invalid:{((IPEndPoint)listener.LocalEndpoint).Port}");
            Check(handler.UseProxy, "Proxy use disabled");
            using var client = new HttpClient(handler);
            var server = ServeHttp(listener);
            Check(await client.GetStringAsync("http://origin.invalid/content") == "ok", "Explicit proxy not used");
            var request = await server;
            Check(resolved == "proxy.invalid", "Connector resolved origin instead of configured proxy");
            Check(request.StartsWith("GET http://origin.invalid/content HTTP/1.1"), "HTTP proxy semantics not preserved");
        }
        finally { listener.Stop(); }
    }

    private static async Task RejectsUntrustedTls()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync();
                using var tls = new System.Net.Security.SslStream(peer.GetStream());
                try { await tls.AuthenticateAsServerAsync(certificate); }
                catch (System.Security.Authentication.AuthenticationException) { }
                catch (IOException) { }
            });
            using var client = new HttpClient(ProtonHttpClientFactory.CreateCompatibilityHandler(
                new ProtonAddressConnector((_, _) => Task.FromResult(new[] { IPAddress.Loopback }))));
            var error = await Throws<HttpRequestException>(() => client.GetAsync($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"));
            Check(error.InnerException is System.Security.Authentication.AuthenticationException, "Untrusted certificate did not fail standard TLS validation");
            await server;
        }
        finally { listener.Stop(); }
    }

    private static async Task<string> ServeHttp(TcpListener listener)
    {
        using var peer = await listener.AcceptTcpClientAsync();
        using var reader = new StreamReader(peer.GetStream(), leaveOpen: true);
        var first = await reader.ReadLineAsync() ?? "";
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
        await peer.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        return first;
    }

    private sealed class TrackedStream : MemoryStream
    {
        public int DisposeCount;
        public readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Interlocked.Increment(ref DisposeCount); Disposed.TrySetResult(); }
            base.Dispose(disposing);
        }
    }
}
