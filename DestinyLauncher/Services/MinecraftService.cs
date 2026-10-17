using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.ModLoaders.FabricMC;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;
public sealed class MinecraftService
{
    private static readonly HttpClient FabricMetaClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly LauncherPaths _paths; private readonly AppLogger _log;
    public MinecraftService(LauncherPaths paths, AppLogger log) { _paths = paths; _log = log; }
    public async Task<Process> PrepareAndLaunchAsync(LauncherConfig config, PackDefinition pack, IProgress<(string status, double value)> progress, CancellationToken token)
    {
        var instance = new MinecraftPath(_paths.Instance(pack.Slug)); var launcher = new MinecraftLauncher(instance);
        progress.Report(("Preparando Minecraft 1.20.1", .05)); launcher.ByteProgressChanged += (_, e) => { if (e.TotalBytes > 0) progress.Report(("Preparando Minecraft", .05 + (double)e.ProgressedBytes / e.TotalBytes * .55)); };
        await launcher.InstallAsync(config.MinecraftVersion, token);
        progress.Report(("Consultando Fabric estable para Minecraft " + config.MinecraftVersion, .7));
        config.FabricLoaderVersion = await GetLatestStableFabricAsync(config.MinecraftVersion, config.FabricLoaderVersion, token);
        _log.Info($"Using Fabric Loader {config.FabricLoaderVersion} for Minecraft {config.MinecraftVersion}.");
        progress.Report(("Instalando Fabric " + config.FabricLoaderVersion, .74));
        using var fabricClient = new HttpClient();
        var fabric = new FabricInstaller(fabricClient);
        var profile = await fabric.Install(config.MinecraftVersion, instance, config.FabricLoaderVersion);
        var options = new MLaunchOption { Session = MSession.CreateOfflineSession(config.PlayerName), MaximumRamMb = checked(config.RamGb * 1024), ServerIp = "destinydynasty.tatnikhost.net" };
        var process = await launcher.BuildProcessAsync(profile, options, token);
        try
        {
            token.ThrowIfCancellationRequested();
            // Keep the actual Java process handle. Do not capture its output or poll all Java
            // processes: the launcher only needs the exit notification for this game session.
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            if (!process.Start()) throw new InvalidOperationException("Minecraft no devolvió un proceso al iniciarse.");
        }
        catch { process.Dispose(); throw; }
        _log.Info($"Minecraft launched: PID {process.Id}, {config.PlayerName}, {profile}, {pack.Name}");
        return process;
    }

    private async Task<string> GetLatestStableFabricAsync(string minecraftVersion, string fallback, CancellationToken token)
    {
        try
        {
            using var response = await FabricMetaClient.GetAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}", token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("loader", out var loader)
                    && loader.TryGetProperty("stable", out var stable)
                    && stable.ValueKind == JsonValueKind.True
                    && loader.TryGetProperty("version", out var version)
                    && version.ValueKind == JsonValueKind.String)
                    return version.GetString()!;
            }
            throw new InvalidDataException("Fabric Meta no devolvió una versión estable.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Info($"Fabric Meta could not be reached; using manifest/config version {fallback}. {ex.Message}");
            return fallback;
        }
    }
}
