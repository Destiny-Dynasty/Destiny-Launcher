namespace DestinyLauncher.Models;

public enum PackEdition { Destiny, DestinyLite }
public sealed class PackDefinition
{
    public const string DefaultDestinyManifestUrl = "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack/main/manifests/destiny.json";

    public string Name { get; init; } = "Destiny";
    public string Slug { get; init; } = "destiny";
    public string DownloadUrl { get; set; } = "";
    public string ManifestUrl { get; set; } = DefaultDestinyManifestUrl;
    public string Version { get; set; } = "0.1.0";
}
public sealed class LauncherConfig
{
    public string PlayerName { get; set; } = "";
    public PackEdition SelectedEdition { get; set; } = PackEdition.Destiny;
    public int RamGb { get; set; } = 8;
    public string InstallDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions", "DestinyDynasty");
    public string MinecraftVersion { get; set; } = "1.20.1";
    public string FabricLoaderVersion { get; set; } = "0.19.5";
    public string ServerAddress { get; set; } = "play.destiny.example";
    public PackDefinition Destiny { get; set; } = new() { Version = "1.4.7" };
    public PackDefinition DestinyLite { get; set; } = new() { Name = "Destiny Lite", Slug = "destiny-lite", DownloadUrl = "", ManifestUrl = "", Version = "0.1.0" };
}
public sealed class InstalledPack
{
    public string Version { get; set; } = "";
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
}
