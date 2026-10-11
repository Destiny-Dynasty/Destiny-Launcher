using DestinyLauncher.Models;
using DestinyLauncher.Services;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PackUpdaterSmoke <v1-manifest-url> <v2-manifest-url>");
    return 2;
}

var tempRoot = Path.Combine(Path.GetTempPath(), $"destiny-pack-smoke-{Guid.NewGuid():N}");
try
{
    var paths = new LauncherPaths(tempRoot);
    var logger = new AppLogger(paths);
    var updater = new GitHubPackUpdater(paths, logger);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var pack = new PackDefinition { Name = "Destiny test fixture", Slug = "destiny", ManifestUrl = args[0] };

    var initial = await updater.CheckAsync(pack, null, cancellation.Token);
    Require(initial.ChangedFiles.Count == 2, $"V1 should install two fixture files, got {initial.ChangedFiles.Count}.");
    await updater.ApplyAsync(pack, initial, null, cancellation.Token);

    var alreadyCurrent = await updater.CheckAsync(pack, null, cancellation.Token);
    Require(!alreadyCurrent.HasChanges, "A second V1 check should not find downloads or removals.");

    var instance = paths.Instance(pack.Slug);
    var preservedFile = Path.Combine(instance, "mods", "kept.txt");
    await File.WriteAllTextAsync(preservedFile, "local player edit", cancellation.Token);

    pack.ManifestUrl = args[1];
    var update = await updater.CheckAsync(pack, null, cancellation.Token);
    Require(update.ChangedFiles.Count == 1, $"V2 should download exactly one changed file, got {update.ChangedFiles.Count}.");
    Require(update.ChangedFiles[0].Path == "mods/alpha.txt", "V2 should change only mods/alpha.txt.");
    Require(update.PreservedOverrides.Contains("mods/kept.txt", StringComparer.OrdinalIgnoreCase), "The local edit should be recognized as an override.");
    await updater.ApplyAsync(pack, update, null, cancellation.Token);

    var alpha = await File.ReadAllTextAsync(Path.Combine(instance, "mods", "alpha.txt"), cancellation.Token);
    var kept = await File.ReadAllTextAsync(preservedFile, cancellation.Token);
    Require(alpha.Trim() == "alpha fixture version 2", "The changed fixture file was not updated to V2.");
    Require(kept == "local player edit", "The local override was overwritten.");

    var final = await updater.CheckAsync(pack, null, cancellation.Token);
    Require(!final.HasChanges, "A final V2 check should find no remaining pack changes.");
    Require(final.PreservedOverrides.Contains("mods/kept.txt", StringComparer.OrdinalIgnoreCase), "The local override should remain tracked.");

    Console.WriteLine("PASS: V1 install, no-op recheck, one-file V2 update, local edit preserved, final hashes current.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.Message}");
    return 1;
}
finally
{
    try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
    catch { /* Temporary test cleanup must not hide the result. */ }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
