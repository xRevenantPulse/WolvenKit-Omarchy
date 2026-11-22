using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WolvenKit.App.Helpers;

/// <summary>Stages a resource archive before replacing content and its opaque update marker.
/// Not a crash-recovery protocol: callers must serialize updates to the same cache.</summary>
internal static class TransactionalContentUpdater
{
    internal static readonly SemaphoreSlim ScriptGate = new(1, 1);
    internal static async Task<bool> UpdateAsync(HttpClient client, string baseDirectory,
        string contentName, string markerName, Uri markerUri, Uri archiveUri,
        SemaphoreSlim gate, CancellationToken cancellationToken = default,
        Action<string, string>? moveDirectory = null, Action<string, string>? moveFile = null,
        Action<Exception>? cleanupWarning = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var resources = Path.Combine(Path.GetFullPath(baseDirectory), "Resources");
            var live = Path.Combine(resources, contentName);
            var marker = Path.Combine(resources, markerName);
            using var response = await client.GetAsync(markerUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var remote = (await response.Content.ReadAsStringAsync(cancellationToken)).TrimEnd('\r', '\n');
            if (File.Exists(marker) && await File.ReadAllTextAsync(marker, cancellationToken) == remote)
            {
                return false;
            }

            // Same-volume private staging permits rename-based commit. Never use process CWD.
            Directory.CreateDirectory(resources);
            var transaction = Path.Combine(resources, ".content-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transaction);
            var stage = Path.Combine(transaction, "new-content");
            var old = Path.Combine(transaction, "old-content");
            var stagedMarker = Path.Combine(transaction, "new-marker");
            var oldMarker = Path.Combine(transaction, "old-marker");
            var preserveRecovery = false;
            var committed = false;
            Exception? updateError = null;
            try
            {
                using var archiveResponse = await client.GetAsync(archiveUri, cancellationToken);
                archiveResponse.EnsureSuccessStatusCode();
                await using var input = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var archive = new ZipArchive(input, ZipArchiveMode.Read);
                if (!System.Linq.Enumerable.Any(archive.Entries, entry => !entry.FullName.EndsWith('/')))
                {
                    throw new InvalidDataException("Resource archive contains no files.");
                }
                Directory.CreateDirectory(stage);
                var paths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var components = new System.Collections.Generic.Dictionary<string, (string Spelling, bool Directory)>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    if (!paths.Add(entry.FullName.TrimEnd('/')))
                    {
                        throw new InvalidDataException("Archive contains colliding paths: " + entry.FullName);
                    }
                    var segments = entry.FullName.TrimEnd('/').Split('/');
                    var componentPath = "";
                    for (var index = 0; index < segments.Length; index++)
                    {
                        var segment = segments[index];
                        componentPath = index == 0 ? segment : componentPath + "/" + segment;
                        var isDirectory = index < segments.Length - 1 || entry.FullName.EndsWith('/');
                        if (components.TryGetValue(componentPath, out var existing) &&
                            (existing.Spelling != componentPath || existing.Directory != isDirectory))
                        {
                            throw new InvalidDataException("Archive contains inconsistent path components: " + entry.FullName);
                        }
                        components[componentPath] = (componentPath, isDirectory);
                        var stem = segment.Split('.')[0].ToUpperInvariant();
                        if (segment.Length == 0 || segment == "." || segment == ".." ||
                            segment.EndsWith('.') || segment.EndsWith(' ') ||
                            segment.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0 ||
                            System.Linq.Enumerable.Any(segment, c => c < 32) ||
                            stem is "CON" or "PRN" or "AUX" or "NUL" ||
                            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
                        {
                            throw new InvalidDataException("Archive contains an unsafe Windows path: " + entry.FullName);
                        }
                    }
                    if (entry.FullName.Contains('\\'))
                    {
                        throw new InvalidDataException("Archive uses ambiguous path separators: " + entry.FullName);
                    }
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    {
                        throw new InvalidDataException("Archive contains a symbolic link: " + entry.FullName);
                    }
                }
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                    if (!destination.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Archive path escapes staging directory.");
                    }
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var source = entry.Open();
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920];
                    uint crc = 0xFFFFFFFF;
                    long length = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                    {
                        for (var i = 0; i < count; i++)
                        {
                            crc ^= buffer[i];
                            for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u); }
                        }
                        length += count;
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                    if (length != entry.Length || ~crc != entry.Crc32)
                    {
                        throw new InvalidDataException("Archive entry failed length/CRC validation: " + entry.FullName);
                    }
                }
                await File.WriteAllTextAsync(stagedMarker, remote, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                moveDirectory ??= Directory.Move;
                moveFile ??= (source, destination) => File.Move(source, destination);
                var backedContent = false;
                var installedContent = false;
                var backedMarker = false;
                var installedMarker = false;
                // No cancellation within the short commit/rollback critical section.
                try
                {
                    if (File.Exists(marker)) { moveFile(marker, oldMarker); backedMarker = true; }
                    if (Directory.Exists(live)) { moveDirectory(live, old); backedContent = true; }
                    moveDirectory(stage, live); installedContent = true;
                    moveFile(stagedMarker, marker); installedMarker = true;
                }
                catch (Exception commitError)
                {
                    try
                    {
                        if (installedMarker) { File.Delete(marker); }
                        // An old marker is valid again only after old content is restored.
                        if (installedContent) { Directory.Delete(live, true); }
                        if (backedContent) { Directory.Move(old, live); }
                        if (backedMarker) { File.Move(oldMarker, marker); }
                    }
                    catch (Exception rollbackError)
                    {
                        preserveRecovery = true;
                        throw new AggregateException($"Content rollback failed; recovery data retained at {transaction}", commitError, rollbackError);
                    }
                    throw;
                }
                committed = true;
                return true;
            }
            catch (Exception ex)
            {
                updateError = ex;
                throw;
            }
            finally
            {
                if (!preserveRecovery)
                {
                    try { Directory.Delete(transaction, true); }
                    catch (Exception ex) when (committed && ex is IOException or UnauthorizedAccessException)
                    {
                        var warning = new IOException($"Resource update committed, but temporary backup cleanup failed at {transaction}", ex);
                        try
                        {
                            if (cleanupWarning != null) { cleanupWarning(warning); }
                            else { System.Diagnostics.Trace.TraceWarning(warning.ToString()); }
                        }
                        catch (Exception observerError)
                        {
                            // Diagnostics must not turn an already committed update into failure.
                            try { System.Diagnostics.Trace.TraceWarning(new AggregateException("Committed resource cleanup warning observer failed", warning, observerError).ToString()); }
                            catch (Exception) { /* A failing trace listener cannot undo the commit either. */ }
                        }
                    }
                    catch (Exception cleanupError) when (updateError != null)
                    {
                        throw new AggregateException($"Resource update failed and staging cleanup also failed at {transaction}", updateError, cleanupError);
                    }
                }
            }
        }
        finally { gate.Release(); }
    }
}
