using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using DestinyLauncher.Models;

using DestinyLauncher.Services;
namespace DestinyLauncher.Linux;

/// <summary>Optional public content. Its cache and failures are independent of game installation.</summary>
public sealed class LinuxNewsFeedService : IDisposable
{
    public static readonly Uri FeedUri = new("https://destiny-dynasty.github.io/news.json");
    private const int MaxFeedBytes = 128 * 1024;
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _cache;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public LinuxNewsFeedService(LauncherPaths paths, HttpClient? http = null)
    {
        _cache = Path.Combine(paths.Cache, "news");
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public static IReadOnlyList<LinuxNewsSlide> BundledNews() =>
    [
        new("launcher-2026-10", "Nuevo launcher: una nueva puerta a Destiny Dynasty.", Bundled("News/nuevo-launcher-v1.jpg"), new Uri(FeedUri, "novedades/nuevo-launcher/")),
        new("nuevo-comienzo-2026-10", "Próximo reinicio de mundo y nivel. Conservaremos estructuras y objetos compatibles.", Bundled("News/nuevo-comienzo-v1.jpg"), new Uri(FeedUri, "novedades/nuevo-comienzo/")),
        new("rpg-desarrollo-2026-10", "Un nuevo RPG en desarrollo: misiones, habilidades y una aventura más profunda.", Bundled("News/rpg-en-desarrollo-v1.jpg"), new Uri(FeedUri, "novedades/rpg-en-desarrollo/"))
    ];

    private static Bitmap Bundled(string name)
    {
        using var stream = AssetLoader.Open(new Uri("avares://DestinyLauncher.Linux/Assets/" + name));
        return Bitmap.DecodeToWidth(stream, 1000, BitmapInterpolationMode.HighQuality);
    }
    // Null means no usable cache. An empty list deliberately hides the carousel.
    public async Task<IReadOnlyList<LinuxNewsSlide>?> ReadCacheAsync(CancellationToken token)
    {
        var file = Path.Combine(_cache, "news.json");
        if (!File.Exists(file)) return null;
        var slides = new List<LinuxNewsSlide>();
        var transferred = false;
        try
        {
            if (new FileInfo(file).Length > MaxFeedBytes) return null;
            var bytes = await File.ReadAllBytesAsync(file, token);
            var items = Parse(bytes);
            foreach (var item in items)
            {
                var imageFile = ImagePath(item.ImageUrl);
                if (!File.Exists(imageFile)) continue;
                try
                {
                    if (new FileInfo(imageFile).Length > MaxImageBytes) continue;
                    var data = await File.ReadAllBytesAsync(imageFile, token);
                    var image = await Task.Run(() => Decode(data), token);
                    slides.Add(ToSlide(item, image));
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { }
            }
            transferred = true;
            return items.Count > 0 && slides.Count == 0 ? null : slides;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { return null; }
        finally { if (!transferred) DisposeSlides(slides); }
    }

    public async Task<IReadOnlyList<LinuxNewsSlide>> RefreshAsync(CancellationToken token)
    {
        var items = Parse(await DownloadAsync(FeedUri, MaxFeedBytes, token));
        var ready = new List<LinuxNewsFeedItem>();
        var slides = new List<LinuxNewsSlide>();
        var transferred = false;
        try
        {
            Directory.CreateDirectory(_cache);
            foreach (var item in items)
            {
                Bitmap? image = null;
                try
                {
                    var imageFile = ImagePath(item.ImageUrl);
                    byte[]? data = null;
                    if (File.Exists(imageFile) && new FileInfo(imageFile).Length <= MaxImageBytes)
                    {
                        try { image = await Task.Run(() => Decode(File.ReadAllBytes(imageFile)), token); }
                        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { }
                    }
                    if (image is null)
                    {
                        data = await DownloadAsync(new Uri(item.ImageUrl), MaxImageBytes, token);
                        image = await Task.Run(() => Decode(data), token);
                    }
                    // Never save undecodable content over a working cache.
                    if (data is not null) await WriteAtomicAsync(imageFile, data, token);
                    ready.Add(item);
                    slides.Add(ToSlide(item, image));
                    image = null; // The result list owns the decoded image now.
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or ArgumentException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                finally { image?.Dispose(); }
            }
            if (items.Count > 0 && slides.Count == 0) throw new InvalidDataException("No se pudo cargar ninguna imagen de novedades.");
            await WriteAtomicAsync(Path.Combine(_cache, "news.json"), JsonSerializer.SerializeToUtf8Bytes(new LinuxNewsFeedDocument { SchemaVersion = 1, Items = ready }, Json), token);
            // Bound disk use; URLs should change when an image is replaced.
            var keep = ready.Select(i => ImagePath(i.ImageUrl)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in Directory.EnumerateFiles(_cache, "*.image").Where(p => !keep.Contains(p)))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            transferred = true;
            return slides;
        }
        finally { if (!transferred) DisposeSlides(slides); }
    }

    private static void DisposeSlides(IEnumerable<LinuxNewsSlide> slides)
    {
        foreach (var slide in slides) slide.Image.Dispose();
    }

    private static List<LinuxNewsFeedItem> Parse(byte[] bytes)
    {
        if (bytes.Length > MaxFeedBytes) throw new InvalidDataException("El feed de novedades es demasiado grande.");
        var feed = JsonSerializer.Deserialize<LinuxNewsFeedDocument>(bytes, Json);
        if (feed is null || feed.SchemaVersion != 1 || feed.Items is null) throw new InvalidDataException("Formato de novedades no compatible.");
        var result = new List<LinuxNewsFeedItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in feed.Items.Take(32))
        {
            if (item is null || !item.Enabled || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 64 || !ids.Add(item.Id)) continue;
            if (string.IsNullOrWhiteSpace(item.Caption) || item.Caption.Length > 90 || item.Caption.Any(char.IsControl)) continue;
            if (!TryHttps(item.ImageUrl, out var image)) continue;
            if (!string.IsNullOrWhiteSpace(item.LinkUrl) && !TryHttps(item.LinkUrl, out _)) continue;
            result.Add(new LinuxNewsFeedItem { Id = item.Id, Caption = item.Caption.Trim(), ImageUrl = image!.AbsoluteUri,
                LinkUrl = string.IsNullOrWhiteSpace(item.LinkUrl) ? null : new Uri(FeedUri, item.LinkUrl).AbsoluteUri });
            if (result.Count == 8) break;
        }
        // A malformed nonempty feed must not silently erase previously working content.
        if (feed.Items.Any(i => i is not null && i.Enabled) && result.Count == 0) throw new InvalidDataException("No hay novedades válidas en el feed.");
        return result;
    }

    private static bool TryHttps(string? value, out Uri? uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(FeedUri, value, out uri)
            && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
    }
    private string ImagePath(string url) => Path.Combine(_cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".image");
    private static LinuxNewsSlide ToSlide(LinuxNewsFeedItem item, Bitmap image) => new(item.Id, item.Caption, image, item.LinkUrl is null ? null : new Uri(item.LinkUrl));

    private async Task<byte[]> DownloadAsync(Uri uri, int limit, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        token = deadline.Token;
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("La descarga debe usar HTTPS.");
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("La descarga de novedades supera el límite.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var result = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > limit) throw new InvalidDataException("La descarga de novedades supera el límite.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
    private static Bitmap Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        int width;
        using (var codec = SKCodec.Create(stream))
        {
            if (codec is null || codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096) throw new InvalidDataException("Dimensiones de imagen no admitidas.");
            // Keep small originals at their native resolution. Upscaling here can
            // turn a narrow portrait into an enormous bitmap before layout scales it down.
            width = Math.Min(1000, codec.Info.Width);
        }
        using var input = new MemoryStream(bytes, false);
        return Bitmap.DecodeToWidth(input, width, BitmapInterpolationMode.HighQuality);
    }
    private static async Task WriteAtomicAsync(string file, byte[] data, CancellationToken token)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, data, token); File.Move(temp, file, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}

public sealed class LinuxNewsFeedDocument
{
    public int SchemaVersion { get; set; }
    public List<LinuxNewsFeedItem> Items { get; set; } = [];
}

public sealed class LinuxNewsFeedItem
{
    public string Id { get; set; } = "";
    public string Caption { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string? LinkUrl { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed record LinuxNewsSlide(string Id, string Caption, Bitmap Image, Uri? Link = null);

