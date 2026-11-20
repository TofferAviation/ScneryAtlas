using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SceneryAtlas.Core;

namespace SceneryAtlas.App;

public partial class MainWindow : Window
{
    private const string Origin = "https://app.sceneryatlas.local";
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private readonly string dataDirectory;
    private readonly string webDirectory;
    private readonly DispatcherTimer? previewReloadTimer;
    private readonly FileSystemWatcher? previewWatcher;
    private readonly LocalAccounts accounts;
    private readonly ProfileStore profiles;
    private readonly SemaphoreSlim commands = new(1);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly HttpClient weatherHttp = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly HttpClient updateHttp = new() { Timeout = TimeSpan.FromMinutes(8) };
    private readonly Dictionary<string, Airport> airports;
    private readonly List<(string Directory, string Simulator)> configs;
    private List<SceneryItem> installed = [];
    private ScanResult? lastScan;
    private ImageryService? imagery;

    public MainWindow()
    {
        InitializeComponent();
        var preview = Environment.GetEnvironmentVariable("SCENERY_ATLAS_PREVIEW") == "1";
        if (preview) Title = "Scenery Atlas Preview";
        dataDirectory = Environment.GetEnvironmentVariable("SCENERY_ATLAS_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SceneryAtlas");
        webDirectory = preview && Environment.GetEnvironmentVariable("SCENERY_ATLAS_WEB_ROOT") is { Length: > 0 } source
            ? Path.GetFullPath(source) : Path.Combine(AppContext.BaseDirectory, "Web");
        if (preview && Directory.Exists(webDirectory))
        {
            previewReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            previewReloadTimer.Tick += (_, _) => { previewReloadTimer.Stop(); if (Browser.CoreWebView2 is not null) Browser.CoreWebView2.Reload(); };
            previewWatcher = new FileSystemWatcher(webDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            previewWatcher.Changed += PreviewFileChanged;
            previewWatcher.Created += PreviewFileChanged;
            previewWatcher.Renamed += (_, args) => PreviewFileChanged(args.Name);
        }
        accounts = new(dataDirectory); profiles = new(dataDirectory);
        airports = Json.Read<List<Airport>>(Path.Combine(webDirectory, "airports.json"), []).ToDictionary(a => a.Icao, StringComparer.OrdinalIgnoreCase);
        configs = SimulatorDiscovery.ConfigDirectories(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Loaded += Initialize;
        Closed += (_, _) => { previewWatcher?.Dispose(); previewReloadTimer?.Stop(); http.Dispose(); weatherHttp.Dispose(); updateHttp.Dispose(); imagery?.Dispose(); Browser.Dispose(); };
    }

    private void PreviewFileChanged(object sender, FileSystemEventArgs args) => PreviewFileChanged(args.Name);
    private void PreviewFileChanged(string? name)
    {
        if (name is not ("app.js" or "index.html")) return;
        _ = Dispatcher.InvokeAsync(() => { previewReloadTimer?.Stop(); previewReloadTimer?.Start(); });
    }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDirectory, "WebView"));
            await Browser.EnsureCoreWebView2Async(environment);
            var web = Browser.CoreWebView2;
            web.Settings.AreHostObjectsAllowed = false; web.Settings.AreDefaultScriptDialogsEnabled = false;
            web.Settings.IsStatusBarEnabled = false; web.Settings.AreDevToolsEnabled = false;
            web.SetVirtualHostNameToFolderMapping("app.sceneryatlas.local", webDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            web.NavigationStarting += (_, args) => { if (!IsLocal(args.Uri)) args.Cancel = true; };
            web.FrameNavigationStarting += (_, args) => args.Cancel = true;
            web.NewWindowRequested += (_, args) => args.Handled = true;
            web.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            web.DownloadStarting += (_, args) => args.Cancel = true;
            web.WebMessageReceived += Receive;
            imagery = new(web, Path.Combine(dataDirectory, "imagery-cache"));
            Browser.NavigationCompleted += (_, args) => { StartupMessage.Visibility = args.IsSuccess ? Visibility.Collapsed : Visibility.Visible; if (!args.IsSuccess) StartupMessage.Text = "The app could not load. Please restart Scenery Atlas."; };
            web.Navigate(Origin + "/index.html");
        }
        catch (Exception error) { StartupMessage.Text = "Could not start Scenery Atlas. Check that Microsoft Edge WebView2 Runtime is installed.\n" + error.Message; }
    }
    private static bool IsLocal(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) == Origin;
    private string ProfileId => accounts.Session?.Id ?? throw new InvalidOperationException("Please sign in first.");
    private object Library() { var profile = profiles.Load(ProfileId); return new { items = Catalogue.Merge(installed, profile), scan = lastScan, extraRoots = profile.ExtraRoots }; }

    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsLocal(e.Source) || !IsLocal(Browser.Source.AbsoluteUri)) return;
        string? id = null;
        if (e.WebMessageAsJson.Length > 25_000) return;
        await commands.WaitAsync();
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson); var request = document.RootElement;
            id = request.GetProperty("id").GetString();
            if (id is null || id.Length > 80) return;
            var method = request.GetProperty("method").GetString();
            var data = request.TryGetProperty("data", out var input) ? input : default;
            string Field(string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            object? result;
            switch (method)
            {
                case "auth.status": result = accounts.Session; break;
                case "auth.register": result = await Task.Run(() => accounts.Register(Field("username"), Field("displayName"), Field("password"))); installed = []; lastScan = null; break;
                case "auth.login": result = await Task.Run(() => accounts.Login(Field("username"), Field("password"))); installed = []; lastScan = null; break;
                case "auth.logout": accounts.Logout(); installed = []; lastScan = null; result = null; break;
                case "library.get": result = Library(); break;
                case "library.scan": result = await Scan(); break;
                case "weather.clouds": _ = ProfileId; _ = SendWeather(id, Field("mode") == "satellite"
                    ? "https://github.com/TofferAviation/Virtual-British-Airways-website/releases/download/bav-satellite-observations-v2/latest.json.gz"
                    : "https://github.com/TofferAviation/Virtual-British-Airways-website/releases/download/bav-global-forecast-v1/latest.json.gz", 8 * 1024 * 1024, true); return;
                case "weather.aurora": _ = ProfileId; _ = SendWeather(id, "https://services.swpc.noaa.gov/json/ovation_aurora_latest.json", 12_000_000); return;
                case "roots.add":
                    var profile = profiles.Load(ProfileId);
                    var picker = new OpenFolderDialog { Title = Field("kind") == "primary-community" ? "Choose the Community folder used by MSFS or Addon Linker" : "Choose a scenery package folder or add-on library" };
                    if (picker.ShowDialog(this) == true)
                    {
                        var source = SimulatorDiscovery.Canonical(picker.FolderName);
                        var simulator = Field("simulator");
                        if (simulator is not ("MSFS 2020" or "MSFS 2024" or "X-Plane 11" or "X-Plane 12")) throw new ArgumentException("Choose a supported simulator.");
                        var primary = Field("kind") == "primary-community";
                        if (primary && !simulator.StartsWith("MSFS")) throw new ArgumentException("Choose MSFS 2020 or 2024 for a Community folder.");
                        var mounted = primary || Field("kind") == "community";
                        var root = new PackageRoot(source, simulator, mounted, primary ? "Selected Community" : mounted ? simulator.StartsWith("X-Plane") ? "User-selected Custom Scenery" : "User-selected Community" : "Addon Linker library");
                        profiles.Save(ProfileId, profile with { ExtraRoots = profile.ExtraRoots.Where(r =>
                            !(r.Path.Equals(source, StringComparison.OrdinalIgnoreCase) && r.Simulator == simulator) &&
                            !(primary && r.Simulator == simulator && r.Source == "Selected Community")).Append(root).ToList() });
                    }
                    result = await Scan(); break;
                case "roots.remove":
                    var previous = profiles.Load(ProfileId); var rootPath = Field("path"); var rootSim = Field("simulator");
                    profiles.Save(ProfileId, previous with { ExtraRoots = previous.ExtraRoots.Where(r => !(r.Path.Equals(rootPath, StringComparison.OrdinalIgnoreCase) && r.Simulator == rootSim)).ToList() }); result = await Scan(); break;
                case "library.purchased":
                    var userProfile = profiles.Load(ProfileId); var productId = Field("productId");
                    profiles.Save(ProfileId, Catalogue.TogglePurchased(userProfile, productId, installed)); result = Library(); break;
                case "library.locate":
                    var locationProfile = profiles.Load(ProfileId); var locationId = Field("productId");
                    if (!Catalogue.Merge(installed, locationProfile).Any(x => x.Id == locationId)) throw new ArgumentException("Choose an existing scenery product.");
                    SceneryLocation location;
                    if (Field("icao").Trim() is { Length: > 0 } code)
                    {
                        if (!airports.TryGetValue(code.ToUpperInvariant(), out var airport)) throw new ArgumentException("That ICAO is not in the airport database. Enter coordinates instead.");
                        location = new(airport.Icao, airport.Latitude, airport.Longitude);
                    }
                    else
                    {
                        if (!double.TryParse(Field("latitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) || !double.TryParse(Field("longitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                            || !double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180) throw new ArgumentException("Enter valid latitude (−90 to 90) and longitude (−180 to 180).");
                        location = new(null, lat, lon);
                    }
                    var locations = new Dictionary<string, SceneryLocation>(locationProfile.Locations ?? []) { [locationId] = location };
                    profiles.Save(ProfileId, locationProfile with { Locations = locations }); result = Library(); break;
                case "catalogue.import":
                    _ = ProfileId;
                    var file = new OpenFileDialog { Title = "Import a scenery catalogue", Filter = "Scenery catalogue (*.json)|*.json", Multiselect = false };
                    if (file.ShowDialog(this) == true)
                    {
                        if (new FileInfo(file.FileName).Length > 10_000_000) throw new ArgumentException("Catalogue file exceeds 10 MB.");
                        var products = Catalogue.Import(await File.ReadAllTextAsync(file.FileName), airports);
                        var current = profiles.Load(ProfileId);
                        profiles.Save(ProfileId, current with { Catalogue = current.Catalogue.Concat(products).GroupBy(x => x.Id).Select(g => g.Last()).ToList() });
                    }
                    result = Library(); break;
                case "catalogue.ini": result = await RefreshIni(); break;
                case "store.open":
                    _ = ProfileId; var url = Field("url");
                    if (!Catalogue.IsStoreUrl(url)) throw new ArgumentException("Only public HTTPS store links can be opened.");
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); result = true; break;
                case "updates.folder":
                    _ = ProfileId;
                    var updateFolder = new OpenFolderDialog { Title = "Choose your local Scenery Atlas releases folder" };
                    if (updateFolder.ShowDialog(this) == true) UpdateChannel.Save(dataDirectory, updateFolder.FolderName);
                    result = UpdateChannel.Check(dataDirectory, typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0)); break;
                case "updates.check": _ = ProfileId; _ = SendUpdateStatus(id); return;
                case "updates.install":
                    _ = ProfileId; _ = InstallUpdate(id, Field("source") == "local"); return;
                default: throw new ArgumentException("Unsupported app action.");
            }
            if (IsLocal(Browser.Source.AbsoluteUri)) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = true, result }, WireJson));
        }
        catch (Exception error)
        {
            if (id is not null && IsLocal(Browser.Source.AbsoluteUri)) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = false, error = error is ArgumentException or InvalidOperationException ? error.Message : "That action could not finish. Your saved library is unchanged. " + error.Message }, WireJson));
        }
        finally { commands.Release(); }
    }

    private async Task SendUpdateStatus(string id)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
            var result = await UpdateChannel.CheckOnlineAsync(updateHttp, version, timeout.Token);
            if (Browser.Source is not null && IsLocal(Browser.Source.AbsoluteUri))
                Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = true, result }, WireJson));
        }
        catch (Exception error)
        {
            if (Browser.Source is not null && IsLocal(Browser.Source.AbsoluteUri))
                Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = false, error = "Update check failed: " + error.Message }, WireJson));
        }
    }

    private async Task InstallUpdate(string id, bool local)
    {
        try
        {
            var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
            string installer;
            if (local)
            {
                var update = UpdateChannel.Check(dataDirectory, version);
                if (!update.Available || update.InstallerPath is null) throw new InvalidDataException("No verified local update is available.");
                installer = update.InstallerPath;
            }
            else installer = await UpdateChannel.DownloadOnlineAsync(updateHttp, dataDirectory, version);
            Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
            if (Browser.Source is not null && IsLocal(Browser.Source.AbsoluteUri))
                Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = true, result = true }, WireJson));
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception error)
        {
            if (Browser.Source is not null && IsLocal(Browser.Source.AbsoluteUri))
                Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = false, error = "Update could not install: " + error.Message }, WireJson));
        }
    }

    private async Task SendWeather(string id, string path, int byteLimit, bool cloudArtifact = false)
    {
        // Weather can take longer than a local scan; it must not hold the
        // command lock while users inspect folders or change map views.
        try
        {
            var result = await ReadGlobeFeed(path, byteLimit, cloudArtifact);
            if (Browser.Source is not null && IsLocal(Browser.Source.AbsoluteUri))
                Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = true, result }, WireJson));
        }
        catch
        {
            if (Browser.Source is not null && IsLocal(Browser.Source.AbsoluteUri))
                Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = false, error = "The public weather source is unavailable." }, WireJson));
        }
    }

    private async Task<JsonElement> ReadGlobeFeed(string path, int byteLimit, bool cloudArtifact)
    {
        // Fixed public weather sources. No user-supplied URL or website credentials.
        if (path is not ("https://github.com/TofferAviation/Virtual-British-Airways-website/releases/download/bav-global-forecast-v1/latest.json.gz"
            or "https://github.com/TofferAviation/Virtual-British-Airways-website/releases/download/bav-satellite-observations-v2/latest.json.gz"
            or "https://services.swpc.noaa.gov/json/ovation_aurora_latest.json")) throw new ArgumentException("Unsupported weather source.");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await weatherHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > byteLimit) throw new InvalidDataException("The weather feed exceeded its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > byteLimit) throw new InvalidDataException("The weather feed exceeded its size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        if (!cloudArtifact)
        {
            using var json = await JsonDocument.ParseAsync(buffer);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The weather feed had an unexpected format.");
            return json.RootElement.Clone();
        }
        await using var decoded = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionMode.Decompress, leaveOpen: true))
        {
            while ((count = await gzip.ReadAsync(chunk)) != 0)
            {
                if (decoded.Length + count > 120 * 1024 * 1024) throw new InvalidDataException("The cloud artifact exceeded its size limit.");
                decoded.Write(chunk, 0, count);
            }
        }
        decoded.Position = 0;
        var node = JsonNode.Parse(decoded) as JsonObject ?? throw new InvalidDataException("Invalid cloud artifact.");
        if (node["encoding"]?.GetValue<string>() != "rgba8-base64") throw new InvalidDataException("Invalid cloud encoding.");
        var width = node["width"]?.GetValue<int>() ?? 0;
        var height = node["height"]?.GetValue<int>() ?? 0;
        if (width < 4 || height < 2 || (long)width * height > 6_480_000) throw new InvalidDataException("Invalid cloud grid.");
        var expected = checked(width * height * 4);
        if (node["layers"] is JsonArray layers) foreach (var layer in layers.OfType<JsonObject>())
            layer["volume"] = CompressVolume(layer["volume"]?.GetValue<string>(), expected);
        if (node["satellite"] is JsonObject satellite)
        {
            if (satellite["current"] is JsonObject current) current["volume"] = CompressVolume(current["volume"]?.GetValue<string>(), expected);
            if (satellite["previous"] is JsonObject previous) previous["volume"] = CompressVolume(previous["volume"]?.GetValue<string>(), expected);
            satellite["motion"] = CompressVolume(satellite["motion"]?.GetValue<string>(), expected);
        }
        node["encoding"] = "rgba8-gzip-base64";
        return JsonSerializer.SerializeToElement(node, WireJson);
    }

    private static string CompressVolume(string? base64, int expected)
    {
        var raw = Convert.FromBase64String(base64 ?? throw new InvalidDataException("Missing cloud volume."));
        if (raw.Length != expected) throw new InvalidDataException("Incomplete cloud volume.");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(raw);
        return Convert.ToBase64String(compressed.ToArray());
    }

    private async Task<object> Scan()
    {
        var profile = profiles.Load(ProfileId);
        var discoveryWarnings = new List<string>(); var roots = SimulatorDiscovery.Discover(configs, discoveryWarnings)
            .Concat(XPlaneDiscovery.Discover(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            .Concat(profile.ExtraRoots)
            .GroupBy(r => (r.Path.ToLowerInvariant(), r.Simulator))
            .Select(g => g.OrderByDescending(r => r.Source == "Selected Community").ThenByDescending(r => r.Mounted).First()).ToList();
        lastScan = await Task.Run(() =>
        {
            var scans = roots.GroupBy(r => r.Simulator).Select(group => group.Key.StartsWith("X-Plane")
                ? new XPlaneScanner(airports).Scan(group)
                : new SceneryScanner(airports).Scan(group,
                    configs.Where(c => c.Simulator == group.Key).SelectMany(c => SimulatorDiscovery.ActivationFiles(c.Directory)))).ToList();
            return new ScanResult(scans.SelectMany(s => s.Items).ToList(), scans.SelectMany(s => s.Roots).ToList(), discoveryWarnings.Concat(scans.SelectMany(s => s.Warnings)).ToList(), DateTimeOffset.UtcNow, scans.Sum(s => s.PackagesExamined));
        });
        installed = lastScan.Items;
        return Library();
    }

    private async Task<object> RefreshIni()
    {
        var current = profiles.Load(ProfileId);
        // Public product links only, not a private store-account or purchase API.
        using var response = await http.GetAsync("https://inibuilds.com/pages/iniscenery");
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests) throw new ArgumentException("iniBuilds is not allowing catalogue access from this app. Import an approved catalogue instead. Installed scenery can still be scanned locally.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 5_000_000) throw new ArgumentException("Store response is too large.");
        var html = await response.Content.ReadAsStringAsync();
        if (html.Length > 5_000_000) throw new ArgumentException("Store response is too large.");
        var products = Catalogue.ParseIniSceneryPage(html, airports);
        if (products.Count == 0) throw new ArgumentException("The public store listing could not be read. Import a catalogue or try later; your existing catalogue is retained.");
        profiles.Save(ProfileId, current with { Catalogue = current.Catalogue.Concat(products).GroupBy(x => x.Id).Select(g => g.Last()).ToList() });
        return Library();
    }
}
