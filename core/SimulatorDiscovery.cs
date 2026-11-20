using System.Text.RegularExpressions;

namespace SceneryAtlas.Core;

public static class SimulatorDiscovery
{
    public static List<(string Directory, string Simulator)> ConfigDirectories(string local, string roaming) => [
        (Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache"), "MSFS 2024"),
        (Path.Combine(roaming, "Microsoft Flight Simulator 2024"), "MSFS 2024"),
        (Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache"), "MSFS 2020"),
        (Path.Combine(roaming, "Microsoft Flight Simulator"), "MSFS 2020")
    ];

    public static string? InstalledPackagesPath(string text)
    {
        // NextBoot is not the current package location.
        var match = Regex.Match(text, "^\\s*InstalledPackagesPath\\s+\"([^\"]+)\"\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success && Path.IsPathFullyQualified(match.Groups[1].Value) ? match.Groups[1].Value : null;
    }

    public static string Canonical(string path)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(path));
        return (dir.Exists ? dir.ResolveLinkTarget(true)?.FullName ?? dir.FullName : dir.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static List<string> ActivationFiles(string directory)
    {
        var paths = new List<string>();
        var direct = Path.Combine(directory, "Content.xml"); if (File.Exists(direct)) paths.Add(direct);
        if (!Directory.Exists(directory)) return paths;
        try
        {
            // Recent MSFS versions put Content.xml inside a per-gamertag subfolder.
            // Check exactly one level; never walk the package tree or unrelated user files.
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                var content = Path.Combine(child, "Content.xml"); if (File.Exists(content)) paths.Add(content);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Other available activation files remain useful. */ }
        return paths;
    }

    public static List<PackageRoot> Discover(IEnumerable<(string Directory, string Simulator)> configs, List<string> warnings)
    {
        var roots = new List<PackageRoot>();
        foreach (var (directory, simulator) in configs)
        {
            var cfg = Path.Combine(directory, "UserCfg.opt");
            if (!File.Exists(cfg)) continue;
            try
            {
                var location = InstalledPackagesPath(File.ReadAllText(cfg));
                if (location is null) { warnings.Add($"No current package path found for {simulator}."); continue; }
                foreach (var name in new[] { "Community", "Community2024", "Official", "Official2020", "Official2024", "StreamedPackages" })
                {
                    var folder = Path.Combine(location, name);
                    if (Directory.Exists(folder)) roots.Add(new(Canonical(folder), simulator, true, name));
                    if (name == "Official") foreach (var variant in new[] { "OneStore", "Steam" })
                    {
                        var official = Path.Combine(folder, variant);
                        if (Directory.Exists(official)) roots.Add(new(Canonical(official), simulator, true, "Official"));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Cannot inspect {simulator} configuration: {e.Message}"); }
        }
        return roots.DistinctBy(r => (r.Path.ToLowerInvariant(), r.Simulator)).ToList();
    }
}
