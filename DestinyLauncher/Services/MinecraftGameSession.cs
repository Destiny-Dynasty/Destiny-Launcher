using System.Diagnostics;
using System.Text;

namespace DestinyLauncher.Services;

/// <summary>Owns one Java process and a bounded stderr tail for failures before game logs exist.</summary>
public sealed class MinecraftGameSession : IDisposable
{
    internal const int MaxErrorCharacters = 16 * 1024;
    public Process Process { get; }
    private readonly Task _errors;
    private readonly StreamReader _errorReader;
    private readonly GameProfileSession? _profileSession;
    private readonly StringBuilder _errorTail = new(MaxErrorCharacters + 2048);
    private readonly object _errorLock = new();

    private MinecraftGameSession(Process process, GameProfileSession? profileSession)
    {
        Process = process;
        _profileSession = profileSession;
        _errorReader = process.StandardError;
        _errors = CaptureErrorsAsync(_errorReader);
    }

    internal static MinecraftGameSession Start(Process process, GameProfileSession? profileSession = null)
    {
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardError = true;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Minecraft no devolvió un proceso al iniciarse.");
            return new MinecraftGameSession(process, profileSession);
        }
        catch { process.Dispose(); throw; }
    }

    private async Task CaptureErrorsAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (_errorLock)
                {
                    _errorTail.Append(buffer, 0, read);
                    if (_errorTail.Length > MaxErrorCharacters) _errorTail.Remove(0, _errorTail.Length - MaxErrorCharacters);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    internal async Task<string> ReadErrorsAsync()
    {
        // A child inheriting Java's stderr must not hold up restoration indefinitely.
        try { await _errors.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        lock (_errorLock) return _errorTail.ToString();
    }

    internal Task CompleteAsync() => _profileSession?.CompleteAsync() ?? Task.CompletedTask;

    public void Dispose()
    {
        // Close our pipe as well as the process handle, even if a child inherited stderr.
        // Disposing a session never kills the game.
        try { _errorReader.Dispose(); }
        finally { Process.Dispose(); }
    }
}
