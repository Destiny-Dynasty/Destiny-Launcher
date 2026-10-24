using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

if (args.FirstOrDefault() == "--live-manifests")
{
    var root = Path.GetFullPath(args[1]);
    var paths = new LauncherPaths(root);
    var updater = new GitHubPackUpdater(paths, new AppLogger(paths));
    var results = new List<object>();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    foreach (var id in new[] { "destiny", "destiny-lite", "destiny-staff" })
    {
        var pack = new PackDefinition { Slug = id };
        var manifest = await updater.ReadManifestAsync(pack, timeout.Token);
        if (manifest.Version != args[2] || manifest.SchemaVersion != 2)
            throw new Exception($"Unexpected public release metadata: {id}");
        var plan = await updater.PrepareForPlayAsync(pack, manifest, "", null, timeout.Token);
        var realms = plan.Manifest.Files.Single(f => f.Path == "mods/destiny-dynasty.jar");
        if (plan.ChangedFiles.Count != manifest.Files.Count || realms.GitHub?.Channel != "destiny-dynasty"
            || realms.GitHub.Repository != "Destiny-Dynasty/Mod-Releases"
            || plan.Manifest.Files.Any(f => f.Url.Contains("/Modpack/releases/download/") && !f.Url.Contains($"/modpack-{args[2]}/")))
            throw new Exception($"Public install plan is inconsistent: {id}");
        if (Directory.Exists(Path.Combine(paths.Instance(id), "mods")))
            throw new Exception("Metadata audit must not install any game files.");
        results.Add(new { id, manifest.Version, manifest.FabricLoaderVersion, files = plan.ChangedFiles.Count, realms.Url, installed = false });
    }
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "public-manifests.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS: actual launcher validates all public manifests and resolves Destiny Dynasty latest for all variants. No game files installed.");
    return 0;
}

if (args.FirstOrDefault() == "--floating-only")
{
    await FloatingModsAudit.Run(Path.GetFullPath(args[1]));
    return 0;
}
if (args.FirstOrDefault() == "--live-github-mod")
{
    await FloatingModsAudit.Live(Path.GetFullPath(args[1]));
    return 0;
}

