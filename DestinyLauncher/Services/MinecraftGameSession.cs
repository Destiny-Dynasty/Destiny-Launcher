using System.Diagnostics;
using System.Text;

namespace DestinyLauncher.Services;

/// <summary>Owns one Java process and a bounded stderr tail for failures before game logs exist.</summary>
public sealed class MinecraftGameSession : IDisposable
{
    internal const int MaxErrorCharacters = 16 * 1024;
    public Process Process { get; }
    private readonly Task<string> _errors;
    private string _errorTail = "";

    private MinecraftGameSession(Process process)
    {
        Process = process;
        _errors = CaptureErrorsAsync(process.StandardError);
    }

    internal static MinecraftGameSession Start(Process process)
    {
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardError = true;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Minecraft no devolvió un proceso al iniciarse.");
            return new MinecraftGameSession(process);
        }
        catch { process.Dispose(); throw; }
    }

    private async Task<string> CaptureErrorsAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[2048];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > MaxErrorCharacters) tail.Remove(0, tail.Length - MaxErrorCharacters);
                Volatile.Write(ref _errorTail, tail.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        return tail.ToString();
    }

    internal async Task<string> ReadErrorsAsync()
    {
        // A child inheriting Java's stderr must not hold up restoration indefinitely.
        try { return await _errors.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { return Volatile.Read(ref _errorTail); }
    }

    public void Dispose() => Process.Dispose(); // Never kills the game.
}
