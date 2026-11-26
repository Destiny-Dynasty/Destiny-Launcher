using System.Diagnostics;
using System.Text.Json;
using DestinyLauncher.Services;

if (args.FirstOrDefault() == "--crash-child")
{
    var childPaths = new LauncherPaths(args[1]); childPaths.Ensure();
    var child = await GameProfileSession.BeginAsync(childPaths, "destiny-lite", CancellationToken.None);
    if (args[2] == "active")
    {
        await child.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
        File.WriteAllText(Path.Combine(childPaths.GameDirectory, "config", "lite.txt"), "edited-before-kill");
    }
    File.WriteAllText(args[3], "ready");
    await Task.Delay(TimeSpan.FromSeconds(30));
    await child.CompleteAsync();
    return;
}

var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/storage-smoke");
root = Path.Combine(root, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); }
LauncherPaths Paths(string name) { var paths = new LauncherPaths(Path.Combine(root, name)); paths.Ensure(); return paths; }
void Put(string path, string value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
async Task Reject(Func<Task> action, string message)
{
    try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { checks.Add(message); return; }
    throw new Exception(message);
}
var paths = Paths("profiles");
var world = Path.Combine(paths.GameDirectory, "saves", "world", "level.dat");
Put(world, "player-world");
var editions = new[] { "destiny", "destiny-lite", "destiny-staff" };
foreach (var edition in editions) Put(Path.Combine(paths.ProfileConfigDirectory(edition), "config", "edition.txt"), edition);
for (var index = 0; index < 30; index++)
{
    var edition = editions[index % 3];
    await using var session = await GameProfileSession.BeginAsync(paths, edition, CancellationToken.None);
    Check(File.ReadAllText(Path.Combine(paths.GameDirectory, "config", "edition.txt")) == edition, "Switch " + index + " loads only the selected edition");
    await session.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
    Put(Path.Combine(paths.GameDirectory, "config", "custom.txt"), edition + index);
    await session.CompleteAsync();
    Check(File.ReadAllText(Path.Combine(paths.ProfileConfigDirectory(edition), "config", "custom.txt")) == edition + index, "Switch " + index + " persists player edits");
}
Check(File.ReadAllText(world) == "player-world", "Repeated switches preserve shared worlds");
// A completed game's stale live copy must not undo a newer pack config.
Put(Path.Combine(paths.ProfileConfigDirectory("destiny-staff"), "config", "edition.txt"), "updated-staff");
await using (var updated = await GameProfileSession.BeginAsync(paths, "destiny-staff", CancellationToken.None))
    Check(File.ReadAllText(Path.Combine(paths.GameDirectory, "config", "edition.txt")) == "updated-staff", "Idle recovery does not overwrite a newly updated saved profile");

var rollback = Paths("rollback-failure");
var edits = Paths("idle-manual-edits");
var savedConfig = Path.Combine(edits.ProfileConfigDirectory("destiny"), "config", "manual.txt");
var liveConfig = Path.Combine(edits.GameDirectory, "config", "manual.txt");
Put(savedConfig, "baseline");
Put(Path.Combine(edits.ProfileConfigDirectory("destiny"), "config", "pack.txt"), "old-pack");
await using (var game = await GameProfileSession.BeginAsync(edits, "destiny", CancellationToken.None))
{ await game.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None); await game.CompleteAsync(); }
Put(liveConfig, "manual-edit-after-exit");
Put(Path.Combine(edits.ProfileConfigDirectory("destiny"), "config", "pack.txt"), "updated-pack");
await using (var game = await GameProfileSession.BeginAsync(edits, "destiny", CancellationToken.None))
{
    Check(File.ReadAllText(liveConfig) == "manual-edit-after-exit", "Manual edits after game exit survive reopening a completed profile");
    Check(File.ReadAllText(Path.Combine(edits.GameDirectory, "config", "pack.txt")) == "updated-pack", "Independent pack updates survive reconciliation with manual edits");
    await game.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None); await game.CompleteAsync();
}
Put(liveConfig, "live-conflict"); Put(savedConfig, "saved-conflict");
await Reject(async () => { await using var game = await GameProfileSession.BeginAsync(edits, "destiny", CancellationToken.None); }, "Conflicting manual and pack edits block switching instead of silently choosing a copy");
Check(File.ReadAllText(liveConfig) == "live-conflict" && File.ReadAllText(savedConfig) == "saved-conflict", "A merge conflict preserves both original versions");
Put(liveConfig, "saved-conflict");
await using (var resolved = await GameProfileSession.BeginAsync(edits, "destiny", CancellationToken.None))
    Check(File.ReadAllText(liveConfig) == "saved-conflict", "A manually resolved conflict can be retried normally");
