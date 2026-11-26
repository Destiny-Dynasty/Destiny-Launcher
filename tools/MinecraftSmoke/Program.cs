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

if (args.FirstOrDefault() == "--microsoft-only")
{
    await MicrosoftAudit.Run(Path.GetFullPath(args[1]));
    return 0;
}

if (args.FirstOrDefault() == "--java-only")
{
    await JavaRuntimeAudit.Run(Path.GetFullPath(args[1]));
    return 0;
}

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
Assert(launchOptions.ScreenWidth == 1280 && launchOptions.ScreenHeight == 720 && !launchOptions.FullScreen, "Default windowed launch uses the configured 1280x720 resolution");
var fullOptions = MinecraftService.CreateLaunchOptions(new LauncherConfig { PlayerName = "Fixture", WindowMode = GameWindowMode.Fullscreen, GameWidth = 1920, GameHeight = 1080 });
Assert(fullOptions.FullScreen && fullOptions.ScreenWidth == 1920 && fullOptions.ScreenHeight == 1080, "Fullscreen mode reaches the supported CmlLib launch options");
Assert(string.IsNullOrEmpty(launchOptions.ServerIp), "Automatic server entry stays disabled by default");
var autoJoinLaunch = MinecraftService.CreateLaunchOptions(new LauncherConfig { PlayerName = "Fixture", AutoJoinServer = true });
Assert(autoJoinLaunch.ServerIp == "destinydynasty.tatnikhost.net", "Explicit automatic entry includes the configured server in launch options");
var profileMods = Path.Combine(root, "Portable Game", "Profiles", "Lite Mods");
var profileArguments = MinecraftService.CreateExtraJvmArguments("-Dcustom.flag=works", profileMods);
Assert(profileArguments.SequenceEqual(["-Dfabric.modsFolder=" + Path.GetFullPath(profileMods), "-Dcustom.flag=works"]),
    "Fabric receives only the selected profile's mods directory while retaining player Java flags");
try { MinecraftService.CreateExtraJvmArguments("-Dfabric.modsFolder=override", profileMods); throw new Exception("Profile mods folder override accepted"); }
catch (InvalidOperationException) { Assert(true, "Custom Java arguments cannot bypass the selected profile's mods directory"); }

var sharedPaths = new LauncherPaths(Path.Combine(root, "shared-profile-fixture"));
sharedPaths.SetInstallDirectory(Path.Combine(root, "shared-profile-fixture", "shared-game"));
Directory.CreateDirectory(sharedPaths.GameDirectory);
var sharedLite = sharedPaths.Instance("destiny-lite");
Directory.CreateDirectory(Path.Combine(sharedPaths.GameDirectory, "saves", "SharedWorld"));
Directory.CreateDirectory(Path.Combine(sharedLite, "saves", "SharedWorld"));
Directory.CreateDirectory(Path.Combine(sharedLite, "saves", "LiteWorld"));
await File.WriteAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "saves", "SharedWorld", "level.dat"), "destiny world");
await File.WriteAllTextAsync(Path.Combine(sharedLite, "saves", "SharedWorld", "level.dat"), "lite world");
await File.WriteAllTextAsync(Path.Combine(sharedLite, "saves", "LiteWorld", "level.dat"), "lite unique world");
Directory.CreateDirectory(Path.Combine(sharedPaths.GameDirectory, "Distant_Horizons_server_data", "Destiny Server"));
Directory.CreateDirectory(Path.Combine(sharedLite, "Distant_Horizons_server_data", "Destiny Server"));
await File.WriteAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "Distant_Horizons_server_data", "Destiny Server", "lod.sqlite"), "destiny LOD");
await File.WriteAllTextAsync(Path.Combine(sharedLite, "Distant_Horizons_server_data", "Destiny Server", "lod.sqlite"), "lite LOD");
Directory.CreateDirectory(Path.Combine(sharedPaths.GameDirectory, "assets", "objects"));
Directory.CreateDirectory(Path.Combine(sharedLite, "assets", "objects"));
await File.WriteAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "assets", "objects", "same.bin"), "same asset");
await File.WriteAllTextAsync(Path.Combine(sharedLite, "assets", "objects", "same.bin"), "same asset");
await File.WriteAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "assets", "objects", "different.bin"), "normal asset");
await File.WriteAllTextAsync(Path.Combine(sharedLite, "assets", "objects", "different.bin"), "lite asset");
await new SharedGameDataMigration(sharedPaths).MigrateAsync(CancellationToken.None);
Assert(File.Exists(Path.Combine(sharedPaths.GameDirectory, "saves", "LiteWorld", "level.dat"))
    && File.Exists(Path.Combine(sharedPaths.GameDirectory, "saves", "SharedWorld (Lite)", "level.dat")),
    "Worlds migrate into the shared game directory, preserving same-name worlds under an edition suffix");
