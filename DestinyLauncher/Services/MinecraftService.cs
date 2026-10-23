using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.FileExtractors;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;
public sealed class MinecraftService
{
    private static readonly HttpClient FabricMetaClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly LauncherPaths _paths; private readonly AppLogger _log;
    public MinecraftService(LauncherPaths paths, AppLogger log) { _paths = paths; _log = log; }
    public async Task<MinecraftGameSession> PrepareAndLaunchAsync(LauncherConfig config, PackDefinition pack, IProgress<(string status, double value)> progress, CancellationToken token)
    {
        var preparation = Stopwatch.StartNew();
        var options = CreateLaunchOptions(config);
        try { options.JavaPath = await new JavaRuntimeService(_paths, _log).EnsureAsync(pack.JavaVersion, progress, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new JavaRuntimeException($"No se pudo preparar Java {pack.JavaVersion}. Comprueba la conexión y el espacio disponible y vuelve a intentarlo.", ex); }
        _log.Info($"Using managed Java {pack.JavaVersion}: {options.JavaPath}");
        var instance = new MinecraftPath(_paths.Instance(pack.Slug));
        progress.Report(("Consultando Fabric estable para Minecraft " + config.MinecraftVersion, .7));
        config.FabricLoaderVersion = await GetLatestStableFabricAsync(config.MinecraftVersion, config.FabricLoaderVersion, token);
        _log.Info($"Using Fabric Loader {config.FabricLoaderVersion} for Minecraft {config.MinecraftVersion}.");
        var parameters = MinecraftLauncherParameters.CreateDefault(instance);
        UseManagedRuntime(parameters);
        // Complete local metadata needs no Mojang version-list download on every launch.
        if (HasLocalProfile(instance, config.MinecraftVersion, null)
            && HasLocalProfile(instance, FabricProfile(config.MinecraftVersion, config.FabricLoaderVersion), config.MinecraftVersion))
            parameters.VersionLoader = new LocalJsonVersionLoader(instance);
        var launcher = new MinecraftLauncher(parameters);
        launcher.ByteProgressChanged += (_, e) => { if (e.TotalBytes > 0) progress.Report(("Preparando Minecraft", .05 + (double)e.ProgressedBytes / e.TotalBytes * .55)); };
        progress.Report(("Instalando Fabric " + config.FabricLoaderVersion, .74));
        using var fabricClient = new HttpClient();
        var fabric = new FabricInstaller(fabricClient);
        var profile = await PrepareFabricAsync(launcher, fabric, instance, config.MinecraftVersion, config.FabricLoaderVersion, progress, token);
        await GameOptionsFile.SetFullscreenAsync(instance.BasePath, config.WindowMode == GameWindowMode.Fullscreen, token);
        var process = await launcher.BuildProcessAsync(profile, options, token);
        JavaArguments.CorrectWindowsQuoting(process.StartInfo, options.ExtraJvmArguments.SelectMany(a => a.Values));
        _log.Info($"Minecraft preparation completed in {preparation.Elapsed.TotalSeconds:F2}s.");
        try
        {
            token.ThrowIfCancellationRequested();
            _log.Info($"Starting Minecraft: {config.PlayerName}, {profile}, {pack.Name}, {config.WindowMode}, {config.GameWidth}x{config.GameHeight}");
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
        ScreenWidth = config.GameWidth,
        ScreenHeight = config.GameHeight,
        FullScreen = config.WindowMode == GameWindowMode.Fullscreen,
        // Explicitly replace CmlLib's extra tuning defaults. Mandatory profile/platform
        // arguments remain intact; empty user text deliberately means no extra JVM flags.
        ExtraJvmArguments = [new MArgument(JavaArguments.Parse(config.JavaArguments))]
    };

    internal static void UseManagedRuntime(MinecraftLauncherParameters parameters)
    {
        // Minecraft 1.20.1 requests Java 17. Our pack requirement replaces that runtime,
        // while leaving all asset, library and native extraction intact.
        var extractors = parameters.FileExtractors ?? throw new InvalidOperationException("No se configuraron los archivos de Minecraft.");
        foreach (var extractor in extractors.Where(e => e is JavaFileExtractor or LegacyJavaFileExtractor).ToArray())
            extractors.Remove(extractor);
    }

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
        var expected = FabricProfile(minecraftVersion, loaderVersion);
        var profile = HasLocalProfile(instance, expected, minecraftVersion) ? expected
            : await InstallFabricAsync(installer, instance, minecraftVersion, loaderVersion, token);
        // FabricInstaller only writes profile JSON. Resolve and install its inherited files
        // (including the loader, intermediary and ASM) before constructing the Java classpath.
        progress.Report(("Preparando bibliotecas de Fabric", .8));
        await launcher.InstallAsync(profile, token);
        return profile;
    }

    private static string FabricProfile(string minecraftVersion, string loaderVersion) => $"fabric-loader-{loaderVersion}-{minecraftVersion}";
    internal static bool HasLocalProfile(MinecraftPath path, string id, string? parent)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path.GetVersionJsonPath(id)));
            var root = json.RootElement;
            return root.GetProperty("id").GetString() == id && root.TryGetProperty("mainClass", out var main) && !string.IsNullOrWhiteSpace(main.GetString())
                && (parent is null || (root.GetProperty("inheritsFrom").GetString() == parent && root.GetProperty("libraries").GetArrayLength() > 0));
        }
        catch { return false; }
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
        var cacheFile = Path.Combine(_paths.Cache, "fabric-stable-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(minecraftVersion))) + ".json");
        try
        {
            if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromHours(24))
            {
                using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(cacheFile, token));
                var cached = saved.RootElement.GetProperty("loader").GetString();
                if (Version.TryParse(cached, out _)) return cached!;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }
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
                {
                    var resolved = version.GetString()!;
                    try { await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(new { loader = resolved }), token); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                    return resolved;
                }
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
