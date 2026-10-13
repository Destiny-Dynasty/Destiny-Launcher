using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace DestinyLauncher.Services;

public sealed class PlayerHeadService : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private const int MaxImageBytes = 1_000_000;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly LauncherPaths _paths;

    public PlayerHeadService(LauncherPaths paths) => _paths = paths;

    public async Task<BitmapImage?> GetHeadAsync(string username, CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.Combine(_paths.Cache, "player-heads");
        Directory.CreateDirectory(cacheDirectory);
        // Include the render type so a previously cached 3D image cannot mask the /head/ endpoint.
        var cacheIdentity = $"mc-heads/head/{username.ToLowerInvariant()}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity)));
        var cachePath = Path.Combine(cacheDirectory, $"{key}.png");
        byte[]? cached = null;

        if (File.Exists(cachePath))
        {
            try
            {
                cached = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                if (cached.Length > 0 && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheLifetime)
                    return Decode(cached);
            }
            catch (OperationCanceledException) { throw; }
            catch { cached = null; }
        }

        try
        {
            var url = $"https://mc-heads.net/head/{Uri.EscapeDataString(username)}/100.png";
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxImageBytes)
                throw new InvalidDataException("La imagen de la skin supera el tamaño permitido.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(chunk, cancellationToken);
                if (read == 0) break;
                if (buffer.Length + read > MaxImageBytes)
                    throw new InvalidDataException("La imagen de la skin supera el tamaño permitido.");
                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            var image = Decode(bytes);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
            return image;
        }
        catch (OperationCanceledException) { throw; }
        catch when (cached is { Length: > 0 })
        {
            return Decode(cached);
        }
    }

    private static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 100;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose() => _http.Dispose();
}
