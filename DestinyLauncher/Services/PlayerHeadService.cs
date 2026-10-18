using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DestinyLauncher.Services;

public sealed class PlayerHeadService : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _directory;
    public PlayerHeadService(LauncherPaths paths, HttpClient? http = null)
    {
        _directory = Path.Combine(paths.Cache, "player-heads");
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }
    private string CachePath(string name) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("verified-mojang-face-v1:" + name.ToLowerInvariant()))) + ".png");

    public async Task<BitmapImage?> GetCachedHeadAsync(string username, CancellationToken token)
    {
        if (!Regex.IsMatch(username, "^[A-Za-z0-9_]{3,16}$")) return null;
        try
        {
            var file = CachePath(username);
            if (!File.Exists(file) || new FileInfo(file).Length > 1_000_000) return null;
            return Decode(await File.ReadAllBytesAsync(file, token));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
    public async Task<BitmapImage?> GetHeadAsync(string username, CancellationToken token)
    {
        if (!Regex.IsMatch(username, "^[A-Za-z0-9_]{3,16}$")) return null;
        var file = CachePath(username);
        var cached = await GetCachedHeadAsync(username, token);
        if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < CacheLifetime) return cached;
        var retryFile = file + ".retry";
        if (File.Exists(retryFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(retryFile) < TimeSpan.FromHours(6)) return cached;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            // Public texture lookup only; the game session remains offline. A renderer's
            // silent Steve/Alex fallback must never overwrite a cached verified skin.
            using var player = JsonDocument.Parse(await DownloadAsync(new Uri("https://api.mojang.com/users/profiles/minecraft/" + Uri.EscapeDataString(username)), 32_768, timeout.Token));
            var uuid = player.RootElement.GetProperty("id").GetString();
            if (!Regex.IsMatch(uuid ?? "", "^[a-fA-F0-9]{32}$")) throw new InvalidDataException();
            using var profile = JsonDocument.Parse(await DownloadAsync(new Uri("https://sessionserver.mojang.com/session/minecraft/profile/" + uuid), 131_072, timeout.Token));
            var encoded = profile.RootElement.GetProperty("properties").EnumerateArray()
                .First(p => p.GetProperty("name").GetString() == "textures").GetProperty("value").GetString()!;
            using var textures = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var address = new Uri(textures.RootElement.GetProperty("textures").GetProperty("SKIN").GetProperty("url").GetString()!);
            if (address.Host != "textures.minecraft.net" || !Regex.IsMatch(address.AbsolutePath, "^/texture/[a-fA-F0-9]+$")) throw new InvalidDataException();
            address = new UriBuilder(address) { Scheme = "https", Port = -1 }.Uri;
            var skin = Decode(await DownloadAsync(address, 1_000_000, timeout.Token));
            if (skin.PixelWidth != 64 || skin.PixelHeight is not (32 or 64)) throw new InvalidDataException();
            var face = new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8));
            var overlay = new CroppedBitmap(skin, new Int32Rect(40, 8, 8, 8));
            var overlayPixels = new byte[8 * 8 * 4];
            new FormatConvertedBitmap(overlay, PixelFormats.Bgra32, null, 0).CopyPixels(overlayPixels, 32, 0);
            // Legacy skins sometimes fill the unused hat rectangle with an opaque color.
            var drawOverlay = skin.PixelHeight != 32 || Enumerable.Range(0, 64).Any(i => overlayPixels[i * 4 + 3] < 255);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            { drawing.DrawImage(face, new Rect(0, 0, 8, 8)); if (drawOverlay) drawing.DrawImage(overlay, new Rect(0, 0, 8, 8)); }
            var result = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32); result.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result));
            using var bytes = new MemoryStream(); encoder.Save(bytes);
            var data = bytes.ToArray(); var image = Decode(data);
            try
            {
                Directory.CreateDirectory(_directory);
                var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(temporary, data, token); File.Move(temporary, file, true); File.Delete(retryFile); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return image;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            try { Directory.CreateDirectory(_directory); await File.WriteAllTextAsync(retryFile, "", token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { }
            return cached;
        }
    }
    private async Task<byte[]> DownloadAsync(Uri uri, int limit, CancellationToken token)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        { if (output.Length + read > limit) throw new InvalidDataException(); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private static BitmapImage Decode(byte[] data)
    {
        if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})
            || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)) > 512
            || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4)) > 512) throw new InvalidDataException();
        using var stream = new MemoryStream(data, false);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
