using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SceneryAtlas.Core;

public static class XPlaneDiscovery
{
    public static List<PackageRoot> Discover(string programFiles, string programFilesX86, string home)
    {
        // Probe known install locations and Steam's declared libraries only; never sweep drives.
        var candidates = new List<string> { programFiles, programFilesX86, home,
            Path.Combine(programFilesX86, "Steam", "steamapps", "common") };
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                candidates.Add(drive.RootDirectory.FullName);
                candidates.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common"));
            }
            foreach (var steam in new[] { Path.Combine(programFilesX86, "Steam"), Path.Combine(programFiles, "Steam") })
            {
                var libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(libraries) || new FileInfo(libraries).Length > 1_000_000) continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
                {
                    var path = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (Path.IsPathFullyQualified(path)) candidates.Add(Path.Combine(path, "steamapps", "common"));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Manual folder selection remains available. */ }
        var roots = new List<PackageRoot>();
        foreach (var parent in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var version in new[] { "11", "12" })
        {
            var sim = Path.Combine(parent, "X-Plane " + version);
            if (!File.Exists(Path.Combine(sim, "X-Plane.exe"))) continue;
            var scenery = Path.Combine(sim, "Custom Scenery");
            if (Directory.Exists(scenery)) roots.Add(new(SimulatorDiscovery.Canonical(scenery), "X-Plane " + version, true, "Custom Scenery"));
        }
        return roots.DistinctBy(r => (r.Path.ToLowerInvariant(), r.Simulator)).ToList();
    }
}

public sealed class XPlaneScanner(IReadOnlyDictionary<string, Airport> airports)
{
    public ScanResult Scan(IEnumerable<PackageRoot> sources, CancellationToken cancellation = default)
    {
        var roots = sources.DistinctBy(r => (r.Path.ToLowerInvariant(), r.Simulator)).ToList();
        var items = new List<SceneryItem>(); var warnings = new List<string>(); var examined = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path)) { warnings.Add($"Folder unavailable: {root.Path}"); continue; }
            var states = ReadSceneryPacks(Path.Combine(root.Path, "scenery_packs.ini"), warnings);
            try
            {
                foreach (var package in Directory.EnumerateDirectories(root.Path))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(package) & FileAttributes.ReparsePoint) != 0) continue;
                    var name = Path.GetFileName(package);
                    var apt = Path.Combine(package, "Earth nav data", "apt.dat");
                    var library = Path.Combine(package, "library.txt");
                    var nav = Path.Combine(package, "Earth nav data");
                    if (!File.Exists(apt) && !File.Exists(library) && !Directory.Exists(nav)) continue;
                    examined++;
                    var kind = File.Exists(library) && !File.Exists(apt) && !Directory.Exists(nav) ? "Library" : "Scenery";
                    try
                    {
                        var codes = AirportCodes(apt, name).Where(airports.ContainsKey).Distinct().ToList();
                        var airport = codes.Count == 1 ? airports[codes[0]] : null;
                        var key = name.ToLowerInvariant();
                        var state = !root.Mounted ? "disabled" : states.TryGetValue(key, out var known) ? known : "unknown";
                        var evidence = !root.Mounted ? "Outside the simulator's Custom Scenery folder; not loaded by this scan."
                            : states.ContainsKey(key) ? "scenery_packs.ini state; not a live loaded-state probe."
                            : "Present in Custom Scenery but absent from scenery_packs.ini; X-Plane may add it on next launch.";
                        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.Simulator + "|" + SimulatorDiscovery.Canonical(package).ToLowerInvariant())))[..24].ToLowerInvariant();
                        items.Add(new(id, name, "", "", airport?.Icao, airport?.Latitude, airport?.Longitude,
                            root.Simulator, name, package, true, state, evidence,
                            CompatibleSimulators: [root.Simulator], Kind: kind));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Skipped {name}: {e.Message}"); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Cannot read {root.Path}: {e.Message}"); }
        }
        return new(items.DistinctBy(i => i.Id).ToList(), roots, warnings, DateTimeOffset.UtcNow, examined);
    }

    public static Dictionary<string, string> ReadSceneryPacks(string path, List<string> warnings)
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return states;
        try
        {
            if (new FileInfo(path).Length > 2_000_000) { warnings.Add("scenery_packs.ini is too large to inspect."); return states; }
            foreach (var line in File.ReadLines(path))
            {
                var match = Regex.Match(line.Trim(), @"^SCENERY_PACK(_DISABLED)?\s+Custom Scenery[/\\](.+?)[/\\]?\s*$", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                var name = match.Groups[2].Value.TrimEnd('/', '\\');
                if (name.Contains('/') || name.Contains('\\') || name is "." or "..") continue;
                var state = match.Groups[1].Success ? "disabled" : "active";
                states[name.ToLowerInvariant()] = states.TryGetValue(name, out var prior) && prior != state ? "unknown" : prior ?? state;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Cannot read scenery_packs.ini: {e.Message}"); }
        return states;
    }

    public static IEnumerable<string> AirportCodes(string aptPath, string packageName)
    {
        if (File.Exists(aptPath) && new FileInfo(aptPath).Length <= 10_000_000)
        {
            foreach (var line in File.ReadLines(aptPath))
            {
                var match = Regex.Match(line, @"^\s*(?:1|16|17)\s+\S+\s+\S+\s+\S+\s+([A-Z][A-Z0-9]{3})(?:\s|$)", RegexOptions.IgnoreCase);
                if (match.Success) yield return match.Groups[1].Value.ToUpperInvariant();
            }
        }
        foreach (Match match in Regex.Matches(packageName.ToUpperInvariant(), @"(?<![A-Z0-9])[A-Z][A-Z0-9]{3}(?![A-Z0-9])")) yield return match.Value;
    }
}
