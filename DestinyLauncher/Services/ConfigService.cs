using System.Text.Json;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;
public sealed class ConfigService
{
    private readonly LauncherPaths _paths; private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public ConfigService(LauncherPaths paths) { _paths = paths; _paths.Ensure(); }
    public LauncherConfig Load()
    {
        try
        {
            var config = File.Exists(_paths.ConfigFile)
                ? JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(_paths.ConfigFile)) ?? new()
                : new();
            config.Destiny ??= new LauncherConfig().Destiny;
            config.DestinyLite ??= new LauncherConfig().DestinyLite;
            if (string.IsNullOrWhiteSpace(config.InstallDirectory)) config.InstallDirectory = LauncherPaths.DefaultInstallDirectory;
            var oldZipPlaceholder = string.Equals(config.Destiny.DownloadUrl, "https://example.com/destiny.zip", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(config.Destiny.DownloadUrl, "https://github.com/destiny/pack/releases/latest/download/destiny.zip", StringComparison.OrdinalIgnoreCase);
            if (oldZipPlaceholder)
            {
                config.Destiny.DownloadUrl = "";
                if (string.IsNullOrWhiteSpace(config.Destiny.ManifestUrl))
                    config.Destiny.ManifestUrl = PackDefinition.DefaultDestinyManifestUrl;
            }
            return config;
        }
        catch { return new(); }
    }
    public void Save(LauncherConfig config) => File.WriteAllText(_paths.ConfigFile, JsonSerializer.Serialize(config, _json));
    public string InstalledVersion(string slug) { var file = Path.Combine(_paths.Instance(slug), "destiny-installed.json"); try { return File.Exists(file) ? JsonSerializer.Deserialize<InstalledPack>(File.ReadAllText(file))?.Version ?? "" : ""; } catch { return ""; } }
    public void SaveInstalled(string slug, string version) { var dir = _paths.Instance(slug); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "destiny-installed.json"), JsonSerializer.Serialize(new InstalledPack { Version = version }, _json)); }
}