await Reject(() => { StorageSpaceGuard.RequireBytes(edits.GameDirectory, 4096, 0); return Task.CompletedTask; }, "Insufficient disk space is rejected before staging or deleting configuration");
Check(File.ReadAllText(savedConfig) == "saved-conflict", "Disk-space rejection preserves saved configuration");

var live = Path.Combine(rollback.GameDirectory, "config");
Put(Path.Combine(live, "original.txt"), "original");
var pending = await GameProfileSession.BeginAsync(rollback, "destiny-lite", CancellationToken.None);
// A file occupying a directory's name simulates a failed rollback without permission assumptions.
if (Directory.Exists(live)) Directory.Delete(live, true);
Put(live, "blocking-file");
await Reject(() => pending.CompleteAsync(), "Rollback failure is reported");
Check(File.Exists(Path.Combine(rollback.GameDirectory, ".destiny-profile-session.json"))
    && File.Exists(Path.Combine(rollback.GameDirectory, ".destiny-profile-backup", "config", "original.txt")),
    "Rollback failure preserves both recovery journal and original backup");
File.Delete(live);
await using (var recovered = await GameProfileSession.BeginAsync(rollback, "destiny", CancellationToken.None))
    Check(File.ReadAllText(Path.Combine(live, "original.txt")) == "original", "Next launch recovers after a failed rollback");

var invalid = Paths("invalid-journal");
var journal = Path.Combine(invalid.GameDirectory, ".destiny-profile-session.json");
Put(journal, JsonSerializer.Serialize(new { Slug = "destiny", Phase = "unexpected" }));
await Reject(async () => { await using var ignored = await GameProfileSession.BeginAsync(invalid, "destiny", CancellationToken.None); }, "Unknown journal states block profile switching");
Check(File.Exists(journal), "Invalid recovery state is retained for inspection");
var cancelled = Paths("cancelled");
Put(Path.Combine(cancelled.GameDirectory, "config", "original.txt"), "untouched");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    await Reject(async () => { await using var ignored = await GameProfileSession.BeginAsync(cancelled, "destiny-lite", cancel.Token); }, "Cancelled switch does not apply another profile");
}
Check(File.ReadAllText(Path.Combine(cancelled.GameDirectory, "config", "original.txt")) == "untouched", "Cancellation preserves original live configuration");
await using (var retry = await GameProfileSession.BeginAsync(cancelled, "destiny", CancellationToken.None))
    checks.Add("Cancellation releases the shared-directory lock for retry");

var busy = Paths("preparing-uninstall");
Put(Path.Combine(busy.GameDirectory, "destiny-installed.json"), "{}");
Put(Path.Combine(busy.GameDirectory, "saves", "keep", "level.dat"), "untouched");
var busyRemoval = new InstallationDataService(busy);
busyRemoval.Register(busy.GameDirectory);
await using (var preparing = await GameProfileSession.BeginAsync(busy, "destiny", CancellationToken.None))
{
    await Reject(() => { busyRemoval.RemoveSelected(true, true); return Task.CompletedTask; }, "Uninstall cannot race a profile still preparing before Java starts");
    var otherRoot = Paths("second-launcher"); otherRoot.SetInstallDirectory(busy.GameDirectory);
    await Reject(() => { using var ignored = SharedGameDirectoryLock.Acquire(otherRoot, "busy"); return Task.CompletedTask; }, "Different launcher data roots share the same canonical game-directory lock");
    Check(File.ReadAllText(Path.Combine(busy.GameDirectory, "saves", "keep", "level.dat")) == "untouched", "Blocked concurrent uninstall deletes no worlds");
}
var active = Paths("orphaned-game");
Put(Path.Combine(active.GameDirectory, ".destiny-profile-session.json"), JsonSerializer.Serialize(new
{
    Slug = "destiny", Phase = "active", ProcessId = Environment.ProcessId,
    ProcessStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks
}));
await Reject(() => { using var ignored = SharedGameDirectoryLock.Acquire(active, "busy"); return Task.CompletedTask; },
    "A surviving game process blocks writes even after the launcher lock was released");
