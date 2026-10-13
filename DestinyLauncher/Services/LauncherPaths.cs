namespace DestinyLauncher.Services;
public sealed class LauncherPaths
{
    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "launcher.json");
    public string Instances => InstallDirectory;
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public string Temp => Path.Combine(Root, "temp");
    public string InstallDirectory { get; private set; } = DefaultInstallDirectory;
    public static string DefaultInstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions", "DestinyDynasty");
    public LauncherPaths(string? root = null) => Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DestinyLauncher");
    public void SetInstallDirectory(string? path) => InstallDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? DefaultInstallDirectory : Environment.ExpandEnvironmentVariables(path.Trim()));
    public string Instance(string slug) => string.Equals(slug, "destiny-lite", StringComparison.OrdinalIgnoreCase) ? InstallDirectory + "-Lite" : InstallDirectory;
    public void Ensure() { foreach (var p in new[] { Root, Cache, Logs, Temp }) Directory.CreateDirectory(p); }
}
