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
            config.JavaArguments ??= LauncherConfig.DefaultJavaArguments;
            config.NormalizeAccounts();
            if (!Enum.IsDefined(config.WindowMode)) config.WindowMode = GameWindowMode.Windowed;
            if (config.GameWidth is < 640 or > 7680) config.GameWidth = 1280;
            if (config.GameHeight is < 360 or > 4320) config.GameHeight = 720;
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

    public void Save(LauncherConfig config)
    {
        config.NormalizeAccounts();
        var temporary = _paths.ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(config, _json)); File.Move(temporary, _paths.ConfigFile, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public string InstalledVersion(string slug) { var file = Path.Combine(_paths.Instance(slug), "destiny-installed.json"); try { return File.Exists(file) ? JsonSerializer.Deserialize<InstalledPack>(File.ReadAllText(file))?.Version ?? "" : ""; } catch { return ""; } }
    public int InstalledJavaVersion(string slug) { try { return System.Text.Json.JsonSerializer.Deserialize<InstalledPack>(File.ReadAllText(Path.Combine(_paths.Instance(slug), "destiny-installed.json")))?.JavaVersion ?? 21; } catch { return 21; } }
    public void SaveInstalled(string slug, string version, int javaVersion = 21) { var dir = _paths.Instance(slug); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "destiny-installed.json"), JsonSerializer.Serialize(new InstalledPack { Version = version, JavaVersion = javaVersion }, _json)); }
}