await Reject(() => new SharedGameDataMigration(active).MigrateAsync(CancellationToken.None),
    "Migration refuses to touch a surviving game's shared directory");
Put(Path.Combine(active.GameDirectory, "destiny-installed.json"), "{}");
Put(Path.Combine(active.GameDirectory, "saves", "protected", "level.dat"), "keep");
var removal = new InstallationDataService(active);
removal.Register(active.GameDirectory);
await Reject(() => { removal.RemoveSelected(true, true); return Task.CompletedTask; },
    "Full uninstall refuses a surviving game before deleting any data");
Check(File.ReadAllText(Path.Combine(active.GameDirectory, "saves", "protected", "level.dat")) == "keep",
    "Rejected uninstall preserves player worlds");
var linked = Paths("linked-tree");
var external = Path.Combine(root, "external-fixture");
Put(Path.Combine(external, "keep.txt"), "external-original");
Directory.CreateDirectory(Path.Combine(linked.Instance("destiny-lite"), "assets"));
try
{
    Directory.CreateSymbolicLink(Path.Combine(linked.Instance("destiny-lite"), "assets", "outside"), external);
    await Reject(() => new SharedGameDataMigration(linked).MigrateAsync(CancellationToken.None), "Migration rejects nested links before moving a complete tree");
    Check(File.ReadAllText(Path.Combine(external, "keep.txt")) == "external-original", "Rejected link migration preserves external data");
}
catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
{ checks.Add("SKIPPED: symlink creation is unavailable: " + ex.GetType().Name); }
catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 1314)
{ checks.Add("SKIPPED: Windows requires additional privileges to create symbolic links"); }

