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
    var initial = await updater.CheckAsync(pack, null, token);
    Require(fixture.LastManifestUri?.AbsoluteUri == "https://raw.githubusercontent.com/Destiny-Dynasty/Modpack/main/manifests/destiny.json", "The full pack must keep its existing repository.");
    Require(initial.ChangedFiles.Count == 2, "Initial installation must download two fixture files.");
    await updater.ApplyAsync(pack, initial, null, token);
    Require(!(await updater.CheckAsync(pack, null, token)).HasChanges, "Rechecking must be a no-op.");
    var instance = paths.Instance(pack.Slug);
    var kept = Path.Combine(instance, "mods", "kept.txt");
    await File.WriteAllTextAsync(kept, "local player edit", token);
    Directory.CreateDirectory(Path.Combine(instance, "saves"));
    var personal = Path.Combine(instance, "saves", "world.txt");
    await File.WriteAllTextAsync(personal, "personal world", token);
    fixture.Version = 2;
    var update = await updater.CheckAsync(pack, null, token);
    Require(update.ChangedFiles.Count == 1 && update.ChangedFiles[0].Path == "mods/alpha.txt", "Only the changed managed file should download.");
    Require(update.PreservedOverrides.Contains("mods/kept.txt", StringComparer.OrdinalIgnoreCase), "Player edits must be identified.");
    await updater.ApplyAsync(pack, update, null, token);
    Require(await File.ReadAllTextAsync(Path.Combine(instance, "mods", "alpha.txt"), token) == "alpha fixture version 2", "The update was not applied.");
    Require(await File.ReadAllTextAsync(kept, token) == "local player edit", "The player edit was overwritten.");
    Require(!(await updater.CheckAsync(pack, null, token)).HasChanges, "The updated files should be current.");
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
    public Uri? LastManifestUri { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var alpha = Encoding.UTF8.GetBytes($"alpha fixture version {Version}");
        var kept = Encoding.UTF8.GetBytes("kept fixture version 1");
        var path = request.RequestUri!.AbsolutePath;
        byte[] bytes;
        if (path.EndsWith(".json", StringComparison.Ordinal))
        {
            LastManifestUri = request.RequestUri;
            var isLite = path.EndsWith("/destiny-lite.json", StringComparison.Ordinal);
            if (isLite && LiteUnavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var manifest = new PackManifest { PackId = isLite ? "destiny-lite" : "destiny", Version = $"1.0.{Version}", Files = [File(BadPath ?? "mods/alpha.txt", alpha), File("mods/kept.txt", kept)] };
            bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        }
        else bytes = path.EndsWith("alpha.txt", StringComparison.Ordinal) ? (CorruptDownload ? Enumerable.Repeat((byte)'x', alpha.Length).ToArray() : alpha) : kept;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    private static PackManifestFile File(string path, byte[] data) => new() { Path = path, Url = $"https://fixture.invalid/{path}", Sha256 = Convert.ToHexString(SHA256.HashData(data)), Size = data.Length };
}
