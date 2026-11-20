using System.Text.Json;
using System.Net;
using System.Net.Http;
using SceneryAtlas.Core;

var airports = new Dictionary<string, Airport>(StringComparer.OrdinalIgnoreCase) { ["ENGM"] = new("ENGM", "Oslo Gardermoen", 60.1939, 11.1004, "NO"), ["EGLL"] = new("EGLL", "Heathrow", 51.47, -.4543, "GB") };
if (args.Length == 3 && args[0] == "--inspect")
{
    var all = Json.Read<List<Airport>>(args[1], []).ToDictionary(a => a.Icao, StringComparer.OrdinalIgnoreCase);
    var configs = SimulatorDiscovery.ConfigDirectories(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    var warnings = new List<string>(); var found = SimulatorDiscovery.Discover(configs, warnings);
    var activationFiles = configs.Where(c => c.Simulator == "MSFS 2024").SelectMany(c => SimulatorDiscovery.ActivationFiles(c.Directory)).ToList();
    var result = new SceneryScanner(all).Scan(found.Where(r => r.Simulator == "MSFS 2024").Append(new(args[2], "MSFS 2024", false, "Read-only inspection")), activationFiles);
    Console.WriteLine(JsonSerializer.Serialize(new { result.PackagesExamined, scenery = result.Items.Count, mapped = result.Items.Count(i => i.Icao is not null), explicitActivationRecords = result.Items.Count(i => i.Evidence.StartsWith("Content.xml")), active = result.Items.Count(i => i.Activation == "active"), detectedRoots = found.Count, activationFiles = activationFiles.Count, warnings = result.Warnings.Count }, Json.Options)); return;
}
var directory = Path.Combine(Path.GetTempPath(), "SceneryAtlasTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
var passed = 0; var failed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected input rejection."); }
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
string MakePackage(string folder, string name, string type = "SCENERY", string? title = null)
{
    var path = Path.Combine(directory, folder, name); Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path, "manifest.json"), JsonSerializer.Serialize(new { content_type = type, title = title ?? name, creator = "Test developer", package_version = "1.2.3" })); return path;
}
try
{
    Test("local registration stores a salted password hash, never plaintext", () => {
        var accounts = new LocalAccounts(directory); var user = accounts.Register("test-pilot", "Test Pilot", "Scenery Test Password 123");
        Check(user.Username == "test-pilot" && accounts.Session?.Id == user.Id, "Account not created.");
        Check(!File.ReadAllText(Path.Combine(directory, "accounts.json")).Contains("Scenery Test Password"), "Plaintext password stored.");
        accounts.Logout(); Check(accounts.Session is null, "Logout failed."); Check(accounts.Login("TEST-PILOT", "Scenery Test Password 123").Id == user.Id, "Login failed.");
    });
    Test("registration and login reject duplicates, weak passwords and wrong credentials", () => {
        var accounts = new LocalAccounts(directory); Throws(() => accounts.Register("test-pilot", "Pilot", "Different Password 123"));
        Throws(() => accounts.Register("other-pilot", "Pilot", "short")); Throws(() => accounts.Login("test-pilot", "wrong")); Check(accounts.Session is null, "Failed login created a session.");
    });
    Test("failed login attempts are rate limited", () => {
        var accounts = new LocalAccounts(directory); for (var i = 0; i < 5; i++) Throws(() => accounts.Login("test-pilot", "wrong"));
        Throws(() => accounts.Login("test-pilot", "Scenery Test Password 123"));
    });
    Test("local profiles isolate purchase records", () => {
        var store = new ProfileStore(directory); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        store.Save(a, ProfileData.Empty with { Purchased = ["item-one"] }); Check(store.Load(b).Purchased.Count == 0, "Other profile inherited ownership.");
        Throws(() => store.Load("../../escape"));
    });
    Test("simulator discovery ignores NextBoot and reads only the current installation path", () => {
        var actual = Path.Combine(directory, "Current Packages"); var next = Path.Combine(directory, "Future Packages");
        Check(SimulatorDiscovery.InstalledPackagesPath($"InstalledPackagesPathNextBoot \"{next}\"\nInstalledPackagesPath \"{actual}\"\n") == actual, "Used the future path.");
    });
    var content = Path.Combine(directory, "Content.xml");
    Test("MSFS 2024 package prefixes and activation enums are recognised", () => {
        File.WriteAllText(content, "<Content><Package name=\"communityfs20-test-airport-engm\" active=\"Activated\"/><Package name=\"fs24-test-airport-egll\" active=\"UserDisabled\"/></Content>");
        var states = SceneryScanner.ReadActivation([content], []); Check(states["test-airport-engm"] == "active" && states["test-airport-egll"] == "disabled", "Incorrect activation mapping.");
    });
    Test("activation discovery includes modern gamertag subfolders without recursively walking packages", () => {
        var config = Path.Combine(directory, "Config"); var profile = Path.Combine(config, "TestGamertag"); var nested = Path.Combine(config, "Packages", "Nested");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(profile, "Content.xml"), "<Content/>"); File.WriteAllText(Path.Combine(nested, "Content.xml"), "<Content/>");
        var files = SimulatorDiscovery.ActivationFiles(config); Check(files.Count == 1 && files[0] == Path.Combine(profile, "Content.xml"), "Gamertag configuration was missed or package tree was walked.");
    });
    Test("contradictory package entries are unknown, not falsely active", () => {
        var conflict = Path.Combine(directory, "conflict.xml"); File.WriteAllText(conflict, "<Content><Package name=\"test\" active=\"Activated\"/><Package name=\"test\" active=\"SystemDisabled\"/></Content>");
        Check(SceneryScanner.ReadActivation([conflict], [])["test"] == "unknown", "Conflict silently resolved.");
    });
    Test("XML external entities are rejected", () => {
        var unsafeFile = Path.Combine(directory, "unsafe.xml"); File.WriteAllText(unsafeFile, "<!DOCTYPE Content [<!ENTITY x SYSTEM 'file:///unavailable'>]><Content>&x;</Content>");
        var warnings = new List<string>(); Check(SceneryScanner.ReadActivation([unsafeFile], warnings).Count == 0 && warnings.Count == 1, "Unsafe XML accepted.");
    });
    MakePackage("Community", "test-airport-engm"); MakePackage("Community", "test-aircraft-a320", "AIRCRAFT");
    MakePackage("Library", "test-airport-egll"); MakePackage("Community", "test-landmark-nolocation");
    Test("scan lists scenery and aircraft but only maps scenery to airports", () => {
        var result = new SceneryScanner(airports).Scan([new(Path.Combine(directory, "Community"), "MSFS 2024", true, "Community")], [content]);
        Check(result.PackagesExamined == 3 && result.Items.Count == 3, "Wrong add-on detection.");
        var oslo = result.Items.Single(i => i.Icao == "ENGM"); Check(oslo.Latitude == 60.1939 && oslo.Activation == "active", "Airport mapping failed.");
        Check(result.Items.Single(i => i.Kind == "Aircraft").Icao is null, "Aircraft acquired a scenery marker.");
        Check(result.Items.Where(i => i.Icao is null).All(i => i.Latitude is null), "Invented coordinates.");
    });
    Test("Community packages are available at next launch without a Content.xml entry", () => {
        var result = new SceneryScanner(airports).Scan([new(Path.Combine(directory, "Community"), "MSFS 2024", true, "Community")], []);
        Check(result.Items.All(i => i.Activation == "active" && i.Evidence.Contains("next simulator launch")), "Community activation evidence missing.");
    });
    Test("unmounted library packages stay installed but disabled", () => {
        var result = new SceneryScanner(airports).Scan([new(Path.Combine(directory, "Library"), "MSFS 2024", false, "Library")], []);
        Check(result.Items.Single().Installed && result.Items.Single().Activation == "disabled", "Unmounted library misreported.");
    });
    Test("Addon Linker storage groups are scanned and Community wins duplicate identity", () => {
        MakePackage(Path.Combine("Library", "Airports", "Europe"), "test-airport-nested-egll");
        var library = new PackageRoot(Path.Combine(directory, "Community"), "MSFS 2024", false, "Addon Linker library");
        var community = library with { Mounted = true, Source = "Selected Community" };
        var duplicate = new SceneryScanner(airports).Scan([library, community], []);
        Check(duplicate.Items.Count == 3 && duplicate.Items.All(i => i.Activation == "active"), "Mounted Community copy did not win.");
        var nested = new SceneryScanner(airports).Scan([new(Path.Combine(directory, "Library"), "MSFS 2024", false, "Addon Linker library")], []);
        Check(nested.Items.Any(i => i.PackageName == "test-airport-nested-egll" && i.Activation == "disabled"), "Nested library package missed.");
    });
    Test("malformed manifests do not prevent other scenery detection", () => {
        var invalid = MakePackage("Community", "bad-airport"); File.WriteAllText(Path.Combine(invalid, "manifest.json"), "invalid");
        var result = new SceneryScanner(airports).Scan([new(Path.Combine(directory, "Community"), "MSFS 2024", true, "Community")], []);
        Check(result.Items.Count == 3 && result.Warnings.Count == 1, "Bad manifest killed scan.");
    });
    Test("catalogue import validates store URLs, exact package IDs and location", () => {
        var products = Catalogue.Import("[{\"id\":\"example-engm\",\"title\":\"Test scenery\",\"provider\":\"Test store\",\"storeUrl\":\"https://example.org/engm\",\"icao\":\"ENGM\",\"packageName\":\"test-airport-engm\"}]", airports);
        Check(products.Single().Latitude == 60.1939 && products.Single().Available, "Import location missing.");
        Throws(() => Catalogue.Import("[{\"id\":\"bad\",\"title\":\"Bad\",\"provider\":\"Test\",\"storeUrl\":\"file:///c:/windows\"}]", airports));
        Check(!Catalogue.IsStoreUrl("javascript:alert(1)"), "Script URL accepted.");
        var scenery = new SceneryScanner(airports).Scan([new(Path.Combine(directory, "Community"), "MSFS 2024", true, "Community")], []);
        var merged = Catalogue.Merge(scenery.Items, new([], [products[0].Id], products));
        Check(merged.Count == 3 && merged.Single(i => i.Icao == "ENGM").Purchased, "Exact match failed to merge purchase.");
    });
    Test("competing sceneries at one airport are never merged by ICAO alone", () => {
        var products = Catalogue.Import("[{\"id\":\"competitor\",\"title\":\"Other Oslo scenery\",\"provider\":\"Other store\",\"storeUrl\":\"https://example.org/other\",\"icao\":\"ENGM\"}]", airports);
        var local = new SceneryScanner(airports).Scan([new(Path.Combine(directory, "Community"), "MSFS 2024", true, "Community")], []);
        Check(Catalogue.Merge(local.Items, new([], [], products)).Count == 4, "Different products conflated.");
    });
    Test("public iniBuilds listings are de-duplicated and require a known airport", () => {
        var products = Catalogue.ParseIniSceneryPage("<a href=\"/products/inibuilds-oslo-engm-msfs\">Oslo</a><a href=\"/products/inibuilds-oslo-engm-msfs\">Oslo</a><a href=\"/products/aircraft-noairport\">Aircraft</a>", airports);
        Check(products.Count == 1 && products[0].Icao == "ENGM", "Public listing extraction failed.");
    });
    Test("purchase confirmation can be removed after an exact catalogue match", () => {
        var products = Catalogue.Import("[{\"id\":\"owned-oslo\",\"title\":\"Test\",\"provider\":\"Test\",\"storeUrl\":\"https://example.org/oslo\",\"icao\":\"ENGM\",\"packageName\":\"test-airport-engm\"}]", airports);
        var local = new SceneryScanner(airports).Scan([new(Path.Combine(directory, "Community"), "MSFS 2024", true, "Community")], []).Items;
        var profile = new ProfileData([], [products[0].Id], products); var id = local.Single(x => x.Icao == "ENGM").Id;
        var updated = Catalogue.TogglePurchased(profile, id, local);
        Check(!Catalogue.Merge(local, updated).Single(x => x.Id == id).Purchased, "Matched purchase could not be removed.");
    });
    Test("manual map locations are profile-specific and leave scanned packages unchanged", () => {
        var local = new SceneryScanner(airports).Scan([new(Path.Combine(directory, "Community"), "MSFS 2024", true, "Community")], []).Items;
        var unlocated = local.Single(x => x.Kind == "Scenery" && x.Icao is null); var profile = ProfileData.Empty with { Locations = new() { [unlocated.Id] = new(null, 51, -1) } };
        Check(Catalogue.Merge(local, profile).Single(x => x.Id == unlocated.Id).Latitude == 51, "Manual location missing.");
        Check(unlocated.Latitude is null && Catalogue.Merge(local, ProfileData.Empty).Single(x => x.Id == unlocated.Id).Latitude is null, "Location changed the scan or another profile.");
    });
    Test("X-Plane 11 and 12 scenery pack states and airport data are read without editing files", () => {
        var root = Path.Combine(directory, "X-Plane 12", "Custom Scenery");
        var oslo = Path.Combine(root, "Test ENGM"); var libraryPath = Path.Combine(root, "Test Library");
        Directory.CreateDirectory(Path.Combine(oslo, "Earth nav data")); Directory.CreateDirectory(libraryPath);
        File.WriteAllText(Path.Combine(directory, "X-Plane 12", "X-Plane.exe"), "test executable marker");
        File.WriteAllText(Path.Combine(oslo, "Earth nav data", "apt.dat"), "I\n1200 Version\n1 0 0 0 ENGM Oslo Gardermoen\n99\n");
        File.WriteAllText(Path.Combine(libraryPath, "library.txt"), "A\n1200\nLIBRARY\n");
        var ini = Path.Combine(root, "scenery_packs.ini");
        File.WriteAllText(ini, "SCENERY_PACK Custom Scenery/Test ENGM/\nSCENERY_PACK_DISABLED Custom Scenery/Test Library/\n");
        var scan = new XPlaneScanner(airports).Scan([new(root, "X-Plane 12", true, "Custom Scenery")]);
        Check(scan.Items.Count == 2 && scan.Items.Single(i => i.Icao == "ENGM").Activation == "active", "X-Plane airport missing.");
        Check(scan.Items.Single(i => i.Kind == "Library").Activation == "disabled", "X-Plane library state wrong.");
        Check(XPlaneDiscovery.Discover(directory, directory, directory).Any(r => r.Path == root && r.Simulator == "X-Plane 12"), "X-Plane installation was not found.");
        Check(File.ReadAllText(ini).Contains("SCENERY_PACK_DISABLED"), "Simulator config changed.");
    });
    Test("conflicting X-Plane pack entries stay unknown", () => {
        var ini = Path.Combine(directory, "conflicting-scenery_packs.ini");
        File.WriteAllText(ini, "SCENERY_PACK Custom Scenery/Test ENGM/\nSCENERY_PACK_DISABLED Custom Scenery/Test ENGM/\nSCENERY_PACK Custom Scenery/Test ENGM/\n");
        Check(XPlaneScanner.ReadSceneryPacks(ini, [])["test engm"] == "unknown", "Conflicting X-Plane state was guessed.");
    });
    Test("catalogue records multiple compatible simulators and licensed library download links", () => {
        var records = Catalogue.Import("[{\"id\":\"xp-oslo\",\"title\":\"Oslo\",\"provider\":\"Example\",\"storeUrl\":\"https://example.org/oslo\",\"icao\":\"ENGM\",\"simulators\":[\"X-Plane 11\",\"X-Plane 12\"],\"requiredLibraries\":[{\"name\":\"Test library\",\"downloadUrl\":\"https://example.org/library\"}]}]", airports);
        Check(records.Single().CompatibleSimulators?.Count == 2 && records.Single().RequiredLibraries?.Count == 1, "Compatibility or dependency missing.");
        Throws(() => Catalogue.Import("[{\"id\":\"bad-lib\",\"title\":\"Bad\",\"provider\":\"Example\",\"storeUrl\":\"https://example.org/scenery\",\"requiredLibraries\":[{\"name\":\"Bad\",\"downloadUrl\":\"file:///etc/passwd\"}]}]", airports));
    });
    Test("local update channel rejects a mismatched installer hash", () => {
        var releases = Path.Combine(directory, "Releases"); Directory.CreateDirectory(releases);
        UpdateChannel.Save(directory, releases);
        var fake = Path.Combine(releases, "SceneryAtlas-0.3.0-Setup.exe"); File.WriteAllBytes(fake, new byte[100_000]);
        Json.Save(Path.Combine(releases, "latest.json"), new UpdateManifest("0.3.0", Path.GetFileName(fake), new string('a', 64)));
        var check = UpdateChannel.Check(directory, new Version(0, 2, 0));
        Check(!check.Available && check.Message.Contains("checksum"), "Unverified update became available.");
        Json.Save(Path.Combine(releases, "latest.json"), new UpdateManifest("0.1.0", Path.GetFileName(fake), new string('a', 64)));
        Check(!UpdateChannel.Check(directory, new Version(0, 2, 0)).Available, "Older release was offered.");
        Json.Save(Path.Combine(releases, "latest.json"), new UpdateManifest("0.3.0", Path.GetFileName(fake), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fake)))));
        Check(UpdateChannel.Check(directory, new Version(0, 2, 0)).Available, "Verified new release not offered.");
    });
    Test("public update channel checks and verifies a downloaded installer", () => {
        var installer = new byte[100_000]; new Random(42).NextBytes(installer);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installer));
        var manifest = JsonSerializer.Serialize(new UpdateManifest("0.5.0", "SceneryAtlas-0.5.0-Setup.exe", hash), Json.Options);
        using var client = new HttpClient(new StubHandler(request => {
            var url = request.RequestUri!.AbsoluteUri;
            Check(url.StartsWith("https://github.com/TofferAviation/ScneryAtlas/releases/latest/download/"), "Update requested an unexpected URL.");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request,
                Content = url.EndsWith("latest.json") ? new StringContent(manifest) : new ByteArrayContent(installer) };
        }));
        var status = UpdateChannel.CheckOnlineAsync(client, new Version(0, 4, 0)).GetAwaiter().GetResult();
        Check(status.Available && status.Source == "github" && status.LatestVersion == "0.5.0", "Public release was not offered.");
        var path = UpdateChannel.DownloadOnlineAsync(client, directory, new Version(0, 4, 0)).GetAwaiter().GetResult();
        Check(File.ReadAllBytes(path).SequenceEqual(installer), "Downloaded installer was not verified or saved.");
        Check(!UpdateChannel.CheckOnlineAsync(client, new Version(0, 5, 0)).GetAwaiter().GetResult().Available, "Current version was offered again.");
    });
    Test("public update channel rejects a bad hash and unsafe manifest", () => {
        var installer = new byte[100_000];
        var manifest = JsonSerializer.Serialize(new UpdateManifest("0.6.0", "SceneryAtlas-0.6.0-Setup.exe", new string('a', 64)), Json.Options);
        using var client = new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK) {
            RequestMessage = request, Content = request.RequestUri!.AbsoluteUri.EndsWith("latest.json") ? new StringContent(manifest) : new ByteArrayContent(installer)
        }));
        var badFolder = Path.Combine(directory, "bad-update");
        try { _ = UpdateChannel.DownloadOnlineAsync(client, badFolder, new Version(0, 4, 0)).GetAwaiter().GetResult(); throw new Exception("A bad checksum was installed."); }
        catch (InvalidDataException) { Check(!File.Exists(Path.Combine(badFolder, "updates", "SceneryAtlas-0.6.0-Setup.exe")), "Bad installer was retained."); }
        var unsafeManifest = JsonSerializer.Serialize(new UpdateManifest("0.6.0", "../other.exe", new string('a', 64)), Json.Options);
        using var unsafeClient = new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(unsafeManifest) }));
        Check(!UpdateChannel.CheckOnlineAsync(unsafeClient, new Version(0, 4, 0)).GetAwaiter().GetResult().Available, "Unsafe installer name was offered.");
    });
    Console.WriteLine($"{passed} checks passed; {failed} failed."); Environment.ExitCode = failed == 0 ? 0 : 1;
}
finally { Directory.Delete(directory, true); }

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
