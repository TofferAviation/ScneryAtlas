using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SceneryAtlas.Core;

public record UpdateStatus(string InstalledVersion, string? LatestVersion, bool Available, string? InstallerPath, string Message, string? Folder, string Source = "local");
public record UpdateManifest(string Version, string Installer, string Sha256);

public static class UpdateChannel
{
    public const string PublicManifestUrl = "https://github.com/TofferAviation/ScneryAtlas/releases/latest/download/latest.json";
    private const string PublicAssetBase = "https://github.com/TofferAviation/ScneryAtlas/releases/latest/download/";
    private const long MaxInstallerBytes = 1_000_000_000;
    private static string Setting(string dataDirectory) => Path.Combine(dataDirectory, "release-folder.json");

    private static (UpdateManifest Manifest, Version Latest) ParseManifest(string json)
    {
        if (json.Length > 4096) throw new InvalidDataException("Update manifest is too large.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, Json.Options)
            ?? throw new InvalidDataException("Update manifest is empty.");
        if (!Version.TryParse(manifest.Version, out var latest) || latest.Major < 0 || latest.Minor < 0 || latest.Build < 0 || latest.Revision >= 0)
            throw new InvalidDataException("Update version is invalid.");
        var version = latest.ToString(3);
        if (manifest.Installer != $"SceneryAtlas-{version}-Setup.exe" ||
            !Regex.IsMatch(manifest.Sha256 ?? "", "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("Update file or checksum is invalid.");
        return (manifest, latest);
    }

    private static async Task<UpdateManifest?> ReadOnlineManifest(HttpClient http, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(PublicManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps || response.Content.Headers.ContentLength > 4096)
            throw new InvalidDataException("Invalid public update response.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellation)) > 0)
        {
            if (buffer.Length + count > 4096) throw new InvalidDataException("Update manifest is too large.");
            buffer.Write(chunk, 0, count);
        }
        return ParseManifest(System.Text.Encoding.UTF8.GetString(buffer.ToArray())).Manifest;
    }

    public static async Task<UpdateStatus> CheckOnlineAsync(HttpClient http, Version installed, CancellationToken cancellation = default)
    {
        var label = installed.ToString(3);
        try
        {
            var manifest = await ReadOnlineManifest(http, cancellation);
            if (manifest is null) return new(label, null, false, null, "No public Scenery Atlas release has been published yet.", null, "github");
            var latest = Version.Parse(manifest.Version);
            return latest > installed
                ? new(label, latest.ToString(3), true, null, "New version available from GitHub Releases.", null, "github")
                : new(label, latest.ToString(3), false, null, "You have the latest public release.", null, "github");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidDataException or ArgumentException)
        {
            return new(label, null, false, null, "Could not check GitHub Releases: " + error.Message, null, "github");
        }
    }

    public static async Task<string> DownloadOnlineAsync(HttpClient http, string dataDirectory, Version installed, CancellationToken cancellation = default)
    {
        var manifest = await ReadOnlineManifest(http, cancellation) ?? throw new InvalidDataException("No public release is available.");
        if (Version.Parse(manifest.Version) <= installed) throw new InvalidDataException("No newer public release is available.");
        var updates = Path.Combine(dataDirectory, "updates");
        Directory.CreateDirectory(updates);
        var destination = Path.Combine(updates, manifest.Installer);
        if (File.Exists(destination) && new FileInfo(destination).Length is >= 100_000 and <= MaxInstallerBytes)
        {
            await using var cached = File.OpenRead(destination);
            var cachedHash = Convert.ToHexString(await SHA256.HashDataAsync(cached, cancellation));
            if (cachedHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) return destination;
        }
        var temporary = destination + ".download";
        try
        {
            using var response = await http.GetAsync(PublicAssetBase + manifest.Installer, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps || response.Content.Headers.ContentLength is < 100_000 or > MaxInstallerBytes)
                throw new InvalidDataException("Invalid update download.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellation);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var chunk = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(chunk, cancellation)) > 0)
                {
                    if (output.Length + count > MaxInstallerBytes) throw new InvalidDataException("Update installer is too large.");
                    await output.WriteAsync(chunk.AsMemory(0, count), cancellation);
                }
            }
            if (new FileInfo(temporary).Length < 100_000) throw new InvalidDataException("Update installer is incomplete.");
            string actual;
            await using (var file = File.OpenRead(temporary))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
            if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update installer checksum does not match.");
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Save(string dataDirectory, string folder)
    {
        var full = Path.GetFullPath(folder);
        if (!Directory.Exists(full)) throw new ArgumentException("Choose an existing releases folder.");
        Json.Save(Setting(dataDirectory), full);
    }

    public static UpdateStatus Check(string dataDirectory, Version installed)
    {
        var label = installed.ToString(3);
        var folder = Json.Read<string?>(Setting(dataDirectory), null);
        if (folder is null) return new(label, null, false, null, "Choose a local releases folder to check for updates.", null);
        try
        {
            var manifestPath = Path.Combine(folder, "latest.json");
            if (!File.Exists(manifestPath)) return new(label, null, false, null, "No latest.json in the selected releases folder.", folder);
            if (new FileInfo(manifestPath).Length > 4096) throw new InvalidDataException("Update manifest is too large.");
            var (manifest, latest) = ParseManifest(File.ReadAllText(manifestPath));
            if (latest <= installed) return new(label, latest.ToString(3), false, null, "You have the latest version in this release folder.", folder);
            var installer = Path.Combine(folder, manifest.Installer);
            if (!File.Exists(installer)) throw new FileNotFoundException("Update installer is missing.");
            if (new FileInfo(installer).Length is < 100_000 or > 1_000_000_000) throw new InvalidDataException("Update installer size is invalid.");
            using var stream = File.OpenRead(installer);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update installer checksum does not match.");
            return new(label, latest.ToString(3), true, installer, "Verified local update available.", folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return new(label, null, false, null, "Update check failed: " + error.Message, folder);
        }
    }
}