Assert(File.Exists(Path.Combine(sharedPaths.GameDirectory, "Distant_Horizons_server_data", "Destiny Server (Lite)", "lod.sqlite")),
    "Conflicting Distant Horizons server databases remain intact as separate data sets");
Assert(!File.Exists(Path.Combine(sharedLite, "assets", "objects", "same.bin"))
    && File.Exists(Path.Combine(sharedPaths.GameDirectory, "assets", "objects", "different (Lite).bin")),
    "Identical reusable game assets deduplicate while differing files are preserved");

var initialLightPaths = new LauncherPaths(Path.Combine(root, "initial-light-profile"));
initialLightPaths.SetInstallDirectory(Path.Combine(root, "initial-light-profile", "shared-game"));
Directory.CreateDirectory(Path.Combine(initialLightPaths.GameDirectory, "config"));
await File.WriteAllTextAsync(Path.Combine(initialLightPaths.GameDirectory, "config", "destiny-before-first-launch.toml"), "existing normal profile");
Directory.CreateDirectory(Path.Combine(initialLightPaths.Instance("destiny-lite"), "config"));
await File.WriteAllTextAsync(Path.Combine(initialLightPaths.Instance("destiny-lite"), "config", "lite.toml"), "existing lite profile");
var firstLight = await GameProfileSession.BeginAsync(initialLightPaths, "destiny-lite", CancellationToken.None);
Assert(File.Exists(Path.Combine(initialLightPaths.GameDirectory, "config", "lite.toml"))
    && await File.ReadAllTextAsync(Path.Combine(initialLightPaths.ProfileConfigDirectory("destiny"), "config", "destiny-before-first-launch.toml")) == "existing normal profile",
    "The first launch into Lite saves the pre-existing Destiny configs before replacing the common config folder");
await firstLight.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
await firstLight.CompleteAsync();

var partialSwitchPaths = new LauncherPaths(Path.Combine(root, "partial-profile-switch"));
partialSwitchPaths.SetInstallDirectory(Path.Combine(root, "partial-profile-switch", "shared-game"));
var partialConfig = Path.Combine(partialSwitchPaths.GameDirectory, "config");
Directory.CreateDirectory(partialConfig);
await File.WriteAllTextAsync(Path.Combine(partialConfig, "custom.toml"), "partially applied config");
var backupRoot = Path.Combine(partialSwitchPaths.GameDirectory, ".destiny-profile-backup");
Directory.CreateDirectory(Path.Combine(backupRoot, "config"));
await File.WriteAllTextAsync(Path.Combine(backupRoot, "config", "custom.toml"), "config before the interrupted switch");
await File.WriteAllTextAsync(Path.Combine(backupRoot, "existing-roots.json"), "[\"config\"]");
await File.WriteAllTextAsync(Path.Combine(partialSwitchPaths.GameDirectory, ".destiny-profile-session.json"),
    JsonSerializer.Serialize(new { Slug = "destiny-lite", Phase = "preparing", ProcessId = 0, ProcessStartUtcTicks = 0L }));
var recoveredPartial = await GameProfileSession.BeginAsync(partialSwitchPaths, "destiny", CancellationToken.None);
Assert(await File.ReadAllTextAsync(Path.Combine(partialConfig, "custom.toml")) == "config before the interrupted switch",
    "An interrupted directory swap restores its backup before preparing the selected profile");
await recoveredPartial.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
await recoveredPartial.CompleteAsync();

Directory.CreateDirectory(Path.Combine(sharedPaths.GameDirectory, "config"));
await File.WriteAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "config", "destiny.toml"), "destiny-default");
Directory.CreateDirectory(Path.Combine(sharedLite, "config"));
await File.WriteAllTextAsync(Path.Combine(sharedLite, "config", "lite.toml"), "lite-default");
var destinyProfile = await GameProfileSession.BeginAsync(sharedPaths, "destiny", CancellationToken.None);
Assert(await File.ReadAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "config", "destiny.toml")) == "destiny-default",
    "The shared config directory is initialized from the original Destiny profile");
try { await GameProfileSession.BeginAsync(sharedPaths, "destiny-lite", CancellationToken.None); throw new Exception("Concurrent profile switch accepted"); }
catch (InvalidOperationException) { Assert(true, "The shared profile lock prevents overlapping game sessions"); }
await destinyProfile.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
await File.WriteAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "config", "destiny.toml"), "player-customized-destiny");
await File.WriteAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "config", "player-added.toml"), "custom file");
await destinyProfile.CompleteAsync();

