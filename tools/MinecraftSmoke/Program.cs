using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Installers;
using CmlLib.Core.Version;
using CmlLib.Core.VersionLoader;
using System.IO.Compression;
using DestinyLauncher.Services;
using DestinyLauncher.Models;

if (args.FirstOrDefault() == "--error-fixture")
{
    Console.Error.Write(new string('A', 200_000));
    Console.Error.WriteLine("\nError: Could not find or load main class net.fabricmc.loader.impl.launch.knot.KnotClient");
    return 1;
}

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "Destiny-minecraft-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var checks = new List<string>();
void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks.Add(message);
}

var defaults = new LauncherConfig { PlayerName = "Fixture", RamGb = 1 };
var launchOptions = MinecraftService.CreateLaunchOptions(defaults);
Assert(launchOptions.ExtraJvmArguments.SelectMany(a => a.Values).SequenceEqual(new[] { "-XX:+UseG1GC", "-XX:MaxGCPauseMillis=200" }),
    "Launcher G1 defaults replace the library's experimental tuning flags");
Assert(launchOptions.MaximumRamMb == 1024, "RAM setting still controls the normal maximum heap");
Assert(launchOptions.ServerIp == "destinydynasty.tatnikhost.net", "Automatic server entry stays enabled by default for compatibility");
var manualLaunch = MinecraftService.CreateLaunchOptions(new LauncherConfig { PlayerName = "Fixture", AutoJoinServer = false });
Assert(string.IsNullOrEmpty(manualLaunch.ServerIp), "Disabling automatic entry omits the server from launch options");

