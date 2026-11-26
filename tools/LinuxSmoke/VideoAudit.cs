using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DestinyLauncher.Linux;

internal static class VideoAudit
{
    internal static int Run(string output)
    {
        AvaloniaSynchronizationContext.InstallIfNeeded();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var checks = new List<string>();
        var exit = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            var image = new Image();
            var window = new Window { Width = 960, Height = 540, Content = image, ShowActivated = false,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
            HeroVideoPlayer? player = null;
            string? error = null;
            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
                checks.Add(message);
            }
            async Task Until(Func<bool> condition)
            {
                var clock = Stopwatch.StartNew();
                while (!condition())
                {
                    if (error is not null) throw new InvalidOperationException(error);
                    if (clock.Elapsed.TotalSeconds > 12) throw new TimeoutException("No VLC frame was presented");
                    await Task.Delay(30, lifetime.Token);
                }
            }
            try
            {
                window.Show();
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Video", "reino-rayos-v6.mp4");
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    player = new HeroVideoPlayer(image, path, message => error = message);
                    await Until(() => image.Source is WriteableBitmap && image.IsVisible);
                    var bitmap = (WriteableBitmap)image.Source!;
                    Check(bitmap.PixelSize == new PixelSize(960, 540), $"Cycle {cycle + 1}: original frame resolution is preserved");
                    var first = Hash(bitmap);
                    await Task.Delay(350, lifetime.Token);
                    Check(Hash(bitmap) != first, $"Cycle {cycle + 1}: VLC frames advance through the direct native copy");
                    player.Pause();
                    await Task.Delay(250, lifetime.Token);
                    var paused = Hash(bitmap);
                    await Task.Delay(300, lifetime.Token);
                    Check(Hash(bitmap) == paused, $"Cycle {cycle + 1}: pausing retains the exact last frame");
                    player.Play();
                    await Task.Delay(350, lifetime.Token);
                    Check(Hash(bitmap) != paused, $"Cycle {cycle + 1}: playback resumes on the existing bitmap");
                    player.Dispose(); player.Dispose(); player = null;
                    Check(image.Source is null, $"Cycle {cycle + 1}: disposal clears the displayed bitmap and is idempotent");
                    await Task.Delay(100, lifetime.Token); // Drain callbacks queued before disposal.
                }
                player = new HeroVideoPlayer(image, Path.Combine(output, "missing.mp4"), message => error = message);
                await Until(() => error is not null);
                Check(image.Source is null && !image.IsVisible, "A decoder failure releases its bitmap and preserves the poster fallback");
                exit = 0;
                File.WriteAllText(Path.Combine(output, "vlc-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {checks.Count} real LibVLC/Avalonia checks on {RuntimeInformation.OSDescription}.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { player?.Dispose(); window.Close(); lifetime.Cancel(); }
        });
        try { Dispatcher.UIThread.MainLoop(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        return exit;
    }

    private static string Hash(WriteableBitmap bitmap)
    {
        using var frame = bitmap.Lock();
        var pixels = new byte[frame.RowBytes * bitmap.PixelSize.Height];
        Marshal.Copy(frame.Address, pixels, 0, pixels.Length);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }
}