var liteProfile = await GameProfileSession.BeginAsync(sharedPaths, "destiny-lite", CancellationToken.None);
Assert(File.Exists(Path.Combine(sharedPaths.GameDirectory, "config", "lite.toml"))
    && !File.Exists(Path.Combine(sharedPaths.GameDirectory, "config", "destiny.toml")),
    "Switching to Lite replaces the active config tree and does not leak Destiny-only files");
await liteProfile.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
await File.WriteAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "config", "lite.toml"), "player-customized-lite");
await liteProfile.CompleteAsync();
var destinyAgain = await GameProfileSession.BeginAsync(sharedPaths, "destiny", CancellationToken.None);
Assert(await File.ReadAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "config", "destiny.toml")) == "player-customized-destiny"
    && await File.ReadAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "config", "player-added.toml")) == "custom file",
    "Manual edits and new custom config files survive switching away from and back to Destiny");
await destinyAgain.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
await destinyAgain.CompleteAsync();

var journalPath = Path.Combine(sharedPaths.GameDirectory, ".destiny-profile-session.json");
Directory.Delete(Path.Combine(sharedPaths.GameDirectory, "config"), true);
Directory.CreateDirectory(Path.Combine(sharedPaths.GameDirectory, "config"));
File.Copy(Path.Combine(sharedPaths.ProfileConfigDirectory("destiny-lite"), "config", "lite.toml"),
    Path.Combine(sharedPaths.GameDirectory, "config", "lite.toml"));
await File.WriteAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "config", "lite.toml"), "crash-recovered-lite");
await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(new { Slug = "destiny-lite", Phase = "active", ProcessId = int.MaxValue, ProcessStartUtcTicks = DateTime.UtcNow.Ticks }));
var afterCrash = await GameProfileSession.BeginAsync(sharedPaths, "destiny", CancellationToken.None);
Assert(await File.ReadAllTextAsync(Path.Combine(sharedPaths.ProfileConfigDirectory("destiny-lite"), "config", "lite.toml")) == "crash-recovered-lite"
    && await File.ReadAllTextAsync(Path.Combine(sharedPaths.GameDirectory, "config", "destiny.toml")) == "player-customized-destiny",
    "A stale session journal saves interrupted profile edits before applying the next profile");
await afterCrash.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
await afterCrash.CompleteAsync();

await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(new { Slug = "destiny-lite", Phase = "active", ProcessId = Environment.ProcessId, ProcessStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks }));
try { await GameProfileSession.BeginAsync(sharedPaths, "destiny", CancellationToken.None); throw new Exception("A live Minecraft session was overwritten"); }
catch (InvalidOperationException) { Assert(File.Exists(journalPath), "A live process journal blocks switching and remains available for later recovery"); }
File.Delete(journalPath);

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
foreach (var unsafeFlag in new[] { "-Xmx8G", "-Xms8G", "-XX:MaxHeapSize=8G", "-XX:InitialHeapSize=8G", "-XX:MaxRAMPercentage=100", "@extra-flags.txt" })
{
    try { JavaArguments.ValidateMemoryOverrides(unsafeFlag); throw new Exception("Unsafe heap override accepted"); }
    catch (FormatException) { Assert(true, "Memory selector cannot be bypassed using " + unsafeFlag); }
}
JavaArguments.ValidateMemoryOverrides(LauncherConfig.DefaultJavaArguments);
try { JavaArguments.Parse("-Dtest=\"unfinished"); throw new Exception("Unclosed quote accepted"); }
catch (FormatException) { checks.Add("Unclosed JVM quotes are rejected before launch"); }
var configPaths = new LauncherPaths(Path.Combine(root, "config-fixture"));
var configStore = new ConfigService(configPaths);
var savedLocation = Path.Combine(root, "Existing Custom Instance");
File.WriteAllText(configPaths.ConfigFile, JsonSerializer.Serialize(new { PlayerName = "Fixture", RamGb = 4, InstallDirectory = savedLocation }));
var legacyConfig = configStore.Load();
Assert(legacyConfig.InstallDirectory == savedLocation && legacyConfig.JavaArguments == LauncherConfig.DefaultJavaArguments && !legacyConfig.AutoJoinServer,
    "Old configuration gains default JVM flags without moving or renaming its installation");
