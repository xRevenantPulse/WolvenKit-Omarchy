using System.IO.Compression;
using System.Text;
namespace WolvenKit.App.Helpers;
static class ScriptBaseline { static readonly Logger _loggerService = new(); static readonly Scripts _scriptService=new(); class Logger { public void Error(object value) {} public void Warning(object value) {} } class Scripts { public void RefreshUIScripts() {} }
    public static async Task Run(HttpClient client, string baseDirectory)
    {
        // check remote version (no github API call)
        var hashUrl = $@"https://wolvenkit.github.io/Wolvenkit-Resources/hash.txt";
        var response = await client.GetAsync(new Uri(hashUrl));

        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _loggerService.Error($"Failed to respond to url: {hashUrl}");
            _loggerService.Error(ex);
            return;
        }

        var localHash = "";
        var resourceDir = new DirectoryInfo(Path.Combine(baseDirectory, "Resources", "Scripts"));
        FileInfo hashPath = new(Path.Combine(baseDirectory, "Resources", "scripthash.txt"));
        if (hashPath.Exists)
        {
            localHash = await File.ReadAllTextAsync(hashPath.FullName);
        }

        // check hash
        using var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms);
        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        var remoteHash = await reader.ReadToEndAsync();
        remoteHash = remoteHash.TrimEnd('\r', '\n');
        if (localHash == remoteHash)
        {
            // early out
            return;
        }

        // clean
        if (resourceDir.Exists)
        {
            resourceDir.Delete(true);
        }
        Directory.CreateDirectory(resourceDir.FullName);

        // download zip file
        var contentUrl = $@"https://wolvenkit.github.io/Wolvenkit-Resources/scripts.zip";
        response = await client.GetAsync(new Uri(contentUrl));
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _loggerService.Warning($"Failed update scripts from {contentUrl}");
            _loggerService.Warning($"\t{ex.Message}");
            return;
        }

        var zip = await response.Content.ReadAsStreamAsync();
        zip.Seek(0, SeekOrigin.Begin);
        ZipArchive zipArchive = new(zip);
        zipArchive.ExtractToDirectory(resourceDir.FullName, true);

        await File.WriteAllTextAsync(hashPath.FullName, remoteHash);
        _scriptService.RefreshUIScripts();
    }

}
