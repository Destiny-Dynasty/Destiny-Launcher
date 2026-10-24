using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DestinyLauncher.Services;

if (args.FirstOrDefault() == "--live-current-check")
{
    var paths = new LauncherPaths(Path.GetFullPath(args[1]));
    using var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DestinyLauncher-Production-Audit");
    using var response = await client.GetAsync("https://api.github.com/repos/Destiny-Dynasty/Destiny-Launcher/releases/latest");
    response.EnsureSuccessStatusCode();
    using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var current = typeof(LauncherUpdateService).Assembly.GetName().Version!;
    if (metadata.RootElement.GetProperty("tag_name").GetString() != "v" + current.ToString(3))
        throw new Exception("Stable release does not match the production executable.");
    using var live = new LauncherUpdateService(paths, new HttpClient(), LauncherUpdateService.PublicKey(), current, "missing-unsigned-fixture.exe");
    if (await live.CheckAsync(CancellationToken.None) is not null)
        throw new Exception("The production launcher must not offer an old test release or update to itself.");
    Console.WriteLine($"PASS: stable GitHub release matches {current.ToString(3)} and current launcher offers no spurious update.");
    return;
}
if (args.FirstOrDefault() == "--live-check")
{
    var livePaths = new LauncherPaths(Path.GetFullPath(args[1])); livePaths.Ensure();
    using var live = new LauncherUpdateService(livePaths, new HttpClient(), LauncherUpdateService.PublicKey(), new Version(0, 0, 0), "missing-unsigned-fixture.exe");
    var found = await live.CheckAsync(CancellationToken.None) ?? throw new Exception("No stable signed update found");
    var staged = await live.DownloadAsync(found, new Progress<double>(), CancellationToken.None);
    File.WriteAllText(Path.Combine(livePaths.Root, "live-update-result.json"), JsonSerializer.Serialize(new { verified = true, found.Version, found.InstallerUrl, found.Sha256, staged }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS: official GitHub release {found.Version}, project signature and actual installer SHA-256 verified; {staged}");
    return;
}

if (args.FirstOrDefault() == "--apply-fixture-update")
{
    var target = Path.GetFullPath(args[1]); var setupFile = Path.GetFullPath(args[2]);
    var allowed = Path.Combine(Environment.CurrentDirectory, "artifacts") + Path.DirectorySeparatorChar;
    if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !setupFile.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Integration tests must stay in artifacts.");
    var installation = LauncherInstallation.Read(target);
    if (installation is not { Experimental: true }) throw new Exception("Refusing to update a non-experimental installation.");
    var current = Version.Parse(System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(target, "DestinyLauncher.exe")).FileVersion!);
    var next = Version.Parse(System.Diagnostics.FileVersionInfo.GetVersionInfo(setupFile).ProductVersion!).ToString(3);
    var setupBytes = File.ReadAllBytes(setupFile);
    using var testKey = RSA.Create(2048);
    var nextRelease = new LauncherRelease(1, next, "win-x64", $"https://github.com/Destiny-Dynasty/Destiny-Launcher/releases/download/v{next}/DestinyLauncherSetup.exe", setupBytes.Length, Convert.ToHexString(SHA256.HashData(setupBytes)));
    var nextBytes = JsonSerializer.SerializeToUtf8Bytes(nextRelease);
    var nextSignature = testKey.SignData(nextBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    var verified = LauncherUpdateService.VerifyManifest(nextBytes, nextSignature, testKey.ExportSubjectPublicKeyInfoPem());
    using var update = new LauncherUpdateService(new LauncherPaths(installation.DataRoot), new HttpClient(new FixtureHandler(new() { [nextRelease.InstallerUrl] = setupBytes })), testKey.ExportSubjectPublicKeyInfoPem(), current, Path.Combine(target, "DestinyLauncher.exe"));
    var staged = await update.DownloadAsync(verified, new Progress<double>(), CancellationToken.None);
    using var setup = System.Diagnostics.Process.Start(LauncherUpdateService.InstallCommand(staged, target))!;
    Console.WriteLine($"Authenticated integration upgrade handed off: {current} -> {next}, installer PID {setup.Id}. Test driver is exiting so Setup can proceed.");
    return;
}
if (args.FirstOrDefault() == "--verify-manifest")
{
    var releaseToVerify = LauncherUpdateService.VerifyManifest(File.ReadAllBytes(args[1]), File.ReadAllBytes(args[2]), LauncherUpdateService.PublicKey());
    Console.WriteLine("Verified with the actual embedded project key: " + releaseToVerify.Version);
    return;
}
var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/installation-smoke");
root = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
void Reject(Action action, string label) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException) { checks.Add(label); return; } throw new Exception(label); }
async Task RejectAsync(Func<Task> action, string label) { try { await action(); } catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or OperationCanceledException) { checks.Add(label); return; } throw new Exception(label); }