var freshInstance = Path.Combine(root, "spanish-first-install", Guid.NewGuid().ToString("N"));
await MinecraftService.InitializeNewInstanceAsync(freshInstance, CancellationToken.None);
var spanishOptions = Path.Combine(freshInstance, "options.txt");
Assert(await File.ReadAllTextAsync(spanishOptions) == "lang:es_es\n", "First installation initializes vanilla language to Spanish (Spain)");
Assert(!File.ReadAllBytes(spanishOptions).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "Minecraft options use UTF-8 without a BOM");
await File.WriteAllTextAsync(spanishOptions, "lang:fr_fr\ngamma:0.7\n");
await MinecraftService.InitializeNewInstanceAsync(freshInstance, CancellationToken.None);
Assert(await File.ReadAllTextAsync(spanishOptions) == "lang:fr_fr\ngamma:0.7\n", "Installation retries preserve a player's changed language and other settings");
var installedInstance = Path.Combine(root, "spanish-existing-install", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(installedInstance);
await File.WriteAllTextAsync(Path.Combine(installedInstance, "destiny-installed.json"), "{}");
await MinecraftService.InitializeNewInstanceAsync(installedInstance, CancellationToken.None);
Assert(!File.Exists(Path.Combine(installedInstance, "options.txt")), "Existing installations never gain an options file from the new default");
var liteInstance = freshInstance + "-Lite";
await MinecraftService.InitializeNewInstanceAsync(liteInstance, CancellationToken.None);
Assert(await File.ReadAllTextAsync(Path.Combine(liteInstance, "options.txt")) == "lang:es_es\n"
    && (await File.ReadAllTextAsync(spanishOptions)).Contains("lang:fr_fr"), "Lite initializes its own language without changing Destiny preferences");
using (var cancelledDefaults = new CancellationTokenSource())
{
    cancelledDefaults.Cancel();
    var cancelledInstance = Path.Combine(root, "spanish-cancelled", Guid.NewGuid().ToString("N"));
    try { await MinecraftService.InitializeNewInstanceAsync(cancelledInstance, cancelledDefaults.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Assert(!Directory.Exists(cancelledInstance), "Canceled installation defaults do not create instance data"); }
}
var quotedFlags = """-Ddestiny.path="C:\Game Folder\\" -Ddestiny.quote="a\"b" -Xmx128m""";
var parsedFlags = JavaArguments.Parse(quotedFlags);
Assert(parsedFlags.SequenceEqual(new[] { "-Ddestiny.path=C:\\Game Folder\\", "-Ddestiny.quote=a\"b", "-Xmx128m" }),
    "Custom JVM parsing preserves Windows paths, spaces, trailing backslashes and escaped quotes");
Assert(JavaArguments.Parse(" \r\n ").Length == 0, "An intentionally empty JVM field adds no flags");
try { JavaArguments.Parse("-Dtest=\"unfinished"); throw new Exception("Unclosed quote accepted"); }
catch (FormatException) { checks.Add("Unclosed JVM quotes are rejected before launch"); }
var configPaths = new LauncherPaths(Path.Combine(root, "config-fixture"));
var configStore = new ConfigService(configPaths);
var savedLocation = Path.Combine(root, "Existing Custom Instance");
File.WriteAllText(configPaths.ConfigFile, JsonSerializer.Serialize(new { PlayerName = "Fixture", RamGb = 4, InstallDirectory = savedLocation }));
var legacyConfig = configStore.Load();
Assert(legacyConfig.InstallDirectory == savedLocation && legacyConfig.JavaArguments == LauncherConfig.DefaultJavaArguments && legacyConfig.AutoJoinServer,
    "Old configuration gains default JVM flags without moving or renaming its installation");
legacyConfig.JavaArguments = quotedFlags;
legacyConfig.AutoJoinServer = false;
configStore.Save(legacyConfig);
Assert(configStore.Load().JavaArguments == quotedFlags && !configStore.Load().AutoJoinServer, "Custom flags and disabled server entry round-trip through persisted settings");
legacyConfig.JavaArguments = "";
configStore.Save(legacyConfig);
Assert(configStore.Load().JavaArguments == "", "Empty saved JVM flags remain intentionally empty");
Assert(LauncherPaths.InstallationBaseDirectory(savedLocation) == root && LauncherPaths.InstallationFolderName(savedLocation) == "Existing Custom Instance",
    "Splitting the editable base preserves custom instance folder names");

var requests = new List<string>();
var profileJson = """
{"id":"fabric-loader-0.19.5-1.20.1","inheritsFrom":"1.20.1","type":"release","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.19.5","url":"https://maven.fabricmc.net/"}]}
""";
using var http = new HttpClient(new ProfileHandler(requests, profileJson));
var installer = new FabricInstaller(http, "https://fabric.invalid");
var path = new MinecraftPath(Path.Combine(root, "instance"));
var profile = await MinecraftService.InstallFabricAsync(installer, path, "1.20.1", "0.19.5", CancellationToken.None);
Assert(profile == "fabric-loader-0.19.5-1.20.1", "Fabric profile name includes the loader and Minecraft versions");
Assert(requests.SequenceEqual(["/v2/versions/loader/1.20.1/0.19.5/profile/json"]), "Installer requests the specified loader, without silently selecting a newer one");
Assert(File.Exists(path.GetVersionJsonPath(profile)), "Fabric profile JSON is installed in the isolated instance");
Assert(!Directory.Exists(Path.Combine(path.BasePath, "libraries")), "Regression evidence: FabricInstaller alone does not install any loader libraries");
using var canceled = new CancellationTokenSource();
canceled.Cancel();
try { await MinecraftService.InstallFabricAsync(installer, path, "1.20.1", "0.19.5", canceled.Token); throw new Exception("Cancellation ignored"); }
catch (OperationCanceledException) { Assert(requests.Count == 1, "Canceled preparation does not request or write another Fabric profile"); }

Directory.CreateDirectory(Path.GetDirectoryName(path.GetVersionJsonPath("1.20.1"))!);
await File.WriteAllTextAsync(path.GetVersionJsonPath("1.20.1"), """
{"id":"1.20.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}
""");
MinecraftLauncher LibraryTestLauncher(MinecraftPath isolatedPath, HttpClient client)
{
    var parameters = MinecraftLauncherParameters.CreateDefault(isolatedPath, client);
    parameters.VersionLoader = new LocalJsonVersionLoader(isolatedPath);
    parameters.GameInstaller = new BasicGameInstaller(client);
    parameters.FileExtractors = new FileExtractorCollection();
    parameters.FileExtractors.Add(new LibraryFileExtractor(JsonVersionParserOptions.ClientSide, parameters.RulesEvaluator!));
    return new MinecraftLauncher(parameters);
}
await MinecraftService.PrepareFabricAsync(LibraryTestLauncher(path, http), installer, path, "1.20.1", "0.19.5", new Progress<(string, double)>(), CancellationToken.None);
var loaderJar = Path.Combine(path.Library, "net", "fabricmc", "fabric-loader", "0.19.5", "fabric-loader-0.19.5.jar");
Assert(File.Exists(loaderJar) && await File.ReadAllTextAsync(loaderJar) == "fixture-library", "Production Fabric preparation downloads the missing loader jar after writing the profile");
Assert(requests.Contains("/net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar"), "Dependency download uses the selected loader's Maven coordinates");

if (args.Contains("--live-fabric"))
{
    // Only Fabric libraries are fetched into a separate fixture. No assets, modpack, runtime,
    // game launch, user installation writes or downloads of the whole Minecraft client.
    using var liveHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    var livePath = new MinecraftPath(Path.Combine(root, "live-fabric"));
    Directory.CreateDirectory(Path.GetDirectoryName(livePath.GetVersionJsonPath("1.20.1"))!);
    File.Copy(path.GetVersionJsonPath("1.20.1"), livePath.GetVersionJsonPath("1.20.1"), overwrite: true);
    var liveProfile = await MinecraftService.PrepareFabricAsync(LibraryTestLauncher(livePath, liveHttp), new FabricInstaller(liveHttp), livePath,
        "1.20.1", "0.19.5", new Progress<(string, double)>(), CancellationToken.None);
    var liveJar = Path.Combine(livePath.Library, "net", "fabricmc", "fabric-loader", "0.19.5", "fabric-loader-0.19.5.jar");
    using var jar = ZipFile.OpenRead(liveJar);
    Assert(jar.GetEntry("net/fabricmc/loader/impl/launch/knot/KnotClient.class") is not null,
        "Live preparation installs the real Fabric jar containing the previously missing KnotClient class");
    var libs = Directory.GetFiles(livePath.Library, "*.jar", SearchOption.AllDirectories);
    Assert(liveProfile == "fabric-loader-0.19.5-1.20.1" && libs.Length >= 8,
        "Live preparation resolves loader, intermediary, mixin and ASM dependencies in isolation");
}

// Reproduce the exact observed early failure without touching the real installation:
// Java cannot reach KnotClient when the loader jar is absent, and no game log is created.
var java = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions", "DestinyDynasty", "runtime", "windows-x64", "java-runtime-gamma", "bin", "java.exe");
if (File.Exists(java))
{
    defaults.JavaArguments = LauncherConfig.DefaultJavaArguments + " " + quotedFlags + " -Xms16m -XshowSettings:properties -version";
    var javaOptions = MinecraftService.CreateLaunchOptions(defaults);
    javaOptions.JavaPath = java;
    using (var versionProcess = await LibraryTestLauncher(path, http).BuildProcessAsync(profile, javaOptions))
    {
        Assert(versionProcess.StartInfo.Arguments.Contains("destinydynasty.tatnikhost.net"), "Enabled automatic entry reaches the actual game command line");
        JavaArguments.CorrectWindowsQuoting(versionProcess.StartInfo, javaOptions.ExtraJvmArguments.SelectMany(a => a.Values));
        Assert(!versionProcess.StartInfo.Arguments.Contains("-Xmx1024m"), "An explicit custom heap flag does not produce a conflicting duplicate");
        using var versionSession = MinecraftGameSession.Start(versionProcess);
        await versionSession.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var output = await versionSession.ReadErrorsAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "java-flags-check.log"), output);
        Assert(versionSession.Process.ExitCode == 0 && output.Contains("java.version = 17"), "Installed Java 17 accepts the default flags through the real launch builder");
        Assert(output.Contains("destiny.path = C:\\Game Folder\\") && output.Contains("destiny.quote = a\"b"),
            "Real Java receives quoted custom properties exactly, without launching Minecraft");
    }

    defaults.AutoJoinServer = false;
    var menuOptions = MinecraftService.CreateLaunchOptions(defaults); menuOptions.JavaPath = java;
    using (var menuProcess = await LibraryTestLauncher(path, http).BuildProcessAsync(profile, menuOptions))
        Assert(!menuProcess.StartInfo.Arguments.Contains("destinydynasty.tatnikhost.net") && !menuProcess.StartInfo.Arguments.Contains("--server"),
            "Disabled automatic entry removes server connection arguments from the actual game command line");
    var missingLoader = new Process { StartInfo = new ProcessStartInfo(java) { WorkingDirectory = root } };
    missingLoader.StartInfo.ArgumentList.Add("-cp");
    missingLoader.StartInfo.ArgumentList.Add(Path.Combine(root, "missing-fabric-loader.jar"));
    missingLoader.StartInfo.ArgumentList.Add("net.fabricmc.loader.impl.launch.knot.KnotClient");
    using var session = MinecraftGameSession.Start(missingLoader);
    await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    var error = await session.ReadErrorsAsync();
    Assert(session.Process.ExitCode == 1 && error.Contains("ClassNotFoundException"), "Real Java reproduces exit 1 and a missing KnotClient with absent Fabric libraries");
    Assert(error.Contains("net.fabricmc.loader.impl.launch.knot.KnotClient"), "Early JVM stderr is captured before Minecraft can create its own logs");
    await File.WriteAllTextAsync(Path.Combine(root, "reproduced-java-error.log"), error);
}

