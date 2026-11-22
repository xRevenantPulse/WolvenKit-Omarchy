using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using WolvenKit.App.Helpers;

internal static class LateDnsTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static Task TimeoutFault() => Verify(false, true);
    public static Task CallerFault() => Verify(true, true);
    public static Task TimeoutSuccess() => Verify(false, false);
    public static Task CallerSuccess() => Verify(true, false);

    private static async Task Verify(bool caller, bool fault)
    {
        var weak = await ReleaseResolver(caller, fault);
        // Do not accept a permanently rooted resolver as an observation fix.
        for (var i = 0; i < 40 && weak.IsAlive; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(10);
        }
        Check(!weak.IsAlive, "Completed resolver task remained rooted");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ReleaseResolver(bool caller, bool fault)
    {
        var source = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var connector = new ProtonAddressConnector((_, _) => source.Task,
            (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<Stream>(new MemoryStream()); },
            overallTimeout: TimeSpan.FromMilliseconds(caller ? 2000 : 40));
        using var cancel = new CancellationTokenSource();
        var connect = connector.ConnectAsync(new DnsEndPoint("test.invalid", 443), cancel.Token);
        if (caller) cancel.Cancel();
        Exception? error = null;
        try { using var stream = await connect.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception ex) { error = ex; }
        Check(caller ? error is OperationCanceledException : error is TimeoutException,
            "Original DNS cancellation/timeout replaced or delayed");
        Check(!source.Task.IsCompleted && calls == 0, "Did not return before non-cooperative DNS completion");
        // Connection completion can precede WaitAsync's source-continuation removal.
        // Test-only runtime inspection makes late fault injection wait for actual
        // detachment, rather than relying on a scheduling-sensitive settling sleep.
        var detachment = System.Diagnostics.Stopwatch.StartNew();
        while (HasWaitAsyncContinuation(source.Task) && detachment.Elapsed < TimeSpan.FromSeconds(1))
            await Task.Delay(5);
        Check(!HasWaitAsyncContinuation(source.Task), "WaitAsync source continuation did not detach");
        if (fault)
        {
            source.SetException(new InvalidOperationException("Synthetic late DNS fault"));
            // Test-only .NET 10 runtime inspection: reading the handled bit does NOT
            // observe Task.Exception. Fail closed if runtime layout changes.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!IsExceptionObserved(source.Task) && watch.Elapsed < TimeSpan.FromMilliseconds(500))
                await Task.Delay(5);
            Check(IsExceptionObserved(source.Task), "Late DNS fault was not observed (test-only .NET10 holder inspection)");
        }
        else source.SetResult([IPAddress.Loopback]);
        await Task.Delay(30);
        Check(calls == 0, "Late DNS completion started dialing after connection returned");
        return new WeakReference(source.Task);
    }

    private static bool HasWaitAsyncContinuation(Task task)
    {
        var field = typeof(Task).GetField("m_continuationObject", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("Runtime inspection unavailable: continuation object");
        var continuation = field.GetValue(task);
        static bool IsWaitAsync(object? value) => value?.GetType().Name.StartsWith("CancellationPromise", StringComparison.Ordinal) == true;
        if (continuation is List<object> list)
        {
            lock (list) return list.Any(IsWaitAsync);
        }
        return IsWaitAsync(continuation);
    }

    internal static bool IsExceptionObserved(Task task)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var properties = typeof(Task).GetField("m_contingentProperties", Flags)?.GetValue(task)
            ?? throw new Exception("Runtime inspection unavailable: contingent properties");
        var holder = properties.GetType().GetField("m_exceptionsHolder", Flags | BindingFlags.Public)?.GetValue(properties)
            ?? throw new Exception("Runtime inspection unavailable: exception holder");
        return (bool)(holder.GetType().GetField("m_isHandled", Flags)?.GetValue(holder)
            ?? throw new Exception("Runtime inspection unavailable: handled flag"));
    }
}
