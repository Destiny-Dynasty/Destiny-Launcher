using System.IO.Compression;
using DestinyLauncher.Services;

internal static class IntroBufferAudit
{
    internal static async Task Run()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 0; i < IntroFrameBuffer<Frame>.FrameCount; i++)
                using (var entry = archive.CreateEntry($"{i:0000}.png").Open()) entry.WriteByte((byte)i);
        var bytes = zip.ToArray();
        var created = new List<Frame>();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Frame Decode(Stream stream)
        {
            var frame = new Frame(stream.ReadByte());
            lock (created) { created.Add(frame); if (created.Count == 4) filled.TrySetResult(); }
            return frame;
        }
        var buffer = await IntroFrameBuffer<Frame>.OpenAsync(() => new MemoryStream(bytes), Decode, frame => frame.Dispose());
        try
        {
            await filled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            lock (created) Check(created.Count == 4, "A busy UI retains at most the visible frame plus three queued frames");
            var displayed = new List<int> { buffer.Current.Index };
            for (var index = 1; index < 73; index++)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (buffer.Current.Index < index)
                {
                    buffer.Advance(index, frame => displayed.Add(frame.Index));
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("Intro decoder stalled");
                    await Task.Delay(1);
                }
            }
            Check(displayed.SequenceEqual(Enumerable.Range(0, 73)), "Every original frame is presented in order without changing its data");
        }
        finally { buffer.Dispose(); buffer.Dispose(); }
        lock (created) Check(created.All(frame => frame.Disposals == 1), "Natural completion releases every frame exactly once");

        // Close while the worker is already decoding the next frame.
        using var decoding = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        created.Clear();
        buffer = await IntroFrameBuffer<Frame>.OpenAsync(() => new TrackedStream(bytes, () => closed.TrySetResult()), stream =>
        {
            var frame = new Frame(stream.ReadByte());
            lock (created) created.Add(frame);
            if (frame.Index == 1) { decoding.Set(); resume.Wait(TimeSpan.FromSeconds(5)); }
            return frame;
        }, frame => frame.Dispose());
        try
        {
            Check(decoding.Wait(TimeSpan.FromSeconds(5)), "Cancellation fixture reaches an in-flight decode");
            buffer.Dispose();
        }
        finally { resume.Set(); buffer.Dispose(); }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (created) Check(created.All(frame => frame.Disposals == 1), "Closing during decode releases the visible, queued and in-flight frames");
        try
        {
            using var ignored = await IntroFrameBuffer<Frame>.OpenAsync(() => new MemoryStream(bytes), _ => throw new InvalidDataException("Broken PNG"));
            throw new Exception("A broken first frame was accepted");
        }
        catch (System.Threading.Channels.ChannelClosedException ex) when (ex.InnerException is InvalidDataException)
        { Console.WriteLine("PASS: Decoder failure propagates so startup can keep its existing fallback"); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
    private sealed class Frame(int index) : IDisposable
    {
        internal int Index { get; } = index;
        internal int Disposals;
        public void Dispose() { if (Interlocked.Increment(ref Disposals) != 1) throw new InvalidOperationException("Frame disposed twice"); }
    }
    private sealed class TrackedStream(byte[] bytes, Action closed) : MemoryStream(bytes)
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) closed(); }
    }
}
