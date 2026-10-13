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
            var savedJson = File.Exists(_paths.ConfigFile) ? File.ReadAllText(_paths.ConfigFile) : null;
            var config = savedJson is not null ? JsonSerializer.Deserialize<LauncherConfig>(savedJson) ?? new() : new();
            config.Destiny ??= new LauncherConfig().Destiny;
            config.DestinyLite ??= new LauncherConfig().DestinyLite;
            if (string.IsNullOrWhiteSpace(config.InstallDirectory)) config.InstallDirectory = LauncherPaths.DefaultInstallDirectory;
            config.RamGb = Math.Clamp(config.RamGb, 1, 1024);
            if (savedJson is not null && HasRemovedSettings(savedJson))
            {
                try { Save(config); } catch { }
            }
            return config;
        }
        catch { return new(); }
    }

    private static bool HasRemovedSettings(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("ServerAddress", out _) || root.TryGetProperty("MinecraftVersion", out _) || root.TryGetProperty("FabricLoaderVersion", out _)) return true;
        foreach (var packName in new[] { "Destiny", "DestinyLite" })
        {
            if (root.TryGetProperty(packName, out _)) return true;
        }
        return false;
    }

    public void Save(LauncherConfig config) => File.WriteAllText(_paths.ConfigFile, JsonSerializer.Serialize(config, _json));
    public string InstalledVersion(string slug) { var file = Path.Combine(_paths.Instance(slug), "destiny-installed.json"); try { return File.Exists(file) ? JsonSerializer.Deserialize<InstalledPack>(File.ReadAllText(file))?.Version ?? "" : ""; } catch { return ""; } }
    public void SaveInstalled(string slug, string version) { var dir = _paths.Instance(slug); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "destiny-installed.json"), JsonSerializer.Serialize(new InstalledPack { Version = version }, _json)); }
}
