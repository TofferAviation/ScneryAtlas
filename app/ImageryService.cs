using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace SceneryAtlas.App;

/// <summary>Fixed public imagery sources; never exposes a general URL/file proxy to the renderer.</summary>
public sealed class ImageryService : IDisposable
{
    private readonly CoreWebView2 web;
    private readonly string cache;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(18) };
    private readonly SemaphoreSlim downloads = new(4);
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> pending = new();
    public ImageryService(CoreWebView2 browser, string directory)
    {
        web = browser; cache = directory; Directory.CreateDirectory(cache);
        web.AddWebResourceRequestedFilter("https://app.sceneryatlas.local/api/radar/*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += Requested;
        _ = Task.Run(() => { try { var files = new DirectoryInfo(cache).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList(); long size = 0; foreach (var file in files) { size += file.Length; if (size > 256L * 1024 * 1024) file.Delete(); } } catch (IOException) { } });
    }

    private async Task<byte[]> Download(string key, Uri uri)
    {
        var path = Path.Combine(cache, key.Replace('/', '_'));
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-7)) return await File.ReadAllBytesAsync(path);
        var lazy = pending.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(async () =>
        {
            await downloads.WaitAsync();
            try
            {
                using var response = await http.GetAsync(uri); response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true) throw new IOException("Imagery source returned no image.");
                var bytes = await response.Content.ReadAsByteArrayAsync(); if (bytes.Length > 8_000_000) throw new IOException("Imagery too large.");
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes); File.Move(temp, path, true); return bytes;
            }
            finally { downloads.Release(); }
        }));
        try { return await lazy.Value; } finally { pending.TryRemove(key, out _); }
    }

    private async void Requested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        using var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.Request.Uri); var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (!int.TryParse(query["z"], out var z) || !int.TryParse(query["x"], out var x) || !int.TryParse(query["y"], out var y) || z is < 0 or > 14 || x < 0 || x >= (1 << (z + 1)) || y < 0 || y >= (1 << z)) throw new ArgumentException("Invalid tile.");
            byte[] bytes; string type;
            if (uri.AbsolutePath == "/api/radar/imagery")
            {
                var night = query["kind"] == "night"; if (z > (night ? 6 : 10)) throw new ArgumentException("Invalid imagery level.");
                var span = 180d / (1 << z); var west = -180 + x * span; var north = 90 - y * span;
                var layer = night ? "VIIRS_CityLights_2012" : z >= 7 ? "BlueMarble_NextGeneration,Landsat_WELD_CorrectedReflectance_TrueColor_Global_Annual" : "BlueMarble_NextGeneration";
                var bbox = string.Join(",", new[] { west, north - span, west + span, north }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
                var url = "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS=" + Uri.EscapeDataString(layer) + "&STYLES=&FORMAT=image/jpeg&SRS=EPSG:4326&BBOX=" + bbox + "&WIDTH=512&HEIGHT=512" + (!night && z >= 7 ? "&TIME=2000-12-01" : "");
                bytes = await Download($"{(night ? "night" : "day")}_{z}_{x}_{y}.jpg", new Uri(url)); type = "image/jpeg";
            }
            else if (uri.AbsolutePath == "/api/radar/elevation" && z >= 4) { bytes = await Elevation(z, x, y); type = "application/octet-stream"; }
            else throw new ArgumentException("Unknown imagery request.");
            e.Response = web.Environment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", "Content-Type: " + type + "\r\nCache-Control: public, max-age=86400\r\nX-Elevation-Grid: 65");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or TaskCanceledException or NotSupportedException)
        {
            e.Response = web.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("Public imagery temporarily unavailable; bundled Earth retained.")), 503, "Unavailable", "Content-Type: text/plain\r\nRetry-After: 30");
        }
    }

    private async Task<byte[]> Elevation(int z, int x, int y)
    {
        const int size = 65;
        var span = 180d / (1 << z); var north = 90 - y * span; var west = -180 + x * span;
        var latitude = Math.Max(Math.Abs(north), Math.Abs(north - span));
        var level = Math.Clamp(z + 1 + (int)Math.Floor(Math.Log2(Math.Max(.02, Math.Cos(latitude * Math.PI / 180)))), 0, 14); var n = 1 << level;
        var points = new List<(string Key, double X, double Y)?>(); var keys = new HashSet<(int X, int Y)>();
        for (var row = 0; row < size; row++) for (var col = 0; col < size; col++)
        {
            var lat = north - span * row / (size - 1); var lon = west + span * col / (size - 1);
            if (Math.Abs(lat) > 85.05112878) { points.Add(null); continue; }
            var px = (lon + 180) / 360 * n; var py = (1 - Math.Asinh(Math.Tan(lat * Math.PI / 180)) / Math.PI) * n / 2;
            var tx = Math.Clamp((int)Math.Floor(px), 0, n - 1); var ty = Math.Clamp((int)Math.Floor(py), 0, n - 1);
            keys.Add((tx, ty)); points.Add(($"{tx}/{ty}", (px - tx) * 256, (py - ty) * 256));
        }
        if (keys.Count > 24) throw new IOException("Elevation tile footprint too large.");
        var tiles = new Dictionary<string, float[]>();
        foreach (var (tx, ty) in keys)
        {
            var bytes = await Download($"dem_{level}_{tx}_{ty}.png", new Uri($"https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{level}/{tx}/{ty}.png"));
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            if (bitmap.PixelWidth != 256 || bitmap.PixelHeight != 256) throw new IOException("Unexpected terrain tile dimensions.");
            var pixels = new byte[256 * 256 * 4]; bitmap.CopyPixels(pixels, 256 * 4, 0); var heights = new float[256 * 256];
            for (var i = 0; i < heights.Length; i++) heights[i] = pixels[i * 4 + 2] * 256 + pixels[i * 4 + 1] + pixels[i * 4] / 256f - 32768;
            tiles[$"{tx}/{ty}"] = heights;
        }
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        foreach (var point in points)
        {
            var h = 0d;
            if (point is { } p)
            {
                var grid = tiles[p.Key]; var px = Math.Clamp(p.X - .5, 0, 255); var py = Math.Clamp(p.Y - .5, 0, 255);
                var cx = Math.Min(254, (int)Math.Floor(px)); var cy = Math.Min(254, (int)Math.Floor(py)); var dx = px - cx; var dy = py - cy; var a = cy * 256 + cx;
                h = (grid[a] * (1 - dx) + grid[a + 1] * dx) * (1 - dy) + (grid[a + 256] * (1 - dx) + grid[a + 257] * dx) * dy;
            }
            writer.Write((float)Math.Clamp(h, 0, 9000));
        }
        return output.ToArray();
    }
    public void Dispose() { web.WebResourceRequested -= Requested; http.Dispose(); }
}