var migration = Paths("migration");
Put(Path.Combine(migration.GameDirectory, "saves", "World", "level.dat"), "normal");
Put(Path.Combine(migration.Instance("destiny-lite"), "saves", "World", "level.dat"), "lite");
Put(Path.Combine(migration.Instance("destiny-staff"), "screenshots", "same.png"), "staff");
Put(Path.Combine(migration.GameDirectory, "screenshots", "same.png"), "normal");
await new SharedGameDataMigration(migration).MigrateAsync(CancellationToken.None);
var migratedFiles = Directory.GetFiles(migration.GameDirectory, "*", SearchOption.AllDirectories).Order().ToArray();
await new SharedGameDataMigration(migration).MigrateAsync(CancellationToken.None);
Check(migratedFiles.SequenceEqual(Directory.GetFiles(migration.GameDirectory, "*", SearchOption.AllDirectories).Order()), "Completed migration is idempotent");
Check(File.ReadAllText(Path.Combine(migration.GameDirectory, "saves", "World (Lite)", "level.dat")) == "lite", "Same-name legacy worlds remain independent");
Check(File.ReadAllText(Path.Combine(migration.GameDirectory, "screenshots", "same (Staff).png")) == "staff", "Conflicting legacy screenshots are preserved");
foreach (var phase in new[] { "prepared", "active" })
{
    var crash = Paths("process-kill-" + phase);
    Put(Path.Combine(crash.GameDirectory, "config", "normal.txt"), "normal-original");
    Put(Path.Combine(crash.ProfileConfigDirectory("destiny-lite"), "config", "lite.txt"), "lite-original");
    var ready = Path.Combine(crash.Root, "ready.txt");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var value in new[] { "--crash-child", crash.Root, phase, ready }) start.ArgumentList.Add(value);
    using var child = Process.Start(start)!;
    try
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(ready) && !child.HasExited && DateTime.UtcNow < deadline) await Task.Delay(20);
        Check(File.Exists(ready), "Child reaches real crash fixture phase " + phase);
        child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
        await using var recovery = await GameProfileSession.BeginAsync(crash, "destiny", CancellationToken.None);
        Check(File.ReadAllText(Path.Combine(crash.GameDirectory, "config", "normal.txt")) == "normal-original", "An actual process kill in phase " + phase + " restores the normal profile");
        if (phase == "active") Check(File.ReadAllText(Path.Combine(crash.ProfileConfigDirectory("destiny-lite"), "config", "lite.txt")) == "edited-before-kill", "An actual killed game preserves its last written profile edits");
    }
    finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
}
if (OperatingSystem.IsLinux())
{
    var casePaths = Paths("case-sensitive");
    Put(Path.Combine(casePaths.ProfileConfigDirectory("destiny"), "config", "Setting.txt"), "upper");
    Put(Path.Combine(casePaths.ProfileConfigDirectory("destiny"), "config", "setting.txt"), "lower");
    await using (var session = await GameProfileSession.BeginAsync(casePaths, "destiny", CancellationToken.None))
    {
        Check(File.ReadAllText(Path.Combine(casePaths.GameDirectory, "config", "Setting.txt")) == "upper"
            && File.ReadAllText(Path.Combine(casePaths.GameDirectory, "config", "setting.txt")) == "lower", "Native Linux keeps case-distinct configuration filenames separate");
        await session.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None); await session.CompleteAsync();
        Check(File.ReadAllText(Path.Combine(casePaths.ProfileConfigDirectory("destiny"), "config", "Setting.txt")) == "upper"
            && File.ReadAllText(Path.Combine(casePaths.ProfileConfigDirectory("destiny"), "config", "setting.txt")) == "lower", "Native Linux capture and idle baselines preserve case-distinct files");
    }
    var danglingTarget = Path.Combine(root, "missing-target", "file.txt");
    var dangling = Path.Combine(root, "dangling-link");
    File.CreateSymbolicLink(dangling, danglingTarget);
    await Reject(() => { InstallationDataService.RejectLinks(dangling); return Task.CompletedTask; }, "Native Linux rejects a dangling symlink before its external target exists");
    Check(!File.Exists(danglingTarget), "Rejecting a dangling link creates no external file");
    var source = Path.Combine("/dev/shm", "destiny-cross-device-" + Guid.NewGuid().ToString("N"));
    var target = Path.Combine(root, "cross-device.txt");
    try
    {
        File.WriteAllText(source, "cross-device-original");
        await SharedGameDataMigration.MoveFileAsync(source, target, CancellationToken.None);
        Check(!File.Exists(source) && File.ReadAllText(target) == "cross-device-original", "Native Linux migration verifies a cross-device copy before removing its source");
    }
    finally { if (File.Exists(source)) File.Delete(source); }
    var names = new[] { "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" };
    var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
    try
    {
        foreach (var name in names) Environment.SetEnvironmentVariable(name, Path.Combine(root, name));
        var xdg = new LauncherPaths(); xdg.SetInstallDirectory(Path.Combine(root, "xdg-game")); xdg.Ensure();
        Put(xdg.ConfigFile, "{}"); Put(Path.Combine(xdg.Cache, "temporary"), "cache"); Put(Path.Combine(xdg.Logs, "temporary"), "log");
        new InstallationDataService(xdg).RemoveSelected(true, false);
        Check(!File.Exists(xdg.ConfigFile) && !Directory.Exists(xdg.Cache) && !Directory.Exists(xdg.Logs), "Native Linux cleanup removes the actual separate XDG config, cache and state directories");
    }
    finally { for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]); }
}
File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, os = Environment.OSVersion.ToString(), checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks.Count} storage checks; {root}");
