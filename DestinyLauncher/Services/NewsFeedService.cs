using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;

/// <summary>Optional public content. Its cache and failures are independent of game installation.</summary>
public sealed class NewsFeedService : IDisposable
{
    public static readonly Uri FeedUri = new("https://destiny-dynasty.github.io/Launcher-News/news.json");
    private const int MaxFeedBytes = 128 * 1024;
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _cache;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public NewsFeedService(LauncherPaths paths, HttpClient? http = null)
    {
        _cache = Path.Combine(paths.Cache, "news");
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public static IReadOnlyList<NewsSlide> BundledSamples() =>
    [
        new("sample-valley", "Muestra · Bienvenido a Destiny Dynasty.", Bundled("destiny-hero-gold-v3.png")),
        new("sample-citadel", "Muestra · Tu próxima aventura comienza aquí.", Bundled("destiny-hero-gold-v2.png"))
    ];

    private static BitmapImage Bundled(string name)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 1000;
        image.UriSource = new Uri($"pack://application:,,,/DestinyLauncher;component/Assets/{name}");
        image.EndInit(); image.Freeze();
        return image;
    }

    // Null means no usable cache. An empty list deliberately hides the carousel.
    public async Task<IReadOnlyList<NewsSlide>?> ReadCacheAsync(CancellationToken token)
    {
        var file = Path.Combine(_cache, "news.json");
        if (!File.Exists(file)) return null;
        try
        {
            if (new FileInfo(file).Length > MaxFeedBytes) return null;
            var bytes = await File.ReadAllBytesAsync(file, token);
            var items = Parse(bytes);
            var slides = new List<NewsSlide>();
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
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FileFormatException) { }
            }
            return items.Count > 0 && slides.Count == 0 ? null : slides;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    public async Task<IReadOnlyList<NewsSlide>> RefreshAsync(CancellationToken token)
    {
        var items = Parse(await DownloadAsync(FeedUri, MaxFeedBytes, token));
        var ready = new List<NewsFeedItem>();
        var slides = new List<NewsSlide>();
        Directory.CreateDirectory(_cache);
        foreach (var item in items)
        {
            try
            {
                var imageFile = ImagePath(item.ImageUrl);
                byte[]? data = null;
                BitmapImage? image = null;
                if (File.Exists(imageFile) && new FileInfo(imageFile).Length <= MaxImageBytes)
                {
                    try { image = await Task.Run(() => Decode(File.ReadAllBytes(imageFile)), token); }
                    catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FileFormatException) { }
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
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or ArgumentException or FileFormatException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        if (items.Count > 0 && slides.Count == 0) throw new InvalidDataException("No se pudo cargar ninguna imagen de novedades.");
        await WriteAtomicAsync(Path.Combine(_cache, "news.json"), JsonSerializer.SerializeToUtf8Bytes(new NewsFeedDocument { SchemaVersion = 1, Items = ready }, Json), token);
        // Bound disk use; URLs should change when an image is replaced.
        var keep = ready.Select(i => ImagePath(i.ImageUrl)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in Directory.EnumerateFiles(_cache, "*.image").Where(p => !keep.Contains(p)))
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return slides;
    }

    private static List<NewsFeedItem> Parse(byte[] bytes)
    {
        if (bytes.Length > MaxFeedBytes) throw new InvalidDataException("El feed de novedades es demasiado grande.");
        var feed = JsonSerializer.Deserialize<NewsFeedDocument>(bytes, Json);
        if (feed is null || feed.SchemaVersion != 1 || feed.Items is null) throw new InvalidDataException("Formato de novedades no compatible.");
        var result = new List<NewsFeedItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in feed.Items.Take(32))
        {
            if (item is null || !item.Enabled || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 64 || !ids.Add(item.Id)) continue;
            if (string.IsNullOrWhiteSpace(item.Caption) || item.Caption.Length > 90 || item.Caption.Any(char.IsControl)) continue;
            if (!TryHttps(item.ImageUrl, out var image)) continue;
            if (!string.IsNullOrWhiteSpace(item.LinkUrl) && !TryHttps(item.LinkUrl, out _)) continue;
            result.Add(new NewsFeedItem { Id = item.Id, Caption = item.Caption.Trim(), ImageUrl = image!.AbsoluteUri,
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
    private static NewsSlide ToSlide(NewsFeedItem item, BitmapImage image) => new(item.Id, item.Caption, image, item.LinkUrl is null ? null : new Uri(item.LinkUrl));

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
    private static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth is < 1 or > 4096 || frame.PixelHeight is < 1 or > 4096) throw new InvalidDataException("Dimensiones de imagen no admitidas.");
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Min(1000, frame.PixelWidth); image.StreamSource = stream;
        image.EndInit(); image.Freeze(); return image;
    }
    private static async Task WriteAtomicAsync(string file, byte[] data, CancellationToken token)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, data, token); File.Move(temp, file, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
