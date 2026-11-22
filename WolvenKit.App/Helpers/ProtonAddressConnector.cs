using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WolvenKit.App.Helpers;

/// <summary>
/// Races at most two TCP connections, staggering starts so one blackholed DNS
/// address cannot monopolize the connection budget. Returns plaintext TCP only;
/// SocketsHttpHandler still performs proxy negotiation and TLS authentication.
/// </summary>
internal sealed class ProtonAddressConnector
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, Task<Stream>> _dial;
    private readonly TimeSpan _stagger;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _overallTimeout;

    public ProtonAddressConnector(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPEndPoint, CancellationToken, Task<Stream>>? dial = null,
        TimeSpan? stagger = null,
        TimeSpan? attemptTimeout = null,
        TimeSpan? overallTimeout = null)
    {
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _dial = dial ?? DialAsync;
        _stagger = stagger ?? TimeSpan.FromMilliseconds(200);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(2);
        _overallTimeout = overallTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(_overallTimeout);
        var active = new List<Task<Stream>>();
        Task<IPAddress[]>? resolution = null;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            resolution = _resolve(endpoint.Host, lifetime.Token);
            var addresses = await resolution.WaitAsync(lifetime.Token).ConfigureAwait(false);
            var firstFamily = addresses.FirstOrDefault()?.AddressFamily;
            var preferred = new Queue<IPAddress>(addresses.Distinct().Where(a => a.AddressFamily == firstFamily));
            var alternate = new Queue<IPAddress>(addresses.Distinct().Where(a => a.AddressFamily != firstFamily));
            var candidates = new List<IPAddress>();
            while (preferred.Count > 0 || alternate.Count > 0)
            {
                if (preferred.TryDequeue(out var first)) candidates.Add(first);
                if (alternate.TryDequeue(out var other)) candidates.Add(other);
            }
            var errors = new List<Exception>();
            var index = 0;
            Task nextStart = Task.CompletedTask;
            while (index < candidates.Count || active.Count > 0)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (index < candidates.Count && active.Count < 2 && nextStart.IsCompleted)
                {
                    active.Add(AttemptAsync(new IPEndPoint(candidates[index++], endpoint.Port), lifetime.Token));
                    nextStart = Task.Delay(_stagger, lifetime.Token);
                }

                var waiters = active.Cast<Task>().ToList();
                if (index < candidates.Count && active.Count < 2) waiters.Add(nextStart);
                var completed = await Task.WhenAny(waiters).WaitAsync(lifetime.Token).ConfigureAwait(false);
                if (completed == nextStart) continue;
                var attempt = (Task<Stream>)completed;
                active.Remove(attempt);
                try
                {
                    var stream = await attempt.ConfigureAwait(false);
                    if (lifetime.IsCancellationRequested)
                    {
                        stream.Dispose();
                        lifetime.Token.ThrowIfCancellationRequested();
                    }
                    return stream;
                }
                catch (Exception ex) when (!lifetime.IsCancellationRequested)
                {
                    errors.Add(ex);
                    nextStart = Task.CompletedTask; // A failed address need not delay the next one.
                }
            }
            throw new IOException($"No TCP address could connect to {endpoint.Host}:{endpoint.Port}.", new AggregateException(errors));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"TCP connection budget exhausted for {endpoint.Host}:{endpoint.Port}.", ex);
        }
        finally
        {
            lifetime.Cancel();
            if (resolution is not null) _ = ObserveResolutionAsync(resolution);
            foreach (var task in active) _ = DisposeResultAsync(task);
        }
    }

    private static async Task ObserveResolutionAsync(Task<IPAddress[]> task)
    {
        // WaitAsync may detach on cancellation. Observe a non-cooperative resolver's
        // eventual fault without delaying return or changing the original failure.
        try { await task.ConfigureAwait(false); }
        catch { /* Address arrays own no disposable resources. */ }
    }

    private async Task<Stream> AttemptAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_attemptTimeout);
        var pending = _dial(endpoint, attempt.Token);
        try
        {
            // The real dialer closes its socket before completing cancellation. Await
            // that completion before recycling a race slot; WaitAsync here could
            // start a third socket while a canceled predecessor is still closing.
            // The outer connection budget still bounds non-cooperative test dialers.
            var stream = await pending.ConfigureAwait(false);
            attempt.Token.ThrowIfCancellationRequested();
            return stream;
        }
        catch
        {
            // Also covers a dialer which completes successfully after cancellation.
            // Ownership of any eventual result must not escape on an abandoned race.
            _ = DisposeResultAsync(pending);
            throw;
        }
    }

    private static async Task DisposeResultAsync(Task<Stream> task)
    {
        try { (await task.ConfigureAwait(false)).Dispose(); }
        catch { /* Observe losing task exceptions; never replace the winning result. */ }
    }

    private static async Task<Stream> DialAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
