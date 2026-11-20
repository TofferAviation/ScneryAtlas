using System.Text.Json;

namespace SceneryAtlas.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static T Read<T>(string path, T fallback) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("Saved data is empty.") : fallback;
    public static void Save<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(data, Options)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public record LocalUser(string Id, string Username, string DisplayName);
public record Airport(string Icao, string Name, double Latitude, double Longitude, string Country);
public record PackageRoot(string Path, string Simulator, bool Mounted, string Source);
public record SceneryItem(string Id, string Title, string Developer, string Version, string? Icao,
    double? Latitude, double? Longitude, string Simulator, string? PackageName, string? LocalPath,
    bool Installed, string Activation, string Evidence, string? StoreUrl = null, string? Provider = null,
    bool Purchased = false, bool Available = false, string? Price = null,
    List<string>? CompatibleSimulators = null, List<LibraryDependency>? RequiredLibraries = null, string Kind = "Scenery");
public record LibraryDependency(string Name, string DownloadUrl, string? PackageName = null);
public record ScanResult(List<SceneryItem> Items, List<PackageRoot> Roots, List<string> Warnings, DateTimeOffset ScannedAt, int PackagesExamined);
public record SceneryLocation(string? Icao, double Latitude, double Longitude);
public record ProfileData(List<PackageRoot> ExtraRoots, List<string> Purchased, List<SceneryItem> Catalogue, Dictionary<string, SceneryLocation>? Locations = null)
{
    public static ProfileData Empty => new([], [], []);
}

public sealed class ProfileStore(string directory)
{
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid profile.");
        return Path.Combine(directory, "profiles", id + ".json");
    }
    public ProfileData Load(string id) => Json.Read(PathFor(id), ProfileData.Empty);
    public void Save(string id, ProfileData data) => Json.Save(PathFor(id), data);
}
