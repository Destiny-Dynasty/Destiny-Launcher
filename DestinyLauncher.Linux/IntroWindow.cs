using System.Diagnostics;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DestinyLauncher.Services;

namespace DestinyLauncher.Linux;

/// <summary>The original Windows startup animation, rendered without a video codec.</summary>
internal sealed class IntroWindow : Window
{
    private const int FrameCount = 73;
    private const double FramesPerSecond = 24;
    private readonly IntroFrameBuffer<Bitmap> _frames;
    private readonly Image _image;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 24) };
    private readonly Stopwatch _clock = new();
    private readonly Action _showLauncher;
    private bool _finished;

    internal IntroWindow(Action showLauncher)
    {
        WaylandProperties.SetAppId(this, "destiny-launcher");
        _showLauncher = showLauncher;
        Title = "Destiny · Iniciando";
        Width = Height = 360;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        _frames = IntroFrameBuffer<Bitmap>.OpenAsync(
            () => typeof(IntroWindow).Assembly.GetManifestResourceStream("DestinyLauncher.Intro.emblem.zip")
                ?? throw new FileNotFoundException("No se encontró la animación de inicio."),
            stream => new Bitmap(stream), frame => frame.Dispose()).GetAwaiter().GetResult();
        _image = new Image { Source = _frames.Current, Stretch = Stretch.Uniform };
        Content = _image;
        Opened += (_, _) => { _clock.Start(); _timer.Start(); };
        _timer.Tick += (_, _) =>
        {
            var index = (int)(_clock.Elapsed.TotalSeconds * FramesPerSecond);
            if (index >= FrameCount) Finish();
            else _frames.Advance(index, frame => _image.Source = frame);
        };
        PointerPressed += (_, _) => Finish();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(); } };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _image.Source = null;
            _frames.Dispose();
        };
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        _showLauncher();
        Close();
    }
}