var tempRoot = Path.Combine(Path.GetTempPath(), $"destiny-pack-smoke-{Guid.NewGuid():N}");
try
{
    var paths = new LauncherPaths(tempRoot);
    Require(paths.Instance("destiny").StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase), "The test instance must be isolated.");
    Require(paths.Instance("destiny-lite") == paths.Instance("destiny") + "-Lite", "Lite must keep its separate sibling path.");
    var fixture = new FixtureHandler();
    var updater = new GitHubPackUpdater(paths, new AppLogger(paths), new HttpClient(fixture));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var token = cancellation.Token;
    var pack = new PackDefinition();
    var metadataOnly = await updater.ReadManifestAsync(pack, token);
    Require(metadataOnly.Version == "1.0.1" && !Directory.Exists(paths.Instance(pack.Slug)), "Startup metadata must not create an instance or inspect local files.");
    var initial = await updater.CheckAsync(pack, null, token);
    Require(fixture.LastManifestUri?.AbsoluteUri == "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack/main/manifests/destiny.json", "The full pack must keep its existing repository.");
    Require(initial.ChangedFiles.Count == 2, "Initial installation must download two fixture files.");
    await updater.ApplyAsync(pack, initial, null, token);
    Require(!(await updater.CheckAsync(pack, null, token)).HasChanges, "Rechecking must be a no-op.");
    var instance = paths.Instance(pack.Slug);
    var kept = Path.Combine(instance, "mods", "kept.txt");
    using (var lockedMod = new FileStream(kept, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var lightweight = await updater.ReadManifestAsync(pack, token);
        Require(lightweight.Version == "1.0.1", "Startup must expose the version without opening a locked mod.");
        Require(!(await updater.PlanUpdateAsync(pack, lightweight, null, token)).HasChanges, "An unchanged upstream file must not be opened even during planning.");
    }
    await File.WriteAllTextAsync(kept, "local player edit", token);
    Directory.CreateDirectory(Path.Combine(instance, "saves"));
    var personal = Path.Combine(instance, "saves", "world.txt");
    await File.WriteAllTextAsync(personal, "personal world", token);
    fixture.Version = 2;
    var update = await updater.CheckAsync(pack, null, token);
    Require(update.ChangedFiles.Count == 1 && update.ChangedFiles[0].Path == "mods/alpha.txt", "Only the changed managed file should download.");
    Require(!update.ChangedFiles.Any(f => f.Path == "mods/kept.txt"), "An upstream-unchanged player edit must not be scanned or repaired.");
    await updater.ApplyAsync(pack, update, null, token);
    Require(await File.ReadAllTextAsync(Path.Combine(instance, "mods", "alpha.txt"), token) == "alpha fixture version 2", "The update was not applied.");
    Require(await File.ReadAllTextAsync(kept, token) == "local player edit", "The player edit was overwritten.");
    Require(!(await updater.CheckAsync(pack, null, token)).HasChanges, "The updated files should be current.");
    var personalMod = Path.Combine(instance, "mods", "added-by-player.jar");
    await File.WriteAllTextAsync(personalMod, "personal mod", token);
    File.Delete(Path.Combine(instance, "mods", "alpha.txt"));
    fixture.Version = 3;
    var deletion = await updater.CheckAsync(pack, null, token);
    Require(deletion.ChangedFiles.Count == 0 && deletion.PreservedOverrides.Contains("mods/alpha.txt"), "An intentionally deleted managed mod must not be restored by an update.");
    await updater.ApplyAsync(pack, deletion, null, token);
    fixture.Version = 4;
    var later = await updater.CheckAsync(pack, null, token);
    Require(!later.HasChanges && !File.Exists(Path.Combine(instance, "mods", "alpha.txt")), "A saved deletion must remain deleted on later upstream changes.");
    Require(await File.ReadAllTextAsync(personalMod, token) == "personal mod", "Added player mods must be left untouched.");
    // Restore the isolated fixture baseline for corruption and rollback checks below.
    File.Delete(Path.Combine(instance, ".destiny-managed-files.json"));
    File.Delete(kept);
    fixture.Version = 2;
    await updater.ApplyAsync(pack, await updater.CheckAsync(pack, null, token), null, token);
    await File.WriteAllTextAsync(kept, "local player edit", token);
    Console.WriteLine("PASS: isolated install, no-op check, incremental update and preserved player edits.");

    fixture.Version = 3;
    fixture.CorruptDownload = true;
    var badDownload = await updater.CheckAsync(pack, null, token);
    await Reject<InvalidDataException>(() => updater.ApplyAsync(pack, badDownload, null, token));
    Require(await File.ReadAllTextAsync(Path.Combine(instance, "mods", "alpha.txt"), token) == "alpha fixture version 2", "A corrupt download changed installed files.");
    Require(await File.ReadAllTextAsync(personal, token) == "personal world", "A personal file changed.");
    Console.WriteLine("PASS: bad SHA-256 is rejected without changing installed or personal files.");

    fixture.CorruptDownload = false;
    fixture.BadPath = "saves/world.txt";
    await Reject<InvalidDataException>(() => updater.CheckAsync(pack, null, token));
    fixture.BadPath = "mods/../../outside.txt";
    await Reject<InvalidDataException>(() => updater.CheckAsync(pack, null, token));
    fixture.BadPath = null;
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Reject<OperationCanceledException>(() => updater.CheckAsync(pack, null, cancelled.Token));
    Require(await File.ReadAllTextAsync(personal, token) == "personal world", "Validation/cancellation modified personal files.");
    Console.WriteLine("PASS: protected paths, traversal and cancellation preserve user data.");

    var lite = new PackDefinition { Name = "Destiny Lite", Slug = "destiny-lite" };
    fixture.LiteUnavailable = true;
    await Reject<HttpRequestException>(() => updater.CheckAsync(lite, null, token));
    Require(!Directory.Exists(paths.Instance(lite.Slug)), "An unpublished Lite feed must not create an empty installation.");
    Require(fixture.LastManifestUri?.AbsoluteUri == "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack/main/manifests/destiny-lite.json", "Lite must use the unified repository.");
    fixture.LiteUnavailable = false;
    var liteCheck = await updater.CheckAsync(lite, null, token);
    Require(liteCheck.Manifest.PackId == "destiny-lite" && liteCheck.ChangedFiles.Count == 2, "The future Lite feed must use the existing manifest contract.");
    await updater.ApplyAsync(lite, liteCheck, null, token);
    Require(await File.ReadAllTextAsync(Path.Combine(paths.Instance(lite.Slug), "mods", "alpha.txt"), token) == "alpha fixture version 3", "Lite files must install in their own instance.");
    Require(await File.ReadAllTextAsync(Path.Combine(instance, "mods", "alpha.txt"), token) == "alpha fixture version 2"
        && await File.ReadAllTextAsync(kept, token) == "local player edit", "Installing Lite must not change the full pack or its player edits.");
    Console.WriteLine("PASS: unpublished Lite does not install; its independent feed and instance preserve the full pack.");

    fixture.RemoveKept = true;
    var removal = await updater.CheckAsync(lite, null, token);
    Require(removal.RemovedFiles.SequenceEqual(new[] { "mods/kept.txt" }), "A removed upstream file is only removable when its installed content remains original.");
    await updater.ApplyAsync(lite, removal, null, token);
    Require(!File.Exists(Path.Combine(paths.Instance(lite.Slug), "mods", "kept.txt")), "An untouched obsolete managed file must be removed during an update.");
    fixture.RemoveKept = false; fixture.Version = 1;
    var customPaths = new LauncherPaths(Path.Combine(tempRoot, "custom-instance")); customPaths.Ensure();
    var customMods = Path.Combine(customPaths.Instance(pack.Slug), "mods"); Directory.CreateDirectory(customMods);
    var customAlpha = Path.Combine(customMods, "alpha.txt"); var customKept = Path.Combine(customMods, "kept.txt");
    await File.WriteAllTextAsync(customAlpha, "player file at a manifest path", token);
    var customUpdater = new GitHubPackUpdater(customPaths, new AppLogger(customPaths), new HttpClient(fixture));
    var collision = await customUpdater.CheckAsync(pack, null, token);
    Require(collision.ChangedFiles.Count == 1 && collision.PreservedOverrides.Contains("mods/alpha.txt"), "An untracked player file at a manifest path must not be claimed or overwritten.");
    await customUpdater.ApplyAsync(pack, collision, null, token);
    fixture.Version = 2; fixture.KeptVersion = 2;
    var customUpdate = await customUpdater.CheckAsync(pack, null, token);
    Require(customUpdate.ChangedFiles.Count == 1 && customUpdate.ChangedFiles[0].Path == "mods/kept.txt", "A previous path collision remains preserved while other files update normally.");
    await customUpdater.ApplyAsync(pack, customUpdate, null, token);
    await File.WriteAllTextAsync(customKept, "edited managed mod", token); fixture.KeptVersion = 3;
    var edited = await customUpdater.CheckAsync(pack, null, token);
    Require(!edited.HasChanges && edited.PreservedOverrides.Contains("mods/kept.txt"), "An edited managed file must survive an upstream content change.");
    await customUpdater.ApplyAsync(pack, edited, null, token); fixture.RemoveKept = true;
    var editedRemoval = await customUpdater.CheckAsync(pack, null, token);
    Require(!editedRemoval.HasChanges && editedRemoval.PreservedOverrides.Contains("mods/kept.txt"), "An edited managed file must survive its removal from the upstream manifest.");
    await customUpdater.ApplyAsync(pack, editedRemoval, null, token);
    Require(await File.ReadAllTextAsync(customAlpha, token) == "player file at a manifest path" && await File.ReadAllTextAsync(customKept, token) == "edited managed mod", "Successive updates must retain all player overrides.");
    Console.WriteLine("PASS: untracked collisions, upstream changes, intentional deletions and obsolete-file removal respect player customizations.");

    fixture.RemoveKept = false; fixture.Version = 7; fixture.KeptVersion = 1;
    var repairPaths = new LauncherPaths(Path.Combine(tempRoot, "repair-fixture")); repairPaths.Ensure();
    var repairUpdater = new GitHubPackUpdater(repairPaths, new AppLogger(repairPaths), new HttpClient(fixture));
    var official = await repairUpdater.ReadManifestAsync(pack, token);
    await repairUpdater.ApplyAsync(pack, await repairUpdater.CheckAsync(pack, null, token), null, token);
    var repairConfig = new ConfigService(repairPaths); repairConfig.SaveInstalled(pack.Slug, official.Version);
    var repairInstance = repairPaths.Instance(pack.Slug);
    var repairAlpha = Path.Combine(repairInstance, "mods", "alpha.txt"); var repairKept = Path.Combine(repairInstance, "mods", "kept.txt");
    var extra = Path.Combine(repairInstance, "mods", "third-party.jar");
    var nestedExtra = Path.Combine(repairInstance, "mods", "nested", "third-party.jar");
    Directory.CreateDirectory(Path.GetDirectoryName(nestedExtra)!);
    await File.WriteAllTextAsync(extra, "personal mod", token); await File.WriteAllTextAsync(nestedExtra, "nested personal mod", token);
    var personalFiles = new Dictionary<string, byte[]>();
    foreach (var relative in new[] { "saves/world.dat", "screenshots/photo.png", "options.txt", "servers.dat", "config/personal.txt" })
    {
        var file = Path.Combine(repairInstance, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var content = Encoding.UTF8.GetBytes("Player data: " + relative); await File.WriteAllBytesAsync(file, content, token); personalFiles[file] = content;
    }
    await File.WriteAllTextAsync(repairAlpha, "edited official mod", token); File.Delete(repairKept);
    Require(repairConfig.InstalledVersion(pack.Slug) == official.Version, "Deleting a managed mod does not imply uninstalling the instance.");
    var repaired = await repairUpdater.RepairAsync(pack, official, false, null, token);
    Require(repaired.ChangedFiles.Count == 2 && await File.ReadAllTextAsync(repairAlpha, token) == "alpha fixture version 7"
        && File.Exists(repairKept) && File.Exists(extra) && File.Exists(nestedExtra), "Explicit repair restores edited/deleted official files and preserves third-party mods by default.");
    foreach (var (file, content) in personalFiles) Require((await File.ReadAllBytesAsync(file, token)).SequenceEqual(content), "Repair changed protected player data.");
    Console.WriteLine("PASS: explicit same-version repair restores missing/modified files and preserves extra mods and player data.");

    await File.WriteAllTextAsync(repairAlpha, "must survive a failed download", token);
    var oldTracking = await File.ReadAllBytesAsync(Path.Combine(repairInstance, ".destiny-managed-files.json"), token);
    fixture.CorruptDownload = true;
    await Reject<InvalidDataException>(() => repairUpdater.RepairAsync(pack, official, true, null, token));
    Require(await File.ReadAllTextAsync(repairAlpha, token) == "must survive a failed download" && File.Exists(extra) && File.Exists(nestedExtra)
        && (await File.ReadAllBytesAsync(Path.Combine(repairInstance, ".destiny-managed-files.json"), token)).SequenceEqual(oldTracking),
        "A failed repair download must not modify official files, delete third-party mods or advance the tracking state.");
    fixture.CorruptDownload = false;
    var cleaned = await repairUpdater.RepairAsync(pack, official, true, null, token);
    Require(cleaned.RemovedFiles.Count == 2 && !File.Exists(extra) && !File.Exists(nestedExtra) && File.Exists(repairAlpha), "Opt-in cleanup removes only files absent from the official mods list, including nested mods.");
    foreach (var (file, content) in personalFiles) Require((await File.ReadAllBytesAsync(file, token)).SequenceEqual(content), "Third-party cleanup changed non-mod player data.");
    Require(!(await repairUpdater.RepairAsync(pack, official, true, null, token)).HasChanges, "A healthy repaired instance downloads or deletes nothing on a second repair.");
    Console.WriteLine("PASS: failed downloads delete nothing; opt-in cleanup stays inside mods and healthy repairs are a no-op.");

    await File.WriteAllTextAsync(extra, "keep on invalid repair", token);
    await Reject<OperationCanceledException>(() => repairUpdater.RepairAsync(pack, official, true, null, cancelled.Token));
    var emptyManifest = new PackManifest { PackId = pack.Slug, Version = official.Version, Files = [] };
    await Reject<InvalidDataException>(() => repairUpdater.RepairAsync(pack, emptyManifest, true, null, token));
    Require(File.Exists(extra), "Cancellation or an empty official list must never delete third-party mods.");
    var previousKept = await File.ReadAllTextAsync(repairKept, token);
    await File.WriteAllTextAsync(repairAlpha, "rollback original", token);
    Directory.CreateDirectory(Path.Combine(repairInstance, "mods", "z-locked.jar"));
    // A directory collision at a target fails before destructive cleanup and keeps the old content.
    var badTarget = Path.Combine(repairInstance, "mods", "kept.txt"); File.Delete(badTarget); Directory.CreateDirectory(badTarget);
    // .NET reports a file/directory collision as IOException on Windows and
    // UnauthorizedAccessException (EACCES) on Linux. In both cases rollback must hold.
    var collisionRejected = false;
    try { await repairUpdater.RepairAsync(pack, official, true, null, token); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { collisionRejected = true; }
    Require(collisionRejected, "A directory cannot be overwritten by an official mod file.");
    Require(await File.ReadAllTextAsync(repairAlpha, token) == "rollback original" && File.Exists(extra), "A transaction failure rolls back official replacements and preserves pending extra mods.");
    Directory.Delete(badTarget); await File.WriteAllTextAsync(badTarget, previousKept, token);
    Console.WriteLine("PASS: canceled/invalid repairs and transaction failures preserve installed and personal files.");

    // Simulate deletion of the whole mod directory while leaving the version marker.
    Directory.Delete(Path.Combine(repairInstance, "mods"), true);
    Require(repairConfig.InstalledVersion(pack.Slug) == official.Version, "The lightweight installed check is a version marker, not a mod integrity scan.");
    await repairUpdater.RepairAsync(pack, official, false, null, token);
    Require(File.Exists(repairAlpha) && File.Exists(repairKept), "Explicit repair can recover a completely missing mods directory.");
    File.Delete(Path.Combine(repairInstance, "destiny-installed.json"));
    Require(repairConfig.InstalledVersion(pack.Slug) == "", "Removing the installation marker reports the instance as not installed.");
    Console.WriteLine("PASS: installation detection follows its version marker and repair recovers a deleted mods directory.");
    var outside = Path.Combine(tempRoot, "external-directory"); Directory.CreateDirectory(outside);
    var outsideMod = Path.Combine(outside, "keep.jar"); await File.WriteAllTextAsync(outsideMod, "external user file", token);
    var linkPath = Path.Combine(repairInstance, "mods", "external-link");
    var linkAvailable = false;
    try { Directory.CreateSymbolicLink(linkPath, outside); linkAvailable = true; }
    catch (UnauthorizedAccessException) { }
    catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314) { }
    if (!linkAvailable && OperatingSystem.IsWindows())
    {
        Require(linkPath.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && outside.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Link fixture paths must remain inside the generated test root.");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + linkPath.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null");
        using var createLink = Process.Start(start)!;
        var output = createLink.StandardOutput.ReadToEndAsync(token); var errors = createLink.StandardError.ReadToEndAsync(token);
        await createLink.WaitForExitAsync(token); await Task.WhenAll(output, errors);
        linkAvailable = createLink.ExitCode == 0;
    }
    if (linkAvailable)
    {
        await repairUpdater.RepairAsync(pack, official, true, null, token);
        Require(await File.ReadAllTextAsync(outsideMod, token) == "external user file", "Third-party cleanup must never follow a directory link.");
        var linkedManifest = JsonSerializer.Deserialize<PackManifest>(JsonSerializer.Serialize(official))!;
        linkedManifest.Files[0].Path = "mods/external-link/keep.jar";
        await Reject<InvalidDataException>(() => repairUpdater.RepairAsync(pack, linkedManifest, true, null, token));
        Require(await File.ReadAllTextAsync(outsideMod, token) == "external user file", "Official file repair must also refuse linked targets outside the instance.");
        Directory.Delete(linkPath);
        Console.WriteLine("PASS: cleanup skips external links and official repair refuses linked targets.");
    }
    else Console.WriteLine("SKIP: external-link test could not create either a symlink or junction.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine($"FAIL: {ex}"); return 1; }
finally
{
    // The root is generated here and all test paths are children of this directory.
    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
sealed class FixtureHandler : HttpMessageHandler
{
    public int Version { get; set; } = 1;
    public bool CorruptDownload { get; set; }
    public string? BadPath { get; set; }
    public bool LiteUnavailable { get; set; }
    public bool RemoveKept { get; set; }
    public int KeptVersion { get; set; } = 1;
    public Uri? LastManifestUri { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var alpha = Encoding.UTF8.GetBytes($"alpha fixture version {Version}");
        var kept = Encoding.UTF8.GetBytes($"kept fixture version {KeptVersion}");
        var path = request.RequestUri!.AbsolutePath;
        byte[] bytes;
        if (path.EndsWith(".json", StringComparison.Ordinal))
        {
            LastManifestUri = request.RequestUri;
            var isLite = path.EndsWith("/destiny-lite.json", StringComparison.Ordinal);
            if (isLite && LiteUnavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var manifest = new PackManifest { PackId = isLite ? "destiny-lite" : "destiny", Version = $"1.0.{Version}", Files = [File(BadPath ?? "mods/alpha.txt", alpha), File("mods/kept.txt", kept)] };
            if (RemoveKept) manifest.Files.RemoveAt(1);
            bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        }
        else bytes = path.EndsWith("alpha.txt", StringComparison.Ordinal) ? (CorruptDownload ? Enumerable.Repeat((byte)'x', alpha.Length).ToArray() : alpha) : kept;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    private static PackManifestFile File(string path, byte[] data) => new() { Path = path, Url = $"https://fixture.invalid/{path}", Sha256 = Convert.ToHexString(SHA256.HashData(data)), Size = data.Length };
}
