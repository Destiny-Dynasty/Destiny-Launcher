using System.Text.Json.Serialization;

namespace DestinyLauncher.Models;

public enum PackEdition { Destiny, DestinyLite, DestinyStaff }
public enum GameWindowMode { Windowed, Maximized, Fullscreen }
public sealed class PackDefinition
{
    public const string DefaultDestinyManifestUrl = "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack/main/manifests/destiny.json";
    public const string DefaultDestinyLiteManifestUrl = "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack/main/manifests/destiny-lite.json";

    public string Name { get; init; } = "Destiny";
    public string Slug { get; init; } = "destiny";
    [JsonIgnore]
    public string ManifestUrl => Slug.ToLowerInvariant() switch
    {
        "destiny" => DefaultDestinyManifestUrl,
        "destiny-lite" => DefaultDestinyLiteManifestUrl,
        "destiny-staff" => "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack/main/manifests/destiny-staff.json",
        _ => throw new InvalidOperationException("Variante de modpack desconocida.")
    };
    public string Version { get; set; } = "0.1.0";
    public int JavaVersion { get; set; } = 21;
}
public sealed class LauncherConfig
{
    public const string DefaultJavaArguments = "-XX:+UseG1GC -XX:MaxGCPauseMillis=200";
    public string PlayerName { get; set; } = "";
    public bool MultiAccountEnabled { get; set; }
    public bool AutoJoinServer { get; set; } = true;
    public GameWindowMode WindowMode { get; set; } = GameWindowMode.Windowed;
    public int GameWidth { get; set; } = 1280;
    public int GameHeight { get; set; } = 720;
    public List<string> Accounts { get; set; } = [];
    public static bool ValidPlayerName(string? name) => System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "^[A-Za-z0-9_]{3,16}$");
    public void NormalizeAccounts()
    {
        Accounts = (Accounts ?? []).Select(n => n?.Trim() ?? "").Where(ValidPlayerName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ValidPlayerName(PlayerName) && !Accounts.Contains(PlayerName, StringComparer.OrdinalIgnoreCase)) Accounts.Add(PlayerName);
        if (MultiAccountEnabled && !ValidPlayerName(PlayerName)) PlayerName = Accounts.FirstOrDefault() ?? "";
    }
    public PackEdition SelectedEdition { get; set; } = PackEdition.Destiny;
    public int RamGb { get; set; } = DestinyLauncher.Services.MemoryPolicy.System.DefaultGb;
    [JsonIgnore]
    public bool RamWasAdjusted { get; set; }
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
    [JsonIgnore]
    public PackDefinition DestinyStaff { get; set; } = new() { Name = "Destiny Staff", Slug = "destiny-staff", Version = "" };
}
public sealed class InstalledPack
{
    public string Version { get; set; } = "";
    public int JavaVersion { get; set; } = 21;
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
}