legacyConfig.JavaArguments = quotedFlags;
legacyConfig.GameWidth = 1600; legacyConfig.GameHeight = 900; legacyConfig.WindowMode = GameWindowMode.Maximized;
legacyConfig.AutoJoinServer = false;
configStore.Save(legacyConfig);
Assert(configStore.Load().JavaArguments == quotedFlags && !configStore.Load().AutoJoinServer, "Custom flags and disabled server entry round-trip through persisted settings");
Assert(configStore.Load().GameWidth == 1600 && configStore.Load().GameHeight == 900 && configStore.Load().WindowMode == GameWindowMode.Maximized,
    "Game resolution and exclusive window mode persist with existing settings");
await File.WriteAllTextAsync(configPaths.ConfigFile, JsonSerializer.Serialize(new { PlayerName = "Fixture", GameWidth = -1, GameHeight = 99999, WindowMode = 99 }));
var normalized = configStore.Load();
Assert(normalized.PlayerName == "Fixture" && normalized.GameWidth == 1280 && normalized.GameHeight == 720 && normalized.WindowMode == GameWindowMode.Windowed,
    "Invalid stored display options recover defaults without resetting the offline player");
await GameOptionsFile.SetFullscreenAsync(freshInstance, true, CancellationToken.None);
Assert((await File.ReadAllTextAsync(spanishOptions)).Contains("lang:fr_fr") && (await File.ReadAllTextAsync(spanishOptions)).Contains("fullscreen:true"),
    "Fullscreen option is written without resetting the player's language");
await GameOptionsFile.SetFullscreenAsync(freshInstance, false, CancellationToken.None);
var afterWindowed = await File.ReadAllTextAsync(spanishOptions);
Assert(afterWindowed.Contains("fullscreen:false") && afterWindowed.Contains("gamma:0.7") && afterWindowed.Split("fullscreen:").Length == 2,
    "Windowed mode overrides a saved fullscreen preference without duplicating it or modifying other options");
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
Assert(requests.Count(r => r.Contains("/profile/json")) == 1, "A valid installed Fabric profile is reused rather than downloaded on every start");
Assert(MinecraftService.HasLocalProfile(path, profile, "1.20.1"), "Complete local Fabric metadata is recognized for the offline fast path");
Assert(!MinecraftService.HasLocalProfile(path, profile, "1.21") && !MinecraftService.HasLocalProfile(path, "missing", null),
    "Wrong inheritance or missing metadata cannot enter the local-profile fast path");
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
var java = Environment.GetEnvironmentVariable("DESTINY_TEST_JAVA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions", "DestinyDynasty", "runtime", "windows-x64", "java-runtime-gamma", "bin", "java.exe");
if (File.Exists(java))
{
    defaults.AutoJoinServer = true;
    defaults.JavaArguments = LauncherConfig.DefaultJavaArguments + " " + quotedFlags + " -Xms16m -XshowSettings:properties -version";
    var javaOptions = MinecraftService.CreateLaunchOptions(defaults, null, profileMods);
    javaOptions.JavaPath = java;
    using (var versionProcess = await LibraryTestLauncher(path, http).BuildProcessAsync(profile, javaOptions))
    {
            await File.WriteAllTextAsync(Path.Combine(root, "java-flags-command.txt"), versionProcess.StartInfo.FileName + " " + versionProcess.StartInfo.Arguments);
        Assert(versionProcess.StartInfo.Arguments.Contains("destinydynasty.tatnikhost.net"), "Enabled automatic entry reaches the actual game command line");
        Assert(versionProcess.StartInfo.Arguments.Contains("--width 1280") && versionProcess.StartInfo.Arguments.Contains("--height 720"),
            "Configured resolution reaches the actual Minecraft command line");
        JavaArguments.CorrectWindowsQuoting(versionProcess.StartInfo, javaOptions.ExtraJvmArguments.SelectMany(a => a.Values));
        Assert(!versionProcess.StartInfo.Arguments.Contains("-Xmx1024m"), "An explicit custom heap flag does not produce a conflicting duplicate");
        using var versionSession = MinecraftGameSession.Start(versionProcess);
        await versionSession.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var output = await versionSession.ReadErrorsAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "java-flags-check.log"), output);
        Assert(versionSession.Process.ExitCode == 0 && output.Contains("java.version = "), "An installed Java runtime accepts the default flags through the real launch builder");
        Assert(output.Contains("destiny.path = C:\\Game Folder\\") && output.Contains("destiny.quote = a\"b"),
            "Real Java receives quoted custom properties exactly, without launching Minecraft");
        Assert(output.Contains("fabric.modsFolder = " + Path.GetFullPath(profileMods)),
            "Real Java receives the selected per-edition Fabric mods folder, including paths with spaces");
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
