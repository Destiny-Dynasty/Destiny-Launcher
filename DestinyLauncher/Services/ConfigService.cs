using System.Text.Json;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;
public sealed class ConfigService
{
    private readonly LauncherPaths _paths; private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public ConfigService(LauncherPaths paths) { _paths = paths; _paths.Ensure(); }
    public LauncherConfig Load() { try { return File.Exists(_paths.ConfigFile) ? JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(_paths.ConfigFile)) ?? new() : new(); } catch { return new(); } }
    public void Save(LauncherConfig config) => File.WriteAllText(_paths.ConfigFile, JsonSerializer.Serialize(config, _json));
    public string InstalledVersion(string slug) { var file = Path.Combine(_paths.Instance(slug), "destiny-installed.json"); try { return File.Exists(file) ? JsonSerializer.Deserialize<InstalledPack>(File.ReadAllText(file))?.Version ?? "" : ""; } catch { return ""; } }
    public void SaveInstalled(string slug, string version) { var dir = _paths.Instance(slug); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "destiny-installed.json"), JsonSerializer.Serialize(new InstalledPack { Version = version }, _json)); }
}
