using System.Text.Json.Serialization;

namespace DestinyLauncher.Models;

public enum PackEdition { Destiny, DestinyLite }
public sealed class PackDefinition
{
    public const string DefaultDestinyManifestUrl = "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack/main/manifests/destiny.json";
    public const string DefaultDestinyLiteManifestUrl = "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack-Lite/main/manifests/destiny-lite.json";

    public string Name { get; init; } = "Destiny";
    public string Slug { get; init; } = "destiny";
    [JsonIgnore]
    public string ManifestUrl => Slug.Equals("destiny-lite", StringComparison.OrdinalIgnoreCase)
        ? DefaultDestinyLiteManifestUrl
        : DefaultDestinyManifestUrl;
    public string Version { get; set; } = "0.1.0";
}
public sealed class LauncherConfig
{
    public const string DefaultJavaArguments = "-XX:+UseG1GC -XX:MaxGCPauseMillis=200";
    public string PlayerName { get; set; } = "";
    public PackEdition SelectedEdition { get; set; } = PackEdition.Destiny;
    public int RamGb { get; set; } = 8;
    public string JavaArguments { get; set; } = DefaultJavaArguments;
    public string InstallDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions", "DestinyDynasty");
    [JsonIgnore]
    public string MinecraftVersion { get; set; } = "1.20.1";
    [JsonIgnore]
    public string FabricLoaderVersion { get; set; } = "0.19.5";
    [JsonIgnore]
    public PackDefinition Destiny { get; set; } = new() { Version = "1.4.7" };
    [JsonIgnore]
    public PackDefinition DestinyLite { get; set; } = new() { Name = "Destiny Lite", Slug = "destiny-lite", Version = "0.1.0" };
}
public sealed class InstalledPack
{
    public string Version { get; set; } = "";
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
}
