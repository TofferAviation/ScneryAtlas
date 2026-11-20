using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SceneryAtlas.Core;

public static class Catalogue
{
    public static List<SceneryItem> Import(string json, IReadOnlyDictionary<string, Airport> airports)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 10_000) throw new ArgumentException("Import a JSON array of up to 10,000 scenery products.");
        var items = new List<SceneryItem>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each catalogue entry must be an object.");
            string Text(string name, int max = 300) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is string text && text.Length <= max ? text.Trim() : "";
            var id = Text("id", 150); var title = Text("title"); var provider = Text("provider", 80); var url = Text("storeUrl", 2000); var icao = Text("icao", 4).ToUpperInvariant();
            if (!Regex.IsMatch(id, "^[a-zA-Z0-9_.:-]{1,150}$") || title.Length == 0 || provider.Length == 0 || !IsStoreUrl(url)) throw new ArgumentException("Every product needs an id, title, provider and public HTTPS store URL.");
            airports.TryGetValue(icao, out var airport);
            var lat = airport?.Latitude; var lon = airport?.Longitude;
            if (row.TryGetProperty("latitude", out var latitude) && row.TryGetProperty("longitude", out var longitude)
                && latitude.TryGetDouble(out var la) && longitude.TryGetDouble(out var lo) && double.IsFinite(la) && double.IsFinite(lo) && la is >= -90 and <= 90 && lo is >= -180 and <= 180) { lat = la; lon = lo; }
            var simulators = new List<string>();
            if (row.TryGetProperty("simulators", out var simRows))
            {
                if (simRows.ValueKind != JsonValueKind.Array || simRows.GetArrayLength() > 4) throw new ArgumentException("simulators must be an array of supported simulator names.");
                foreach (var simRow in simRows.EnumerateArray())
                {
                    var sim = simRow.ValueKind == JsonValueKind.String ? simRow.GetString() : null;
                    if (sim is not ("MSFS 2020" or "MSFS 2024" or "X-Plane 11" or "X-Plane 12")) throw new ArgumentException("Use MSFS 2020, MSFS 2024, X-Plane 11 or X-Plane 12 in simulators.");
                    if (!simulators.Contains(sim)) simulators.Add(sim);
                }
            }
            var legacySim = Text("simulator", 40);
            if (simulators.Count == 0) simulators.Add(legacySim is "MSFS 2020" or "MSFS 2024" or "X-Plane 11" or "X-Plane 12" ? legacySim : "MSFS 2024");
            var dependencies = new List<LibraryDependency>();
            if (row.TryGetProperty("requiredLibraries", out var libraryRows))
            {
                if (libraryRows.ValueKind != JsonValueKind.Array || libraryRows.GetArrayLength() > 30) throw new ArgumentException("requiredLibraries must be an array of up to 30 links.");
                foreach (var library in libraryRows.EnumerateArray())
                {
                    if (library.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each library needs a name and public HTTPS download URL.");
                    var name = library.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()?.Trim() : null;
                    var download = library.TryGetProperty("downloadUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || !IsStoreUrl(download ?? "")) throw new ArgumentException("Each library needs a name and public HTTPS download URL.");
                    var packageName = library.TryGetProperty("packageName", out var pkg) && pkg.ValueKind == JsonValueKind.String ? pkg.GetString()?.Trim() : null;
                    if (packageName is { Length: > 200 }) throw new ArgumentException("Library packageName is too long.");
                    dependencies.Add(new(name, download!, string.IsNullOrEmpty(packageName) ? null : packageName));
                }
            }
            items.Add(new("catalogue:" + id, title, Text("developer", 100), Text("version", 50), airport?.Icao, lat, lon,
                simulators[0], Text("packageName", 200) is { Length: > 0 } package ? package : null,
                null, false, "unknown", "Imported store catalogue; compatibility and library requirements are publisher-supplied, not independently verified.", url, provider,
                Available: true, CompatibleSimulators: simulators, RequiredLibraries: dependencies));
        }
        if (items.Select(x => x.Id).Distinct().Count() != items.Count) throw new ArgumentException("Product IDs must be unique within the import.");
        return items;
    }

    public static bool IsStoreUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && !uri.IsLoopback;

    public static List<SceneryItem> Merge(IEnumerable<SceneryItem> installed, ProfileData profile)
    {
        var owned = profile.Purchased.ToHashSet(StringComparer.Ordinal); var remaining = profile.Catalogue.ToDictionary(x => x.Id);
        var output = new List<SceneryItem>();
        foreach (var item in installed)
        {
            // Exact package IDs only. Matching solely by airport would confuse competing developers.
            var matches = profile.Catalogue.Where(c => !string.IsNullOrEmpty(c.PackageName) &&
                SceneryScanner.NormalizePackage(c.PackageName) == SceneryScanner.NormalizePackage(item.PackageName ?? "") &&
                (c.CompatibleSimulators?.Contains(item.Simulator) == true || c.Simulator == item.Simulator || (c.Simulator == "MSFS" && item.Simulator.StartsWith("MSFS")))).ToList();
            var catalogue = matches.Count == 1 ? matches[0] : null;
            if (catalogue is not null) remaining.Remove(catalogue.Id);
            output.Add(item with { Purchased = owned.Contains(item.Id) || (catalogue is not null && owned.Contains(catalogue.Id)), Available = catalogue is not null,
                StoreUrl = catalogue?.StoreUrl, Provider = catalogue?.Provider ?? item.Provider, Price = catalogue?.Price,
                Latitude = item.Latitude ?? catalogue?.Latitude, Longitude = item.Longitude ?? catalogue?.Longitude, Icao = item.Icao ?? catalogue?.Icao,
                CompatibleSimulators = catalogue?.CompatibleSimulators ?? item.CompatibleSimulators ?? [item.Simulator], RequiredLibraries = catalogue?.RequiredLibraries });
        }
        output.AddRange(remaining.Values.Select(i => i with { Purchased = owned.Contains(i.Id) }));
        return output.Select(i => profile.Locations?.TryGetValue(i.Id, out var location) == true
            ? i with { Icao = location.Icao, Latitude = location.Latitude, Longitude = location.Longitude, Evidence = "Map location set by you. " + i.Evidence } : i)
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ProfileData TogglePurchased(ProfileData profile, string id, IEnumerable<SceneryItem> installed)
    {
        var item = Merge(installed, profile).FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("Choose an existing scenery product.");
        var matches = profile.Catalogue.Where(c => !string.IsNullOrEmpty(c.PackageName) &&
            SceneryScanner.NormalizePackage(c.PackageName) == SceneryScanner.NormalizePackage(item.PackageName ?? "") &&
            (c.CompatibleSimulators?.Contains(item.Simulator) == true || c.Simulator == item.Simulator || (c.Simulator == "MSFS" && item.Simulator.StartsWith("MSFS")))).ToList();
        var keys = matches.Count == 1 ? new[] { id, matches[0].Id } : new[] { id };
        var owned = profile.Purchased.ToHashSet();
        if (keys.Any(owned.Contains)) foreach (var key in keys) owned.Remove(key); else owned.Add(id);
        return profile with { Purchased = owned.ToList() };
    }

    public static List<SceneryItem> ParseIniSceneryPage(string html, IReadOnlyDictionary<string, Airport> airports)
    {
        var items = new List<SceneryItem>();
        foreach (Match link in Regex.Matches(html, "href=[\"'](?<url>(?:https://inibuilds\\.com)?/products/(?<handle>[a-zA-Z0-9-]+))[^\"']*[\"']", RegexOptions.IgnoreCase))
        {
            var handle = link.Groups["handle"].Value;
            var code = Regex.Matches(handle.ToUpperInvariant(), "(?<![A-Z0-9])[A-Z][A-Z0-9]{3}(?![A-Z0-9])").Select(m => m.Value).FirstOrDefault(airports.ContainsKey);
            if (code is null || !airports.TryGetValue(code, out var airport)) continue;
            var title = WebUtility.HtmlDecode(string.Join(" ", handle.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Equals(code, StringComparison.OrdinalIgnoreCase) ? code : char.ToUpperInvariant(t[0]) + t[1..])));
            items.Add(new("catalogue:ini:" + handle, title, "iniBuilds Store", "", code, airport.Latitude, airport.Longitude, "MSFS", null, null, false, "unknown",
                "Public iniScenery page listing. Simulator compatibility and price must be checked on the product page.", "https://inibuilds.com/products/" + handle, "iniBuilds", Available: true));
        }
        return items.DistinctBy(x => x.Id).ToList();
    }
}
