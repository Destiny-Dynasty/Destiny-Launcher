using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DestinyLauncher;

/// <summary>A short, codec-independent, per-pixel transparent startup animation.</summary>
internal sealed class IntroWindow : Window
{
    internal const int FrameCount = 73;
    internal const double FramesPerSecond = 24;
    private readonly Image _image;
    private BitmapSource[] _frames;
    private readonly Stopwatch _clock = new();
    private readonly TaskCompletionSource<bool> _completion = new();
    private int _frame = -1;
    internal bool IsPlaying { get; private set; }

    internal IntroWindow(BitmapSource[] frames)
    {
        _frames = frames;
        Title = "Destiny · Iniciando";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = Height = Math.Min(360, Math.Min(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height) * .7);
        _image = new Image { Source = frames[0], Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Content = _image;
        ContentRendered += StartPlayback;
        MouseLeftButtonDown += (_, _) => Finish(true);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(true); } };
        Closed += (_, _) => { Finish(false); _image.Source = null; _frames = []; Content = null; };
    }

    internal static Task<BitmapSource[]> LoadFramesAsync() => Task.Run(() =>
    {
        using var stream = typeof(IntroWindow).Assembly.GetManifestResourceStream("DestinyLauncher.Assets.Intro.emblem.zip")
            ?? throw new FileNotFoundException("No se encontró la animación de inicio.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var frames = new BitmapSource[FrameCount];
        for (var i = 0; i < frames.Length; i++)
        {
            using var png = (archive.GetEntry($"{i:0000}.png") ?? throw new InvalidDataException("Intro incompleta.")).Open();
            // Zip entries are not seekable; give WIC a complete seekable source
            // so OnLoad really finishes decoding before crossing UI threads.
            using var buffer = new MemoryStream();
            png.CopyTo(buffer);
            buffer.Position = 0;
            var bitmap = BitmapDecoder.Create(buffer, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            bitmap.Freeze(); // Decode off the UI thread; no open archive/file handles survive.
            frames[i] = bitmap;
        }
        return frames;
    });

    internal Task<bool> PlayAsync()
    {
        Show();
        return _completion.Task;
    }

    private void StartPlayback(object? sender, EventArgs e)
    {
        if (_completion.Task.IsCompleted || IsPlaying) return;
        IsPlaying = true;
        _clock.Start();
        CompositionTarget.Rendering += RenderFrame;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        var elapsed = _clock.Elapsed.TotalSeconds;
        var index = (int)(elapsed * FramesPerSecond);
        if (index >= _frames.Length) { Finish(true); return; }
        if (index == _frame) return;
        _frame = index;
        _image.Source = _frames[index];
    }

    private void Finish(bool proceed)
    {
        CompositionTarget.Rendering -= RenderFrame;
        _clock.Stop();
        IsPlaying = false;
        _completion.TrySetResult(proceed);
    }
}
