using System.IO.Compression;
using System.Text;
namespace WolvenKit.App.Helpers;
static class Baseline { static void Log(object value) {}
    public static async Task<bool> Run(HttpClient s_httpClient, string baseDirectory)
    {
        // check remote version (no github API call)
        var hashUrl = $@"https://wolvenkit.github.io/Wolvenkit-Resources/templatehash.txt";
        var response = await s_httpClient.GetAsync(new Uri(hashUrl));

        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Log($"Failed to respond to url: {hashUrl}");
            Log(ex);
            return false;
        }

        var localHash = "";
        var resourcesDir = Path.Combine(baseDirectory, "Resources");
        var templatesDir = new DirectoryInfo(Path.Combine(resourcesDir, "Templates"));
        FileInfo hashPath = new(Path.Combine(resourcesDir, "templatehash.txt"));
        if (hashPath.Exists)
        {
            localHash = await File.ReadAllTextAsync(hashPath.FullName);
        }

        using var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms);
        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        var remoteHash = await reader.ReadToEndAsync();
        remoteHash = remoteHash.TrimEnd('\r', '\n');

        if (localHash == remoteHash)
        {
            return true;
        }

        // clean
        if (templatesDir.Exists)
        {
            templatesDir.Delete(true);
        }
        Directory.CreateDirectory(templatesDir.FullName);

        // download zip file
        var contentUrl = $@"https://wolvenkit.github.io/Wolvenkit-Resources/templates.zip";
        response = await s_httpClient.GetAsync(new Uri(contentUrl));
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Log($"Failed update scripts from {contentUrl}");
            Log($"\t{ex.Message}");
            return false;
        }

        var zip = await response.Content.ReadAsStreamAsync();
        zip.Seek(0, SeekOrigin.Begin);
        ZipArchive zipArchive = new(zip);
        zipArchive.ExtractToDirectory(templatesDir.FullName, true);

        await File.WriteAllTextAsync(hashPath.FullName, remoteHash);
        return true;
    }
}
