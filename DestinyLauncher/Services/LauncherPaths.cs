namespace DestinyLauncher.Services;
public sealed class LauncherPaths
{
    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "launcher.json");
    public string Instances => Path.Combine(Root, "instances");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public string Temp => Path.Combine(Root, "temp");
    public string Instance(string slug) => Path.Combine(Instances, slug);
    public LauncherPaths(string? root = null) => Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DestinyLauncher");
    public void Ensure() { foreach (var p in new[] { Root, Instances, Cache, Logs, Temp }) Directory.CreateDirectory(p); }
}