var fixture = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath!) };
fixture.StartInfo.ArgumentList.Add("--error-fixture");
using (var session = MinecraftGameSession.Start(fixture))
{
    await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    var errors = await session.ReadErrorsAsync();
    Assert(session.Process.ExitCode == 1, "Heavy stderr is drained without blocking the child process");
    Assert(errors.Length <= MinecraftGameSession.MaxErrorCharacters && errors.EndsWith("KnotClient" + Environment.NewLine), "Diagnostics retain a bounded tail with the actual final error");
}
var invalid = new Process { StartInfo = new ProcessStartInfo(Path.Combine(root, "missing-java.exe")) };
try { using var ignored = MinecraftGameSession.Start(invalid); throw new Exception("Missing Java accepted"); }
catch (System.ComponentModel.Win32Exception) { checks.Add("A process start failure is returned before any background game session begins"); }

await File.WriteAllTextAsync(Path.Combine(root, "minecraft-test-results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks.Count} Minecraft preparation and diagnostic checks; isolated files: {root}");
return 0;

sealed class ProfileHandler(List<string> requests, string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        requests.Add(request.RequestUri!.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = request.RequestUri!.AbsolutePath.EndsWith(".jar") ? new ByteArrayContent(Encoding.UTF8.GetBytes("fixture-library")) : new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
