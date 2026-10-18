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
    public async Task<MinecraftGameSession> PrepareAndLaunchAsync(LauncherConfig config, PackDefinition pack, IProgress<(string status, double value)> progress, CancellationToken token)
    {
        var options = CreateLaunchOptions(config);
        var instance = new MinecraftPath(_paths.Instance(pack.Slug)); var launcher = new MinecraftLauncher(instance);
        progress.Report(("Preparando Minecraft 1.20.1", .05)); launcher.ByteProgressChanged += (_, e) => { if (e.TotalBytes > 0) progress.Report(("Preparando Minecraft", .05 + (double)e.ProgressedBytes / e.TotalBytes * .55)); };
        await launcher.InstallAsync(config.MinecraftVersion, token);
        progress.Report(("Consultando Fabric estable para Minecraft " + config.MinecraftVersion, .7));
        config.FabricLoaderVersion = await GetLatestStableFabricAsync(config.MinecraftVersion, config.FabricLoaderVersion, token);
        _log.Info($"Using Fabric Loader {config.FabricLoaderVersion} for Minecraft {config.MinecraftVersion}.");
        progress.Report(("Instalando Fabric " + config.FabricLoaderVersion, .74));
        using var fabricClient = new HttpClient();
        var fabric = new FabricInstaller(fabricClient);
        var profile = await PrepareFabricAsync(launcher, fabric, instance, config.MinecraftVersion, config.FabricLoaderVersion, progress, token);
        var process = await launcher.BuildProcessAsync(profile, options, token);
        JavaArguments.CorrectWindowsQuoting(process.StartInfo, options.ExtraJvmArguments.SelectMany(a => a.Values));
        try
        {
            token.ThrowIfCancellationRequested();
            _log.Info($"Starting Minecraft: {config.PlayerName}, {profile}, {pack.Name}");
            var session = MinecraftGameSession.Start(process);
            return session;
        }
        catch { process.Dispose(); throw; }
    }

    internal static MLaunchOption CreateLaunchOptions(LauncherConfig config) => new()
    {
        Session = MSession.CreateOfflineSession(config.PlayerName),
        MaximumRamMb = checked(config.RamGb * 1024),
        ServerIp = config.AutoJoinServer ? "destinydynasty.tatnikhost.net" : null,
        // Explicitly replace CmlLib's extra tuning defaults. Mandatory profile/platform
        // arguments remain intact; empty user text deliberately means no extra JVM flags.
        ExtraJvmArguments = [new MArgument(JavaArguments.Parse(config.JavaArguments))]
    };

    internal static async Task InitializeNewInstanceAsync(string instance, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var options = Path.Combine(instance, "options.txt");
        // Never reset preferences in existing instances, including retries and updates.
        if (File.Exists(options) || File.Exists(Path.Combine(instance, "destiny-installed.json"))) return;
        Directory.CreateDirectory(instance);
        var temporary = Path.Combine(instance, $".destiny-options-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, "lang:es_es\n", new System.Text.UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, options, overwrite: false); }
            catch (IOException) when (File.Exists(options)) { } // Preserve an options file created meanwhile.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<string> PrepareFabricAsync(MinecraftLauncher launcher, FabricInstaller installer, MinecraftPath instance,
        string minecraftVersion, string loaderVersion, IProgress<(string status, double value)> progress, CancellationToken token)
    {
        var profile = await InstallFabricAsync(installer, instance, minecraftVersion, loaderVersion, token);
        // FabricInstaller only writes profile JSON. Resolve and install its inherited files
        // (including the loader, intermediary and ASM) before constructing the Java classpath.
        progress.Report(("Preparando bibliotecas de Fabric", .8));
        await launcher.InstallAsync(profile, token);
        return profile;
    }

    internal static async Task<string> InstallFabricAsync(FabricInstaller installer, MinecraftPath instance, string minecraftVersion, string loaderVersion, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The (gameVersion, path, string) overload means a custom PROFILE NAME, not a loader
        // version; it silently selects the latest loader instead of the requested stable one.
        var profile = await installer.Install(minecraftVersion, loaderVersion, instance);
        token.ThrowIfCancellationRequested();
        return profile;
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
