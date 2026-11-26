using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;

using Avalonia.Media.Imaging;

using DestinyLauncher.Services;
namespace DestinyLauncher.Linux;

public sealed class LinuxPlayerHeadService : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _directory;
    public LinuxPlayerHeadService(LauncherPaths paths, HttpClient? http = null)
    {
        _directory = Path.Combine(paths.Cache, "player-heads");
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }
    private string CachePath(string name) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("verified-mojang-face-v1:" + name.ToLowerInvariant()))) + ".png");

    public async Task<Bitmap?> GetCachedHeadAsync(string username, CancellationToken token)
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
    public async Task<Bitmap?> GetHeadAsync(string username, CancellationToken token)
    {
        if (!Regex.IsMatch(username, "^[A-Za-z0-9_]{3,16}$")) return null;
        var file = CachePath(username);
        var cached = await GetCachedHeadAsync(username, token);
        if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < CacheLifetime) return cached;
        var retryFile = file + ".retry";
        if (File.Exists(retryFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(retryFile) < TimeSpan.FromHours(6)) return cached;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Bitmap? image = null;
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
            using var skin = Decode(await DownloadAsync(address, 1_000_000, timeout.Token));
            if (skin.PixelSize.Width != 64 || skin.PixelSize.Height is not (32 or 64)) throw new InvalidDataException();
            using var face = RenderFace(skin);
            using var bytes = new MemoryStream(); face.Save(bytes, PngBitmapEncoderOptions.Default);
            var data = bytes.ToArray(); image = Decode(data);
            try
            {
                Directory.CreateDirectory(_directory);
                var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(temporary, data, token); File.Move(temporary, file, true); File.Delete(retryFile); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            cached?.Dispose();
            cached = null;
            var result = image;
            image = null; // Ownership transfers to the caller only on success.
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            try { Directory.CreateDirectory(_directory); await File.WriteAllTextAsync(retryFile, "", token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { }
            token.ThrowIfCancellationRequested();
            return cached;
        }
        finally
        {
            image?.Dispose();
            if (token.IsCancellationRequested) cached?.Dispose();
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
    private static Bitmap Decode(byte[] data)
    {
        if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})
            || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)) > 512
            || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4)) > 512) throw new InvalidDataException();
        using var stream = new MemoryStream(data, false);
        return new Bitmap(stream);
    }
    private static Bitmap RenderFace(Bitmap skin)
    {
        using var pixels = new WriteableBitmap(skin.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var data = new byte[skin.PixelSize.Width * skin.PixelSize.Height * 4];
        using (var frame = pixels.Lock())
        {
            skin.CopyPixels(frame);
            for (var y = 0; y < skin.PixelSize.Height; y++) Marshal.Copy(IntPtr.Add(frame.Address, y * frame.RowBytes), data, y * 256, 256);
        }
        var drawOverlay = skin.PixelSize.Height != 32 || Enumerable.Range(0, 64).Any(i => data[((8 + i / 8) * 64 + 40 + i % 8) * 4 + 3] < 255);
        var face = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
        {
            var origin = ((8 + y) * 64 + 8 + x) * 4;
            var overlay = ((8 + y) * 64 + 40 + x) * 4;
            var target = (y * 8 + x) * 4;
            var alpha = drawOverlay ? data[overlay + 3] : 0;
            for (var channel = 0; channel < 4; channel++) face[target + channel] = (byte)Math.Min(255, (drawOverlay ? data[overlay + channel] : 0) + (data[origin + channel] * (255 - alpha) + 127) / 255);
        }
        var pinned = GCHandle.Alloc(face, GCHandleType.Pinned);
        try { return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pinned.AddrOfPinnedObject(), new PixelSize(8, 8), new Vector(96, 96), 32); }
        finally { pinned.Free(); }
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}

