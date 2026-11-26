using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace DestinyLauncher.Linux;

/// <summary>Composites decoded VLC frames into the normal Avalonia scene, so no detached native overlay covers the interface.</summary>
internal sealed class HeroVideoPlayer : IDisposable
{
    private const int FrameWidth = 960, FrameHeight = 540, Pitch = FrameWidth * 4;
    private readonly Image _target;
    private readonly object _sync = new();
    private readonly LibVLC _engine;
    private readonly MediaPlayer _player;
    private readonly MediaPlayer.LibVLCVideoLockCb _lock;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    private readonly WriteableBitmap _bitmap;
    private readonly Action<string> _error;
    private readonly DispatcherTimer _startupTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private IntPtr _nativeBuffer;
    private int _frameQueued;
    private int _hasPresentedFrame;
    private int _lockedFrames, _displayedFrames;
    private readonly string? _diagnosticFile;
    private volatile bool _disposed;

    internal HeroVideoPlayer(Image target, string path, Action<string> error)
    {
        _target = target;
        _error = error;
        _diagnosticFile = Environment.GetEnvironmentVariable("DESTINY_VIDEO_DIAGNOSTIC");
        _startupTimer.Tick += StartupTimedOut;
        if (OperatingSystem.IsWindows())
        {
            var vlcDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC");
            if (File.Exists(Path.Combine(vlcDirectory, "libvlc.dll"))) Core.Initialize(vlcDirectory);
            else Core.Initialize();
        }
        else Core.Initialize();
        _engine = new LibVLC("--no-audio", "--no-video-title-show", "--avcodec-hw=none", "--no-osd", string.IsNullOrWhiteSpace(_diagnosticFile) ? "--quiet" : "--verbose=2");
        if (!string.IsNullOrWhiteSpace(_diagnosticFile))
            try { _engine.SetLogFile(_diagnosticFile + ".vlc.log"); } catch { }
        try
        {
            _player = new MediaPlayer(_engine);
            _nativeBuffer = Marshal.AllocHGlobal(Pitch * FrameHeight);
            _bitmap = new WriteableBitmap(new PixelSize(FrameWidth, FrameHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _lock = (_, planes) => { Interlocked.Increment(ref _lockedFrames); Monitor.Enter(_sync); Marshal.WriteIntPtr(planes, _nativeBuffer); return IntPtr.Zero; };
            _unlock = (_, _, _) => Monitor.Exit(_sync);
            _display = (_, _) =>
            {
                Interlocked.Increment(ref _displayedFrames);
                if (_disposed) return;
                if (Interlocked.Exchange(ref _frameQueued, 1) != 0) return;
                Dispatcher.UIThread.Post(PresentFrame, DispatcherPriority.Render);
            };
            _player.SetVideoFormat("RV32", FrameWidth, FrameHeight, Pitch);
            _player.SetVideoCallbacks(_lock, _unlock, _display);
            _player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() => Fail("LibVLC encontró un error al cargar o decodificar el vídeo de portada."));
            _player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => { if (!_disposed && Volatile.Read(ref _hasPresentedFrame) == 0) Fail("El vídeo de portada terminó sin producir ningún fotograma."); });
            using var media = new Media(_engine, new Uri(path));
            media.AddOption(":input-repeat=65535");
            var started = _player.Play(media);
            Diagnostic($"Play={started}; archivo={path}; existe={File.Exists(path)}");
            if (started) _startupTimer.Start();
            else Fail("LibVLC no pudo iniciar el vídeo de portada.");
        }
        catch
        {
            _disposed = true;
            _startupTimer.Stop();
            _startupTimer.Tick -= StartupTimedOut;
            // Construction can fail after native resources or callbacks are installed.
            _player?.Dispose();
            _engine.Dispose();
            if (_nativeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_nativeBuffer);
            _nativeBuffer = IntPtr.Zero;
            _bitmap?.Dispose();
            throw;
        }
    }

    private void StartupTimedOut(object? sender, EventArgs e)
    {
        _startupTimer.Stop();
        if (!_disposed && Volatile.Read(ref _hasPresentedFrame) == 0)
        {
            Diagnostic($"Tiempo de espera: estado={_player.State}; posición={_player.Time}; frames recibidos={_lockedFrames}; frames mostrados={_displayedFrames}");
            Fail("El vídeo de fondo no produjo fotogramas. Se ha guardado información de diagnóstico.");
        }
    }

    private void Fail(string message)
    {
        if (_disposed) return;
        Dispose();
        _target.IsVisible = false;
        _error(message);
    }

    private void Diagnostic(string message)
    {
        if (string.IsNullOrWhiteSpace(_diagnosticFile)) return;
        try { File.AppendAllText(_diagnosticFile + ".log", $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}"); } catch { }
    }

    private unsafe void PresentFrame()
    {
        try
        {
            if (_disposed) return;
            using var framebuffer = _bitmap.Lock();
            lock (_sync)
            {
                for (var row = 0; row < FrameHeight; row++)
                    // VLC's lock callback excludes writes while copying directly to the
                    // bitmap. No second full-size managed frame or intermediate copy.
                    Buffer.MemoryCopy((byte*)_nativeBuffer + row * Pitch,
                        (byte*)framebuffer.Address + row * framebuffer.RowBytes, framebuffer.RowBytes, Pitch);
            }
            _target.Source = _bitmap;
            _target.IsVisible = true;
            _target.InvalidateVisual();
            Interlocked.Exchange(ref _hasPresentedFrame, 1);
            _startupTimer.Stop();
        }
        finally { Interlocked.Exchange(ref _frameQueued, 0); }
    }

    internal void Pause() { if (!_disposed) { _startupTimer.Stop(); Diagnostic($"Pausa solicitada; estado={_player.State}"); _player.SetPause(true); } }
    internal void Play() { if (!_disposed) { if (Volatile.Read(ref _hasPresentedFrame) == 0) _startupTimer.Start(); Diagnostic($"Reanudación solicitada; estado={_player.State}"); _player.SetPause(false); } }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _startupTimer.Stop();
        _startupTimer.Tick -= StartupTimedOut;
        _player.Stop();
        _player.Dispose();
        _engine.Dispose();
        if (_nativeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_nativeBuffer);
        _nativeBuffer = IntPtr.Zero;
        _target.Source = null;
        _bitmap.Dispose();
    }
}

