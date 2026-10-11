namespace DestinyLauncher.Services;
public sealed class AppLogger
{
    private readonly LauncherPaths _paths; private readonly object _gate = new();
    public AppLogger(LauncherPaths paths) { _paths = paths; _paths.Ensure(); }
    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message} :: {ex}");
    private void Write(string level, string message) { lock (_gate) File.AppendAllText(Path.Combine(_paths.Logs, $"launcher-{DateTime.Now:yyyy-MM-dd}.log"), $"[{DateTime.Now:O}] [{level}] {message}{Environment.NewLine}"); }
}
