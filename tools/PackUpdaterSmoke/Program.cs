using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

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
    Require(fixture.LastManifestUri?.AbsoluteUri == "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack-Lite/main/manifests/destiny-lite.json", "Lite must use its independent repository.");
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
