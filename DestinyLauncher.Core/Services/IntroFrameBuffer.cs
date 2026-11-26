using System.IO.Compression;
using System.Threading.Channels;

namespace DestinyLauncher.Services;

/// <summary>Decodes the original intro off the UI thread with bounded look-ahead.</summary>
internal sealed class IntroFrameBuffer<T> : IDisposable where T : class
{
    internal const int FrameCount = 73;
    internal const int Capacity = 3;
    private readonly Channel<(int Index, T Frame)> _frames = Channel.CreateBounded<(int, T)>(
        new BoundedChannelOptions(Capacity) { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<T>? _release;
    private readonly Task _producer;
    private int _requestedIndex;
    private bool _disposed;
    internal T Current { get; private set; } = null!;

    private IntroFrameBuffer(Func<Stream> open, Func<Stream, T> decode, Action<T>? release)
    {
        _release = release;
        var token = _stop.Token;
        _producer = Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                using var source = open();
                using var archive = new ZipArchive(source, ZipArchiveMode.Read);
                for (var index = 0; index < FrameCount; index++)
                {
                    // Wait before decoding, so even the producer cannot retain a fourth
                    // queued image while the UI is busy preparing the main window.
                    if (!await _frames.Writer.WaitToWriteAsync(token).ConfigureAwait(false)) break;
                    token.ThrowIfCancellationRequested();
                    // Match the playback clock after a slow UI frame instead of
                    // decoding a backlog that can no longer be displayed.
                    index = Math.Max(index, Volatile.Read(ref _requestedIndex));
                    if (index >= FrameCount) break;
                    var item = archive.GetEntry($"{index:0000}.png")
                        ?? throw new InvalidDataException("Intro incompleta.");
                    using var entry = item.Open();
                    using var png = new MemoryStream(checked((int)item.Length));
                    await entry.CopyToAsync(png, token).ConfigureAwait(false);
                    png.Position = 0;
                    var frame = decode(png);
                    if (!_frames.Writer.TryWrite((index, frame))) _release?.Invoke(frame);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { error = ex; }
            finally { _frames.Writer.TryComplete(error); }
        });
    }

    internal static async Task<IntroFrameBuffer<T>> OpenAsync(Func<Stream> open, Func<Stream, T> decode, Action<T>? release = null)
    {
        var buffer = new IntroFrameBuffer<T>(open, decode, release);
        try
        {
            buffer.Current = (await buffer._frames.Reader.ReadAsync().ConfigureAwait(false)).Frame;
            return buffer;
        }
        catch { buffer.Dispose(); throw; }
    }

    internal void Advance(int index, Action<T> present)
    {
        if (_disposed) return;
        Volatile.Write(ref _requestedIndex, index);
        while (_frames.Reader.TryPeek(out var next) && next.Index <= index)
        {
            if (!_frames.Reader.TryRead(out next)) break;
            var previous = Current;
            Current = next.Frame;
            present(Current);
            _release?.Invoke(previous);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        // Complete the writer before draining: a decoder in flight must release its
        // result instead of enqueueing it after the window has closed.
        _frames.Writer.TryComplete();
        while (_frames.Reader.TryRead(out var frame)) _release?.Invoke(frame.Frame);
        if (Current is { } current) _release?.Invoke(current);
        Current = null!;
        _ = _producer.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
