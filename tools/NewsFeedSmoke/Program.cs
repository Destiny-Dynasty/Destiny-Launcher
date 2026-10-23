using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "Destiny-news-tests", Guid.NewGuid().ToString("N"));
        try { Run(root, args.Contains("--live")).GetAwaiter().GetResult(); Console.WriteLine($"PASS: {_checks} news assertions; isolated data: {root}"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Run(string root, bool live)
    {
        var paths = new LauncherPaths(root);
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 30, 70, 120, 255, 20, 50, 90, 255, 30, 70, 120, 255, 20, 50, 90, 255 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); var png = stream.ToArray();
        var handler = new FakeHttp();
        using var http = new HttpClient(handler);
        using var service = new NewsFeedService(paths, http);
        Assert(await service.ReadCacheAsync(default) is null, "First open has no cached feed");
        string Feed(params NewsFeedItem[] items) => JsonSerializer.Serialize(new NewsFeedDocument { SchemaVersion = 1, Items = [.. items] }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        NewsFeedItem Item(string id, string url) => new() { Id = id, Caption = "Muestra breve · " + id, ImageUrl = url };
        var first = Item("one", "images/one.png"); first.LinkUrl = "https://example.com/news";
        var second = Item("two", "images/two.png");
        var feed = Feed(first, second);
        handler.Reply = uri => uri == NewsFeedService.FeedUri ? Json(feed) : Image(png);
        var slides = await service.RefreshAsync(default);
        Assert(slides.Count == 2 && slides[0].Id == "one" && slides[1].Id == "two", "Remote ordering survives parsing and image loading");
        Assert(slides.All(s => s.Image.IsFrozen), "Downloaded images can safely cross thread boundaries");
        Assert(slides[0].Link?.AbsoluteUri == first.LinkUrl, "Optional HTTPS destination is retained");
        Assert(handler.Requests.Contains(new Uri(NewsFeedService.FeedUri, first.ImageUrl)), "Relative images resolve against the feed directory");
        Assert(!Directory.Exists(paths.Instances) && !File.Exists(paths.ConfigFile), "News never writes installation or profile data");
        var cacheFile = Path.Combine(paths.Cache, "news", "news.json");
        var originalCache = await File.ReadAllTextAsync(cacheFile);
        handler.Requests.Clear();
        slides = await service.RefreshAsync(default);
        Assert(handler.Requests.Count == 1, "Unchanged image URLs reuse decoded disk cache without another download");
        handler.Reply = _ => throw new HttpRequestException("offline");
        Assert((await service.ReadCacheAsync(default))?.Count == 2, "Offline startup restores both cached images");
        await Throws<HttpRequestException>(() => service.RefreshAsync(default), "Network failure is reported to the optional-content caller");
        Assert(await File.ReadAllTextAsync(cacheFile) == originalCache, "Network failure preserves the last working feed");
        handler.Reply = _ => Json("{broken");
        await Throws<JsonException>(() => service.RefreshAsync(default), "Malformed JSON is rejected");
        handler.Reply = _ => Json("{\"schemaVersion\":99,\"items\":[]}");
        await Throws<InvalidDataException>(() => service.RefreshAsync(default), "Unknown schema preserves compatibility instead of guessing");
        Assert(await File.ReadAllTextAsync(cacheFile) == originalCache, "Malformed feed never replaces the valid cache");
        handler.Reply = _ => Json(Feed(Item("unsafe", "http://example.com/image.png")));
        await Throws<InvalidDataException>(() => service.RefreshAsync(default), "HTTP image URLs cannot be loaded");
        var unsafeLink = Item("unsafe-link", "images/one.png"); unsafeLink.LinkUrl = "file:///C:/Windows/notepad.exe";
        handler.Reply = _ => Json(Feed(unsafeLink));
        await Throws<InvalidDataException>(() => service.RefreshAsync(default), "Non-HTTPS launch destinations cannot enter the carousel");
        var overlong = Item("long", "images/long.png"); overlong.Caption = new string('x', 91);
        handler.Reply = _ => Json(Feed(overlong));
        await Throws<InvalidDataException>(() => service.RefreshAsync(default), "Long captions do not become scrolling articles");
        handler.Reply = uri => uri == NewsFeedService.FeedUri ? Json(Feed(Item("bad", "images/bad.png"))) : Image(Encoding.UTF8.GetBytes("not an image"));
        await Throws<InvalidDataException>(() => service.RefreshAsync(default), "A broken image cannot erase every working slide");
        Assert(await File.ReadAllTextAsync(cacheFile) == originalCache, "Undecodable image preserves existing feed");
        handler.Reply = uri => uri == NewsFeedService.FeedUri ? Json(Feed(first, Item("bad", "images/bad.png"))) : Image([0, 1, 2]);
        slides = await service.RefreshAsync(default);
        Assert(slides.Count == 1 && slides[0].Id == "one", "One broken image does not hide the remaining valid announcement");
        handler.Reply = _ => { var response = Json("{}"); response.Content.Headers.ContentLength = 128 * 1024 + 1; return response; };
        await Throws<InvalidDataException>(() => service.RefreshAsync(default), "Oversized feed is rejected before buffering");
        handler.Reply = uri => { var response = uri == NewsFeedService.FeedUri ? Json(Feed(Item("big", "images/big.png"))) : Image(png); if (uri != NewsFeedService.FeedUri) response.Content.Headers.ContentLength = 4 * 1024 * 1024 + 1; return response; };
        await Throws<InvalidDataException>(() => service.RefreshAsync(default), "Oversized images cannot replace a working cache");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => service.RefreshAsync(canceled.Token), "Closing the launcher cancels outstanding news work");
        Assert(!Directory.EnumerateFiles(Path.Combine(paths.Cache, "news"), "*.tmp").Any(), "Canceled or failed writes leave no temporary files");
        handler.Reply = _ => Json(Feed());
        Assert((await service.RefreshAsync(default)).Count == 0, "An explicitly empty feed hides the carousel");
        Assert((await service.ReadCacheAsync(default))?.Count == 0, "An empty feed stays hidden when offline");
        Assert(!Directory.EnumerateFiles(Path.Combine(paths.Cache, "news"), "*.image").Any(), "Removed news images do not grow the cache forever");
        await File.WriteAllTextAsync(cacheFile, "broken");
        Assert(await service.ReadCacheAsync(default) is null, "Damaged local cache allows bundled fallback");
        if (live)
        {
            using var remote = new NewsFeedService(new LauncherPaths(Path.Combine(root, "live")));
            var actual = await remote.RefreshAsync(default);
            Assert(actual.Count == 3 && actual.All(s => !s.Caption.StartsWith("Muestra") && s.Link is not null), "Published feed contains three announcements with readable images and article links");
        }
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Image(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); } catch (T) { _checks++; return; }
        throw new InvalidOperationException(message);
    }
    private sealed class FakeHttp : HttpMessageHandler
    {
        internal Func<Uri, HttpResponseMessage> Reply { get; set; } = _ => throw new InvalidOperationException();
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!);
            var response = Reply(request.RequestUri!); response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
