using System.Net.Http;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.ModLoaders.FabricMC;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;
public sealed class MinecraftService
{
    private readonly LauncherPaths _paths; private readonly AppLogger _log;
    public MinecraftService(LauncherPaths paths, AppLogger log) { _paths = paths; _log = log; }
    public async Task PrepareAndLaunchAsync(LauncherConfig config, PackDefinition pack, IProgress<(string status, double value)> progress, CancellationToken token)
    {
        var instance = new MinecraftPath(_paths.Instance(pack.Slug)); var launcher = new MinecraftLauncher(instance);
        progress.Report(("Preparando Minecraft 1.20.1", .05)); launcher.ByteProgressChanged += (_, e) => { if (e.TotalBytes > 0) progress.Report(("Preparando Minecraft", .05 + (double)e.ProgressedBytes / e.TotalBytes * .55)); };
        await launcher.InstallAsync(config.MinecraftVersion, token);
        progress.Report(("Preparando Fabric", .7));
        var fabric = new FabricInstaller(new HttpClient());
        var profile = await fabric.Install(config.MinecraftVersion, instance, config.FabricLoaderVersion);
        var options = new MLaunchOption { Session = MSession.CreateOfflineSession(config.PlayerName), MaximumRamMb = config.RamGb * 1024, ServerIp = config.ServerAddress };
        var process = await launcher.BuildProcessAsync(profile, options);
        process.Start(); _log.Info($"Minecraft launched: {config.PlayerName}, {profile}, {pack.Name}"); progress.Report(("Minecraft iniciado", 1));
    }
}
