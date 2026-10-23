using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace DestinyLauncher.Services;

/// <summary>One native media decoder; no frame timers, image sequences or web runtime.</summary>
internal sealed class BackgroundVideoController : IDisposable
{
    private readonly MediaElement _video;
    private readonly Window _window;
    private readonly LauncherPaths _paths;
    private readonly Func<bool> _allowed;
    private readonly Action<Exception> _reportError;
    private readonly CancellationTokenSource _lifetime = new();
    private Task<string>? _sourceTask;
    private bool _enabled, _opening, _failed, _disposed;

    internal bool Enabled { get => _enabled; set { _enabled = value; Update(); } }
    internal bool IsOpen { get; private set; }
    internal bool IsPlaying { get; private set; }
    internal int CompletedLoops { get; private set; }
    internal Exception? LastError { get; private set; }
    // The isolated decoder audit can supply focus without taking it from the
    // user. Normal launcher sessions always use the window's native IsActive.
    internal Func<bool> HasPlaybackFocus { get; set; }

    internal BackgroundVideoController(MediaElement video, Window window, LauncherPaths paths,
        Func<bool> allowed, Action<Exception> reportError, bool enabled)
    {
        _video = video; _window = window; _paths = paths;
        _allowed = allowed; _reportError = reportError; _enabled = enabled;
        HasPlaybackFocus = () => _window.IsActive;
        _video.MediaOpened += MediaOpened;
        _video.MediaEnded += MediaEnded;
        _video.MediaFailed += MediaFailed;
        _video.IsVisibleChanged += VisibilityChanged;
        _window.Loaded += WindowChanged;
        _window.Activated += WindowChanged;
        _window.Deactivated += WindowChanged;
        _window.StateChanged += WindowChanged;
        _window.IsVisibleChanged += VisibilityChanged;
        SystemParameters.StaticPropertyChanged += PreferenceChanged;
    }

    private bool CanPlay => !_disposed && _enabled && !_failed && _window.IsLoaded
        && HasPlaybackFocus() && _window.IsVisible && _window.WindowState == WindowState.Normal
        && _video.IsVisible && _allowed() && SystemParameters.ClientAreaAnimation;

    internal void Update() => _ = UpdateAsync();

    private async Task UpdateAsync()
    {
        if (_disposed) return;
        if (!CanPlay)
        {
            // Preserve the decoded frame across minimization as well as focus
            // changes. Reopening on restore exposes the decoder's black startup
            // frame. Real hiding, other tabs and game operations still close it.
            var preserveFrame = _enabled && !_failed && _allowed() && SystemParameters.ClientAreaAnimation
                && (_window.WindowState == WindowState.Minimized || (_window.IsVisible && _video.IsVisible));
            if (!preserveFrame) ClosePlayer();
            else if (IsOpen && IsPlaying) { _video.Pause(); IsPlaying = false; }
            return;
        }
        if (IsOpen)
        {
            if (!IsPlaying) { _video.Play(); IsPlaying = true; }
            return;
        }
        if (_opening) return;
        _opening = true;
        try
        {
            // WPF media needs a local file. Extract the embedded clip once, to
            // the launcher's own cache, without blocking the UI or game launch.
            var path = await (_sourceTask ??= Task.Run(() => MaterializeAsync(_lifetime.Token)));
            if (!CanPlay) return;
            IsOpen = true;
            _video.Source = new Uri(path);
            _video.Play(); IsPlaying = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        { Fail(ex); }
        finally { _opening = false; }
    }

    private async Task<string> MaterializeAsync(CancellationToken token)
    {
        await using var source = typeof(BackgroundVideoController).Assembly.GetManifestResourceStream(
            "DestinyLauncher.Assets.Video.reino-rayos-v6.mp4")
            ?? throw new IOException("Missing embedded background video.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token)).ToLowerInvariant();
        source.Position = 0;
        var directory = Path.Combine(_paths.Cache, "background");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"reino-{hash}.mp4");
        if (File.Exists(target) && new FileInfo(target).Length == source.Length) return target;
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
                await source.CopyToAsync(destination, token);
            token.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void MediaOpened(object? sender, RoutedEventArgs e)
    {
        if (_disposed || !IsOpen) return;
        if (!_video.HasVideo) { Fail(new NotSupportedException("Background clip contains no playable video.")); return; }
        _video.Opacity = 1;
        Update();
    }

    private void MediaEnded(object? sender, RoutedEventArgs e)
    {
        if (!CanPlay) { Update(); return; }
        CompletedLoops++;
        _video.Position = TimeSpan.Zero;
        _video.Play();
    }

    private void MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    { if (!_disposed && IsOpen) Fail(e.ErrorException); }

    private void Fail(Exception exception)
    {
        if (_disposed || _failed) return;
        LastError = exception; _failed = true;
        ClosePlayer(); _reportError(exception);
    }

    private void ClosePlayer()
    {
        if (!IsOpen) return;
        IsOpen = false; IsPlaying = false;
        _video.Opacity = 0;
        _video.Close(); _video.Source = null;
    }

    private void WindowChanged(object? sender, EventArgs e) => Update();
    private void VisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e) => Update();
    private void PreferenceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) _window.Dispatcher.InvokeAsync(Update); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        _video.MediaOpened -= MediaOpened; _video.MediaEnded -= MediaEnded; _video.MediaFailed -= MediaFailed;
        _video.IsVisibleChanged -= VisibilityChanged;
        _window.Loaded -= WindowChanged; _window.Activated -= WindowChanged;
        _window.Deactivated -= WindowChanged; _window.StateChanged -= WindowChanged;
        _window.IsVisibleChanged -= VisibilityChanged;
        SystemParameters.StaticPropertyChanged -= PreferenceChanged;
        ClosePlayer();
        // An extraction in flight still owns the cancellation token; the small
        // CTS can be disposed once that task has observed cancellation.
        if (_sourceTask is { IsCompleted: false } sourceTask)
            _ = sourceTask.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
        else _lifetime.Dispose();
    }
}
