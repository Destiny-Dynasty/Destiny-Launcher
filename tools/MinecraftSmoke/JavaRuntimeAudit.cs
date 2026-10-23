using System.Net;
using System.Net.Http;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.VersionLoader;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

internal static class JavaRuntimeAudit
{
    internal static async Task Run(string root)
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        var paths = new LauncherPaths(root); paths.Ensure();
        var log = new AppLogger(paths);
        var reports = new List<(string status, double value)>();
        var progress = new InlineProgress(value => reports.Add(value));
        var service = new JavaRuntimeService(paths, log);
        var java = await service.EnsureAsync(21, progress, CancellationToken.None);
        Check(File.Exists(java) && java.StartsWith(paths.Cache), "Real Temurin 21 is installed exclusively in isolated launcher data");
        await JavaRuntimeService.VerifyExecutableAsync(Path.Combine(Path.GetDirectoryName(java)!, "java.exe"), 21, CancellationToken.None);
        Check(true, "Downloaded Java 21 executes successfully");
        Check(new PackManifest().JavaVersion == 21, "Legacy manifests default to Java 21");
        var parameters = MinecraftLauncherParameters.CreateDefault(new MinecraftPath(Path.Combine(root, "minecraft")));
        var before = parameters.FileExtractors!.Count();
        MinecraftService.UseManagedRuntime(parameters);
        Check(parameters.FileExtractors!.All(e => e is not JavaFileExtractor and not LegacyJavaFileExtractor) && parameters.FileExtractors!.Count() < before,
            "Managed Java prevents CmlLib from also downloading Minecraft's default Java 17");
        Check(parameters.FileExtractors!.Count() >= 3, "Assets, libraries and other Minecraft file installers remain enabled");
        var instance = parameters.MinecraftPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(instance.GetVersionJsonPath("1.20.1"))!);
        File.WriteAllText(instance.GetVersionJsonPath("1.20.1"), """
            {"id":"1.20.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}
            """);
        parameters.VersionLoader = new LocalJsonVersionLoader(instance);
        var options = MinecraftService.CreateLaunchOptions(new LauncherConfig { PlayerName = "Fixture", RamGb = 1, AutoJoinServer = false,
            JavaArguments = LauncherConfig.DefaultJavaArguments + " -Xms16m -XshowSettings:properties -version" });
        options.JavaPath = java;
        using (var process = await new MinecraftLauncher(parameters).BuildProcessAsync("1.20.1", options))
        {
            Check(process.StartInfo.FileName == java, "Real Minecraft process builder uses managed Java instead of the Java 17 profile default");
            JavaArguments.CorrectWindowsQuoting(process.StartInfo, options.ExtraJvmArguments.SelectMany(a => a.Values));
            using var session = MinecraftGameSession.Start(process);
            await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var output = await session.ReadErrorsAsync();
            File.WriteAllText(Path.Combine(root, "managed-launch-flags.log"), output);
            Check(session.Process.ExitCode == 0 && output.Contains("java.version = 21."), "Java 21 accepts the actual launcher command line and default JVM flags");
        }
        var offlineHandler = new OfflineHandler();
        var offline = new JavaRuntimeService(paths, log, new HttpClient(offlineHandler));
        Check(await offline.EnsureAsync(21, progress, CancellationToken.None) == java && offlineHandler.Calls == 0,
            "Cached runtime is reused without any network request");
        var marker = Path.Combine(paths.Cache, "java", "temurin-21", "active.json");
        var active = JsonSerializer.Deserialize<JavaRuntimeService.Runtime>(File.ReadAllText(marker))!;
        File.WriteAllText(marker, JsonSerializer.Serialize(active with { CheckedUtc = DateTime.UtcNow.AddDays(-8) }));
        Check(await offline.EnsureAsync(21, progress, CancellationToken.None) == java && offlineHandler.Calls == 1,
            "Offline patch checks preserve the working runtime");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await service.EnsureAsync(21, progress, cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { checks.Add("Cancellation stops runtime preparation"); }
        foreach (var name in new[] { "../outside.txt", "C:/outside.txt", "home/link:stream" })
        {
            var zipPath = Path.Combine(root, Guid.NewGuid() + ".zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("unsafe"); }
            try { JavaRuntimeService.Extract(zipPath, Path.Combine(root, "extract"), CancellationToken.None); throw new Exception("Unsafe path accepted"); }
            catch (InvalidDataException) { checks.Add("ZIP rejects unsafe path: " + name); }
        }
        var corruptPaths = new LauncherPaths(Path.Combine(root, "corrupt")); corruptPaths.Ensure();
        var corrupt = new JavaRuntimeService(corruptPaths, new AppLogger(corruptPaths), new HttpClient(new CorruptHandler()));
        try { await corrupt.EnsureAsync(21, progress, CancellationToken.None); throw new Exception("Checksum ignored"); }
        catch (InvalidDataException) { checks.Add("Corrupt download fails SHA-256 before extraction or execution"); }
        Check(!Directory.GetFiles(corruptPaths.Cache, "*.part", SearchOption.AllDirectories).Any(), "Failed download removes partial archive");
        Check(!File.Exists(Path.Combine(corruptPaths.Cache, "java", "temurin-21", "active.json")), "Failed first installation never activates a runtime");
        var configService = new ConfigService(paths);
        configService.SaveInstalled("destiny", "fixture", 25);
        Check(configService.InstalledJavaVersion("destiny") == 25, "Installed major version persists for offline launches");
        File.WriteAllText(Path.Combine(root, "java-test-results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks, java, reports }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {checks.Count} managed Java assertions; {java}");
    }
    private sealed class InlineProgress(Action<(string status, double value)> action) : IProgress<(string status, double value)>
    { public void Report((string status, double value) value) => action(value); }
    private sealed class OfflineHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new HttpRequestException("Offline fixture"); }
    }
    private sealed class CorruptHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var bytes = request.RequestUri!.Host == "api.adoptium.net"
                ? JsonSerializer.SerializeToUtf8Bytes(new[] { new { version = new { major = 21 }, binary = new { package = new { checksum = new string('0', 64), size = 3, link = "https://github.com/adoptium/temurin21-binaries/releases/download/fixture/runtime.zip" } } } })
                : new byte[] { 1, 2, 3 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
