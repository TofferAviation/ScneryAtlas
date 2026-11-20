using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SceneryAtlas.Core;

public sealed class SceneryScanner(IReadOnlyDictionary<string, Airport> airports)
{
    private static IEnumerable<string> PackageFolders(PackageRoot root)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root.Path, 0));
        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                if (File.Exists(Path.Combine(child, "manifest.json"))) { yield return child; continue; }
                // Addon Linker libraries can group packages under folders.
                // Mounted Community roots stay direct; never traverse junction graphs.
                if (!root.Mounted && depth < 3 && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    pending.Push((child, depth + 1));
            }
        }
    }
    public static string NormalizePackage(string name) => Regex.Replace(name.ToLowerInvariant(), "^(?:community(?:fs20|fs24)?-|communityfs20-|communityfs24-|fs20-|fs24-)", "");
    public static Dictionary<string, string> ReadActivation(IEnumerable<string> files, List<string> warnings)
    {
        var values = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 15_000_000 });
                var xml = XDocument.Load(reader);
                foreach (var el in xml.Descendants().Where(x => x.Name.LocalName.Equals("Package", StringComparison.OrdinalIgnoreCase)))
                {
                    var name = (string?)el.Attribute("name"); var raw = ((string?)el.Attribute("active"))?.ToLowerInvariant();
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(raw)) continue;
                    var state = raw is "true" or "activated" or "active" ? "active" : raw is "false" or "userdisabled" or "systemdisabled" or "disabled" ? "disabled" : "unknown";
                    var key = NormalizePackage(name);
                    if (!values.TryGetValue(key, out var states)) values[key] = states = [];
                    states.Add(state);
                }
            }
            catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException) { warnings.Add($"Cannot read activation file {Path.GetFileName(file)}: {e.Message}"); }
        }
        return values.ToDictionary(x => x.Key, x => x.Value.Count == 1 ? x.Value.First() : "unknown", StringComparer.OrdinalIgnoreCase);
    }

    public ScanResult Scan(IEnumerable<PackageRoot> sources, IEnumerable<string> contentFiles, CancellationToken cancellation = default)
    {
        var items = new List<SceneryItem>(); var warnings = new List<string>();
        var roots = sources.GroupBy(r => (r.Path.ToLowerInvariant(), r.Simulator))
            .Select(g => g.OrderByDescending(r => r.Mounted).ThenByDescending(r => r.Source == "Selected Community").First()).ToList();
        var activation = ReadActivation(contentFiles.Concat(roots.Select(r => Path.Combine(r.Path, "Content.xml"))), warnings);
        var examined = 0;
        foreach (var root in roots)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Directory.Exists(root.Path)) { warnings.Add($"Folder unavailable: {root.Path}"); continue; }
            try
            {
                foreach (var package in PackageFolders(root))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var manifest = Path.Combine(package, "manifest.json");
                    if (!File.Exists(manifest)) continue;
                    examined++;
                    try
                    {
                        if (new FileInfo(manifest).Length > 2_000_000) { warnings.Add($"Manifest too large: {Path.GetFileName(package)}"); continue; }
                        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                        string Read(string key) => doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                        var name = Path.GetFileName(package);
                        var type = Read("content_type");
                        if (Regex.IsMatch(name, "^(?:navigraph|fs-base-nav)|(?:^|-)navdata(?:-|$)", RegexOptions.IgnoreCase)) continue;
                        var kind = type.Equals("AIRCRAFT", StringComparison.OrdinalIgnoreCase) ? "Aircraft"
                            : type.Equals("SCENERY", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(name, "(?:airport|scenery|landmark|city|mesh|terrain)", RegexOptions.IgnoreCase) ? "Scenery" : "Utility";
                        if (kind != "Scenery" && (root.Source.Contains("Official", StringComparison.OrdinalIgnoreCase) || root.Source == "StreamedPackages")) continue;
                        var title = Read("title"); if (title.Length == 0) title = name;
                        var developer = Read("creator"); if (developer.Length == 0) developer = Read("manufacturer");
                        var candidates = Regex.Matches((name + " " + title).ToUpperInvariant(), @"(?<![A-Z0-9])[A-Z][A-Z0-9]{3}(?![A-Z0-9])")
                            .Select(m => m.Value).Where(airports.ContainsKey).Distinct().ToList();
                        // An ambiguous multi-airport package stays unlocated until manually resolved.
                        var airport = kind == "Scenery" && candidates.Count == 1 ? airports[candidates[0]] : null;
                        var key = NormalizePackage(name);
                        var hasActivation = activation.TryGetValue(key, out var known);
                        var community = root.Source.Contains("Community", StringComparison.OrdinalIgnoreCase);
                        var linked = (File.GetAttributes(package) & FileAttributes.ReparsePoint) != 0;
                        var state = !root.Mounted ? "disabled" : hasActivation ? known! : community ? "active" : "enabled";
                        var evidence = !root.Mounted ? "Stored outside the selected simulator Community folder; no active link detected in this scan."
                            : known is not null ? known == "unknown" ? "Conflicting or unrecognised Content.xml entries; verify in MSFS." : "Content.xml package state; not a live loaded-state probe."
                            : community ? linked ? "Add-on link is present in the selected Community folder; available on the next simulator launch."
                                : "Package is present in the Community folder; available on the next simulator launch."
                            : "Present in a configured simulator folder; activation not explicitly confirmed.";
                        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.Simulator + "|" + SimulatorDiscovery.Canonical(package).ToLowerInvariant())))[..24].ToLowerInvariant();
                        items.Add(new(id, title, developer, Read("package_version"), airport?.Icao, airport?.Latitude, airport?.Longitude, root.Simulator, name, package, true, state, evidence, Provider: InferProvider(developer + " " + name), Kind: kind));
                    }
                    catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { warnings.Add($"Skipped {Path.GetFileName(package)}: {e.Message}"); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Cannot read {root.Path}: {e.Message}"); }
        }
        // The same files may be discovered through an external library and an
        // Addon Linker junction. The mounted Community view takes precedence.
        return new(items.GroupBy(i => i.Id).Select(group => group.OrderByDescending(i => i.Activation == "active")
            .ThenByDescending(i => i.Activation == "enabled").ThenByDescending(i => i.Activation == "unknown")
            .ThenByDescending(i => i.Evidence.StartsWith("Content.xml") || i.Evidence.Contains("Community folder")).First()).ToList(), roots, warnings, DateTimeOffset.UtcNow, examined);
    }

    public static string? InferProvider(string value)
    {
        var lower = value.ToLowerInvariant();
        foreach (var provider in new[] { "inibuilds", "aerosoft", "orbx", "flightbeam", "flytampa", "drzewiecki", "contrail", "simmarket" })
            if (lower.Contains(provider)) return provider == "inibuilds" ? "iniBuilds" : provider;
        return null;
    }
}
