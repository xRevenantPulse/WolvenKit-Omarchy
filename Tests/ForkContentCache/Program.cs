using System.IO.Compression;
using System.Net;
using WolvenKit.App.Helpers;

var scenario = args.FirstOrDefault() ?? "http";
var root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "wk-cache-test-" + Guid.NewGuid().ToString("N"));
var live = Path.Combine(root, "Resources", scenario == "baseline-scripts" ? "Scripts" : "Templates");
var marker = Path.Combine(root, "Resources", scenario == "baseline-scripts" ? "scripthash.txt" : "templatehash.txt");
Directory.CreateDirectory(live);
File.WriteAllText(Path.Combine(live, "old.txt"), "old-content");
File.WriteAllText(marker, "old-marker");
using var gate = new SemaphoreSlim(scenario is "lock-cancel" or "lock-wait-cancel" ? 0 : 1, 1);
using var cancel = new CancellationTokenSource();
using var fixture = new Fixture(scenario, cancel, args.ElementAtOrDefault(1));
using var client = new HttpClient(fixture);
using var traceText = new StringWriter();
using var traceListener = new System.Diagnostics.TextWriterTraceListener(traceText);
if (scenario == "cleanup-observer") System.Diagnostics.Trace.Listeners.Add(traceListener);
Exception? error = null;
Exception? cleanupWarning = null;
FileStream? cleanupLock = null;
string? protectedDirectory = null;
void BlockCleanup(string transaction)
{
    protectedDirectory = Path.Combine(transaction, "cleanup-blocked");
    Directory.CreateDirectory(protectedDirectory);
    var file = Path.Combine(protectedDirectory, "block.txt");
    File.WriteAllText(file, "cleanup fixture");
    if (OperatingSystem.IsWindows()) cleanupLock = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
    else File.SetUnixFileMode(protectedDirectory, (UnixFileMode)0);
}
var directoryMoves = 0;
var changed = false;
try
{
    if (scenario == "lock-cancel") cancel.Cancel();
    try
    {
        if (scenario == "baseline-scripts") await ScriptBaseline.Run(client, root);
        else if (scenario == "baseline") await Baseline.Run(client, root);
        else if (scenario == "lock-wait-cancel")
        {
            var pending = TransactionalContentUpdater.UpdateAsync(client, root, "Templates", "templatehash.txt",
                new Uri("https://fixture/templatehash.txt"), new Uri("https://fixture/templates.zip"), gate, cancel.Token);
            await Task.Delay(50);
            if (pending.IsCompleted || cancel.IsCancellationRequested || fixture.Requests != 0)
                throw new Exception("Lock fixture must actually wait before cancellation without HTTP");
            cancel.Cancel();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else changed = await TransactionalContentUpdater.UpdateAsync(client, root, "Templates", "templatehash.txt",
            new Uri("https://fixture/templatehash.txt"), new Uri("https://fixture/templates.zip"), gate, cancel.Token,
            (source, destination) => { directoryMoves++; if (scenario == "content-commit" && source.EndsWith("new-content")) throw new IOException("Injected content rename failure"); Directory.Move(source, destination); if ((scenario is "cleanup-success" or "cleanup-observer") && source.EndsWith("new-content")) BlockCleanup(Path.GetDirectoryName(source)!); },
            (source, destination) => {
                if (scenario == "rollback-failure" && source.EndsWith("new-marker"))
                {
                    protectedDirectory = live;
                    if (OperatingSystem.IsWindows()) cleanupLock = new FileStream(Path.Combine(live, "new.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
                    else File.SetUnixFileMode(live, (UnixFileMode)0);
                    throw new IOException("Injected marker commit failure before rollback fault");
                }
                if (scenario == "marker-backup" && source == marker) throw new IOException("Injected marker backup failure"); if (scenario == "cleanup-error" && source.EndsWith("new-marker")) { BlockCleanup(Path.GetDirectoryName(source)!); throw new InvalidOperationException("Original unknown commit error"); } if (scenario == "hash-commit" && source.EndsWith("new-marker")) throw new IOException("Injected marker rename failure"); File.Move(source, destination); }, ex => { cleanupWarning = ex; if (scenario == "cleanup-observer") throw new InvalidOperationException("Injected observer failure"); });
    }
    catch (Exception ex) { error = ex; }
    void Assert(bool condition, string message) { if (!condition) throw new Exception("ASSERT " + scenario + ": " + message + "; actual=" + error); }
    if (scenario == "rollback-failure")
    {
        Assert(error is AggregateException a && a.InnerExceptions.Count == 2 && a.InnerExceptions[0].Message == "Injected marker commit failure before rollback fault" && a.InnerExceptions[1] is IOException or UnauthorizedAccessException, "must preserve both commit and rollback causes");
        Assert(!File.Exists(marker), "must not restore old marker when content rollback failed");
        var recovery = Directory.GetDirectories(Path.Combine(root, "Resources"), ".content-update-*").Single();
        Assert(error!.Message.Contains(recovery), "must identify recovery directory");
        Assert(File.ReadAllText(Path.Combine(recovery, "old-marker")) == "old-marker", "backup marker lost");
        Assert(File.ReadAllText(Path.Combine(recovery, "old-content", "old.txt")) == "old-content", "backup content lost");
    }
    else if (scenario == "tree-valid")
    {
        Assert(error == null && changed, "consistent explicit/implicit tree should commit");
        foreach (var path in args[1].Split(';').Where(p => !p.EndsWith('/')))
            Assert(File.ReadAllText(Path.Combine(live, path)) == "new-content", "tree entry changed");
        Assert(File.ReadAllText(marker) == "new-marker", "new marker missing");
    }
    else if (scenario == "artifact")
    {
        Assert(error == null && changed, "real artifact should stage and commit");
        using var archive = ZipFile.OpenRead(args[1]);
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            using var original = entry.Open();
            using var expected = new MemoryStream();
            original.CopyTo(expected);
            Assert(File.ReadAllBytes(Path.Combine(live, entry.FullName)).SequenceEqual(expected.ToArray()), "artifact entry differs: " + entry.FullName);
        }
        Assert(!File.Exists(Path.Combine(live, "old.txt")), "old content not replaced");
        Console.WriteLine("Verified real artifact entries=" + archive.Entries.Count);
    }
    else if (scenario is "success" or "cleanup-success" or "cleanup-observer")
    {
        Assert(error == null && changed, "update should succeed");
        if (scenario == "cleanup-observer")
        {
            System.Diagnostics.Trace.Flush();
            Assert(cleanupWarning is IOException && traceText.ToString().Contains("temporary backup cleanup failed") && traceText.ToString().Contains("Injected observer failure"), "trace must preserve cleanup warning and observer failure");
        }
        if (scenario == "cleanup-success") Assert(cleanupWarning is IOException, "cleanup failure must be reported as a warning");
        Assert(File.ReadAllText(Path.Combine(live, "new.txt")) == "new-content", "new content missing");
        Assert(!File.Exists(Path.Combine(live, "old.txt")), "old content retained");
        Assert(File.ReadAllText(marker) == "new-marker", "new marker missing");
    }
    else
    {
        if (scenario == "marker-backup") Assert(error is IOException && directoryMoves == 0, "marker backup failure must precede any content mutation");
        else if (scenario == "unchanged") Assert(error == null && !changed && fixture.ArchiveRequests == 0, "unchanged marker must skip archive");
        else if (!scenario.StartsWith("baseline"))
        {
            var expected = scenario switch
            {
                "cleanup-error" => error is AggregateException aggregate && aggregate.InnerExceptions.Any(ex => ex is InvalidOperationException),
                "http" or "hash-http" => error is HttpRequestException,
                "timeout" or "cancel" or "lock-cancel" or "lock-wait-cancel" => error is OperationCanceledException,
                "content-commit" or "hash-commit" or "stage-io" => error is IOException,
                _ => error is InvalidDataException
            };
            Assert(expected, "expected update failure type");
        }
        Assert(File.Exists(Path.Combine(live, "old.txt")) && File.ReadAllText(Path.Combine(live, "old.txt")) == "old-content", "previous content lost");
        Assert(File.ReadAllText(marker) == "old-marker", "previous marker lost");
    }
    if (scenario is not ("cleanup-success" or "cleanup-error" or "cleanup-observer" or "rollback-failure")) Assert(Directory.GetDirectories(Path.Combine(root, "Resources"), ".content-update-*").Length == 0, "transaction leaked");
    if (scenario == "lock-wait-cancel") Assert(gate.CurrentCount == 0 && fixture.Requests == 0, "canceled waiter must not release gate or perform HTTP");
    Assert(!File.Exists(Path.Combine(root, "Resources", "escape.txt")), "archive escaped stage");
    Console.WriteLine("PASS " + scenario + " (fake HTTP, real IO)");
}
finally { cleanupLock?.Dispose(); if (protectedDirectory != null && Directory.Exists(protectedDirectory) && !OperatingSystem.IsWindows()) File.SetUnixFileMode(protectedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); Directory.Delete(root, true); }

sealed class Fixture(string scenario, CancellationTokenSource cancel, string? archivePath) : HttpMessageHandler
{
    public static byte[] MakeZip(string[] paths)
    {
        using var data = new MemoryStream();
        using (var archive = new ZipArchive(data, ZipArchiveMode.Create, true))
            foreach (var path in paths)
            {
                var entry = archive.CreateEntry(path);
                if (!path.EndsWith('/')) { using var writer = new StreamWriter(entry.Open()); writer.Write("new-content"); }
            }
        return data.ToArray();
    }
    public int Requests;
    public int ArchiveRequests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        var zip = request.RequestUri!.AbsolutePath.EndsWith(".zip");
        if (zip) ArchiveRequests++;
        if ((zip && scenario is "http" or "baseline" or "baseline-scripts") || (!zip && scenario == "hash-http"))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        if (zip && scenario == "timeout") throw new TaskCanceledException("Injected HTTP timeout");
        if (zip && scenario == "cancel") { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
        if (!zip) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(scenario == "unchanged" ? "old-marker\n" : "new-marker\n") });
        if (scenario is "tree" or "tree-valid") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MakeZip((archivePath ?? "Dir/a.txt;dir/b.txt").Split(';'))) });
        if (scenario == "superscript") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MakeZip([archivePath ?? "COM¹.txt"])) });
        if (scenario == "artifact") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(archivePath!)) });
        byte[] bytes;
        using (var data = new MemoryStream())
        {
            if (scenario == "corrupt") bytes = [1, 2, 3];
            else
            {
                using (var archive = new ZipArchive(data, ZipArchiveMode.Create, true))
                {
                    var path = scenario switch { "stage-io" => new string('x', 300), "traversal" => "../escape.txt", "backslash" => "..\\escape.txt", "absolute" => "/escape.txt", "drive" => "C:/escape.txt", "reserved" => "CON.txt", "ads" => "file:stream", _ => "new.txt" };
                    if (scenario != "empty")
                    {
                        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                        if (scenario == "symlink") entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                        using (var writer = new StreamWriter(entry.Open())) writer.Write("new-content");
                        if (scenario == "stage") { using var writer = new StreamWriter(archive.CreateEntry("new.txt/child.txt").Open()); writer.Write("file-directory collision"); }
                        if (scenario == "duplicate") { using var writer = new StreamWriter(archive.CreateEntry("NEW.TXT").Open()); writer.Write("collision"); }
                    }
                }
                bytes = data.ToArray();
                if (scenario == "crc")
                {
                    // Locate raw ZIP bytes; headers can contain non-ASCII values.
                    for (var i = 0; i <= bytes.Length - 11; i++)
                    {
                        if (bytes.AsSpan(i, 11).SequenceEqual("new-content"u8)) { bytes[i] ^= 1; break; }
                    }
                }
            }
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