foreach (var (profiles, games) in new[] { (false, false), (true, false), (false, true), (true, true) })
{
    var paths = new LauncherPaths(Path.Combine(root, $"remove-{profiles}-{games}")); paths.Ensure();
    File.WriteAllText(paths.ConfigFile, "profile-data");
    var data = new InstallationDataService(paths);
    var owned = new[] { Path.Combine(paths.Root, "instances", "Destiny"), Path.Combine(paths.Root, "custom", "Destiny-Lite"), Path.Combine(paths.Root, "old-location", "Destiny") };
    foreach (var path in owned)
    {
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "destiny-installed.json"), "{\"Version\":\"1.4.7\"}");
        File.WriteAllText(Path.Combine(path, "world.fixture"), "owned test world"); data.Register(path);
    }
    var external = Path.Combine(paths.Root, "other-minecraft"); Directory.CreateDirectory(external); File.WriteAllText(Path.Combine(external, "keep.txt"), "other instance");
    Check(data.PlanGameRemoval().Count == 3, "History includes both variants and previous custom locations");
    data.RemoveSelected(profiles, games);
    Check(File.Exists(paths.ConfigFile) != profiles, $"Profile deletion is opt-in ({profiles},{games})");
    Check(owned.All(p => Directory.Exists(p) != games), $"Game deletion is independently opt-in ({profiles},{games})");
    Check(File.ReadAllText(Path.Combine(external, "keep.txt")) == "other instance", "Unregistered Minecraft instances remain untouched");
    if (profiles && !games) Check(data.PlanGameRemoval().Count == 3, "Removing profiles preserves retained-game ownership records");
}
{
    var paths = new LauncherPaths(Path.Combine(root, "guards")); paths.Ensure();
    var data = new InstallationDataService(paths);
    File.WriteAllText(Path.Combine(paths.Root, "destiny-installed.json"), "{}");
    Reject(() => data.Register(paths.Root), "Shared data root can never become a deletable game root");
    var instance = Path.Combine(paths.Root, "game"); Directory.CreateDirectory(instance); File.WriteAllText(Path.Combine(instance, "destiny-installed.json"), "{}"); data.Register(instance);
    File.WriteAllText(Path.Combine(instance, ".destiny-instance.json"), "{}");
    File.WriteAllText(paths.ConfigFile, "keep");
    Reject(() => data.RemoveSelected(true, true), "Invalid ownership aborts before game or profile deletion");
    Check(File.ReadAllText(paths.ConfigFile) == "keep" && Directory.Exists(instance), "Failed validation preserves the complete deletion plan");
}
{
    var paths = new LauncherPaths(Path.Combine(root, "cache-game")); paths.Ensure(); var data = new InstallationDataService(paths);
    var instance = Path.Combine(paths.Cache, "custom-game"); Directory.CreateDirectory(instance); File.WriteAllText(Path.Combine(instance, "destiny-installed.json"), "{}"); data.Register(instance);
    data.RemoveSelected(true, false);
    Check(Directory.Exists(instance), "Profile cleanup cannot delete a game placed inside a cache folder");
}
{
    var paths = new LauncherPaths(Path.Combine(root, "junction")); paths.Ensure(); var data = new InstallationDataService(paths);
    var instance = Path.Combine(paths.Root, "game"); Directory.CreateDirectory(instance); File.WriteAllText(Path.Combine(instance, "destiny-installed.json"), "{}"); data.Register(instance);
    var outside = Path.Combine(root, "external-target"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
    // Windows junction creation works without symlink privilege; only this fixture is affected.
    var command = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
    command.ArgumentList.Add("/c"); command.ArgumentList.Add("mklink"); command.ArgumentList.Add("/J"); command.ArgumentList.Add(Path.Combine(instance, "linked")); command.ArgumentList.Add(outside);
    using var process = System.Diagnostics.Process.Start(command)!; await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception("Fixture junction failed");
    data.RemoveSelected(false, true);
    Check(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "outside", "Recursive cleanup removes a junction without traversing its external target");
}

using var key = RSA.Create(2048);
var publicKey = key.ExportSubjectPublicKeyInfoPem();
var payload = Encoding.UTF8.GetBytes("controlled installer fixture");
var release = new LauncherRelease(1, "9.0.0", "win-x64", "https://github.com/Destiny-Dynasty/Destiny-Launcher/releases/download/v9.0.0/DestinyLauncherSetup.exe", payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
var bytes = JsonSerializer.SerializeToUtf8Bytes(release);
var signature = key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
Check(LauncherUpdateService.VerifyManifest(bytes, signature, publicKey) == release, "Release metadata requires a valid project-key signature");
var withNotes = release with { Changelog = ["Nueva ventana de actualización.", "Descarga independiente del launcher."] };
var notesBytes = JsonSerializer.SerializeToUtf8Bytes(withNotes);
var notesSignature = key.SignData(notesBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
Check(LauncherUpdateService.VerifyManifest(notesBytes, notesSignature, publicKey).Changelog!.SequenceEqual(withNotes.Changelog!), "Changelog travels inside the authenticated release metadata");
Reject(() => LauncherUpdateService.VerifyManifest(JsonSerializer.SerializeToUtf8Bytes(withNotes with { Changelog = ["Alterado"] }), notesSignature, publicKey), "Editing signed changelog invalidates its signature");
var tooLong = JsonSerializer.SerializeToUtf8Bytes(release with { Changelog = [new string('x', 241)] });
Reject(() => LauncherUpdateService.VerifyManifest(tooLong, key.SignData(tooLong, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), publicKey), "Oversized changelog entries are rejected");
var modified = bytes.ToArray(); modified[5] ^= 1;
Reject(() => LauncherUpdateService.VerifyManifest(modified, signature, publicKey), "Tampered release metadata is rejected");
using var wrong = RSA.Create(2048);
Reject(() => LauncherUpdateService.VerifyManifest(bytes, signature, wrong.ExportSubjectPublicKeyInfoPem()), "A different signing key cannot authorize an update");
Reject(() => LauncherUpdateService.ValidateAssetUri("https://github.com.evil.invalid/DestinyLauncherSetup.exe", "v9.0.0", "DestinyLauncherSetup.exe"), "Lookalike download domains are rejected");
Reject(() => LauncherUpdateService.ValidateAssetUri(release.InstallerUrl + "?redirect=elsewhere", "v9.0.0", "DestinyLauncherSetup.exe"), "Release URLs cannot carry query-based redirects");
var feed = JsonSerializer.SerializeToUtf8Bytes(new { draft = false, prerelease = false, tag_name = "v9.0.0", assets = new[] {
    new { name="launcher-update.json", browser_download_url=release.InstallerUrl.Replace("DestinyLauncherSetup.exe", "launcher-update.json") },
    new { name="launcher-update.sig", browser_download_url=release.InstallerUrl.Replace("DestinyLauncherSetup.exe", "launcher-update.sig") } } });
var responses = new Dictionary<string, byte[]> { ["https://api.github.com/repos/Destiny-Dynasty/Destiny-Launcher/releases/latest"] = feed,
    [release.InstallerUrl.Replace("DestinyLauncherSetup.exe", "launcher-update.json")] = bytes,
    [release.InstallerUrl.Replace("DestinyLauncherSetup.exe", "launcher-update.sig")] = signature, [release.InstallerUrl] = payload };
var updatePaths = new LauncherPaths(Path.Combine(root, "update")); updatePaths.Ensure();
using var updater = new LauncherUpdateService(updatePaths, new HttpClient(new FixtureHandler(responses)), publicKey, new Version(1, 0, 0, 0), Path.Combine(root, "unsigned.exe"));
Check(await updater.CheckAsync(CancellationToken.None) == release, "Startup resolves and authenticates one coherent GitHub release");
var download = await updater.DownloadAsync(release, new Progress<double>(), CancellationToken.None);
Check(File.ReadAllBytes(download).SequenceEqual(payload), "Verified update is staged without replacing the running launcher");
responses[release.InstallerUrl] = Encoding.UTF8.GetBytes("wrong installer fixture bytes");
await RejectAsync(() => updater.DownloadAsync(release, new Progress<double>(), CancellationToken.None), "Corrupt or wrong-size installer is rejected");
Check(File.ReadAllBytes(download).SequenceEqual(payload) && Directory.GetFiles(Path.GetDirectoryName(download)!, "*.part").Length == 0,
    "Failed download preserves the previously verified file and removes partial data");
using var canceled = new CancellationTokenSource(); canceled.Cancel();
await RejectAsync(() => updater.DownloadAsync(release, new Progress<double>(), canceled.Token), "Canceling an update never launches its installer");
using var old = new LauncherUpdateService(updatePaths, new HttpClient(new FixtureHandler(responses)), publicKey, new Version(10, 0, 0), "missing");
Check(await old.CheckAsync(CancellationToken.None) is null, "Older server releases cannot downgrade the launcher");
var commandLine = LauncherUpdateService.InstallCommand("fixture.exe", @"C:\Folder With Spaces\Launcher");
Check(!commandLine.UseShellExecute && commandLine.ArgumentList.Contains(@"/DIR=C:\Folder With Spaces\Launcher") && commandLine.ArgumentList.Contains("/AUTORESTART=1"),
    "Silent upgrade preserves quoted install paths and requests restart after its parent exits");
Check(!string.IsNullOrWhiteSpace(LauncherUpdateService.PublicKey()), "The release public key is embedded in the launcher");
File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks.Count} installation and authenticated-update assertions; {root}");

internal sealed class FixtureHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var data)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
