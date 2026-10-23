using System.Text.Json;

namespace DestinyLauncher.Services;

internal sealed record LauncherInstallation(string DataRoot, bool Experimental)
{
    internal const string MarkerName = "launcher-installation.json";
    internal const string AppMutex = @"Local\DestinyDynasty.Launcher";
    internal static LauncherInstallation? Read(string directory)
    {
        try
        {
            var path = Path.Combine(directory, MarkerName);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var value = JsonSerializer.Deserialize<LauncherInstallation>(File.ReadAllText(path));
            if (value is null || !Path.IsPathFullyQualified(value.DataRoot)) return null;
            return value with { DataRoot = Path.GetFullPath(value.DataRoot) };
        }
        catch { return null; }
    }
    internal static string MutexName(string? dataRoot) => dataRoot is null ? AppMutex
        : AppMutex + "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..20];
}
