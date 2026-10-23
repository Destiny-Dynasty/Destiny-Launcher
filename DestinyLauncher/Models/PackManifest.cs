namespace DestinyLauncher.Models;

public sealed class PackManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string PackId { get; set; } = "";
    public string Version { get; set; } = "";
    public string MinecraftVersion { get; set; } = "1.20.1";
    public string FabricLoaderVersion { get; set; } = "0.16.9";
    public int JavaVersion { get; set; } = 21;
    public List<PackManifestFile> Files { get; set; } = [];
}

public sealed class PackManifestFile
{
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class ManagedPackState
{
    public string PackId { get; set; } = "";
    public string Version { get; set; } = "";
    public List<ManagedPackFile> Files { get; set; } = [];
    public List<string> Overrides { get; set; } = [];
}

public sealed class ManagedPackFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed record PackUpdateCheck(PackManifest Manifest, IReadOnlyList<PackManifestFile> ChangedFiles, IReadOnlyList<string> RemovedFiles, IReadOnlyList<string> PreservedOverrides)
{
    public int TotalFiles => Manifest.Files.Count;
    public bool HasChanges => ChangedFiles.Count > 0 || RemovedFiles.Count > 0;
}
