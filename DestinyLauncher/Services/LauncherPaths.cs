namespace DestinyLauncher.Services;
public sealed class LauncherPaths
{
    public string Root { get; }
    public string ConfigFile { get; }
    public string Instances => InstallDirectory;
    public string Cache { get; }
    public string Logs { get; }
    public string Temp => Path.Combine(Root, "temp");
    public string InstallDirectory { get; private set; } = DefaultInstallDirectory;
    /// <summary>The one game directory shared by every launcher edition.</summary>
    public string GameDirectory => InstallDirectory;
    public static string DefaultInstallDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions", "DestinyDynasty")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft", "versions", "DestinyDynasty");
    public LauncherPaths(string? root = null)
    {
        var explicitRoot = root is not null;
        Root = Path.GetFullPath(root ?? DefaultDataRoot());
        ConfigFile = explicitRoot || OperatingSystem.IsWindows()
            ? Path.Combine(Root, "launcher.json")
            : Path.Combine(XdgDirectory("XDG_CONFIG_HOME", ".config"), "DestinyLauncher", "launcher.json");
        Cache = explicitRoot || OperatingSystem.IsWindows()
            ? Path.Combine(Root, "cache")
            : Path.Combine(XdgDirectory("XDG_CACHE_HOME", ".cache"), "DestinyLauncher");
        Logs = explicitRoot || OperatingSystem.IsWindows()
            ? Path.Combine(Root, "logs")
            : Path.Combine(XdgDirectory("XDG_STATE_HOME", Path.Combine(".local", "state")), "DestinyLauncher", "logs");
        if (root is not null) InstallDirectory = Path.Combine(Root, "instances", "Destiny");
    }
    public void SetInstallDirectory(string? path) => InstallDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? DefaultInstallDirectory : ExpandUserPath(path.Trim()));
    public string Instance(string slug) => slug.ToLowerInvariant() switch
    {
        "destiny" => InstallDirectory,
        "destiny-lite" => InstallDirectory + "-Lite",
        "destiny-staff" => InstallDirectory + "-Staff",
        _ => throw new InvalidOperationException("Variante de modpack desconocida.")
    };
    public string ProfileConfigDirectory(string slug) => NormalizeSlug(slug) == "destiny"
        ? Path.Combine(GameDirectory, ".destiny-profile-data", "destiny")
        : Instance(slug);
    public string LegacyInstance(string slug) => Instance(slug);
    public static bool IsProfileConfigPath(string relative)
    {
        var root = relative.Replace('\\', '/').Split('/', 2)[0];
        return root.Equals("config", StringComparison.OrdinalIgnoreCase)
            || root.Equals("defaultconfigs", StringComparison.OrdinalIgnoreCase)
            || root.Equals("kubejs", StringComparison.OrdinalIgnoreCase)
            || root.Equals("scripts", StringComparison.OrdinalIgnoreCase);
    }
    private static string NormalizeSlug(string slug) => slug.ToLowerInvariant() switch
    {
        "destiny" or "destiny-lite" or "destiny-staff" => slug.ToLowerInvariant(),
        _ => throw new InvalidOperationException("Variante de modpack desconocida.")
    };
    // Keep the persisted full path compatible with existing installations. Only its parent
    // is editable in Settings; the existing instance name is preserved, including custom names.
    public static string InstallationFolderName(string instanceDirectory) => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExpandUserPath(instanceDirectory))));
    public static string InstallationBaseDirectory(string instanceDirectory)
    {
        var fullPath = Path.GetFullPath(ExpandUserPath(instanceDirectory));
        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullPath)) ?? fullPath;
    }
    public void Ensure() { foreach (var p in new[] { Root, Path.GetDirectoryName(ConfigFile)!, Cache, Logs, Temp }) Directory.CreateDirectory(p); }

    private static string DefaultDataRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DestinyLauncher")
        : Path.Combine(XdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share")), "DestinyLauncher");

    private static string XdgDirectory(string variable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        var expanded = string.IsNullOrWhiteSpace(configured) ? null : ExpandUserPath(configured);
        return Path.GetFullPath(expanded is not null && Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback));
    }

    private static string ExpandUserPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (expanded.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        return expanded;
    }
}
