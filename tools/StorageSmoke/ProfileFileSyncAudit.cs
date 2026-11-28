using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DestinyLauncher.Services;
using Microsoft.Win32.SafeHandles;

internal static class ProfileFileSyncAudit
{
    internal static async Task Run(string root, List<string> checks)
    {
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); checks.Add(message); }
        LauncherPaths Paths(string name) { var paths = new LauncherPaths(Path.Combine(root, name)); paths.Ensure(); return paths; }
        void Put(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text); }
        string Live(LauncherPaths paths, string file) => Path.Combine(paths.GameDirectory, "config", file);
        string Saved(LauncherPaths paths, string slug, string file) => Path.Combine(paths.ProfileConfigDirectory(slug), "config", file);

        var open = Paths("open-configurations");
        var live = Live(open, "unchanged.properties");
        var saved = Saved(open, "destiny", "unchanged.properties");
        Put(live, "keep=1"); Put(saved, "keep=1");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(live, stamp); File.SetLastWriteTimeUtc(saved, stamp);
        var originalAttributes = File.GetAttributes(live);
        File.SetAttributes(live, originalAttributes | FileAttributes.ReadOnly);
        using (var liveDirectory = OpenDirectory(Path.GetDirectoryName(live)!))
        using (var savedDirectory = OpenDirectory(Path.GetDirectoryName(saved)!))
        using (var liveReader = new FileStream(live, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var savedReader = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await using var session = await GameProfileSession.BeginAsync(open, "destiny", CancellationToken.None);
            Check(!Directory.Exists(Path.Combine(open.GameDirectory, ".destiny-profile-backup")), "Identical configuration needs no backup or folder replacement, even with open files and directories");
            await session.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
            await session.CompleteAsync();
            Check(File.GetLastWriteTimeUtc(live) == stamp && File.GetLastWriteTimeUtc(saved) == stamp,
                "Reopening and capturing an unchanged profile leaves open files and their timestamps untouched");
            Check((File.GetAttributes(live) & FileAttributes.ReadOnly) != 0, "An unchanged read-only configuration is retained without changing its attributes");
        }
        File.SetAttributes(live, originalAttributes);

        // Only the edition-specific file changes; a common open file must stay usable.
        Put(Saved(open, "destiny-lite", "unchanged.properties"), "keep=1");
        Put(Saved(open, "destiny-lite", "edition.properties"), "edition=lite");
        using (var liveDirectory = OpenDirectory(Path.GetDirectoryName(live)!))
        using (var commonReader = new FileStream(live, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            await using var session = await GameProfileSession.BeginAsync(open, "destiny-lite", CancellationToken.None);
            Check(File.ReadAllText(Live(open, "edition.properties")) == "edition=lite" && File.GetLastWriteTimeUtc(live) == stamp,
                "A different profile updates only changed files while the shared folder and an unchanged file remain open");
            using var savedDirectory = OpenDirectory(Path.GetDirectoryName(Saved(open, "destiny-lite", "edition.properties"))!);
            Put(Live(open, "edition.properties"), "edition=lite-edited");
            await session.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None);
            await session.CompleteAsync();
            Check(File.ReadAllText(Saved(open, "destiny-lite", "edition.properties")) == "edition=lite-edited",
                "Game edits are saved without replacing an open saved-profile folder");
        }

        // Failure after the first successful replacement must restore only changed bytes.
        // An unchanged locked file must not make rollback fail as well.
        if (OperatingSystem.IsWindows())
        {
            var blocked = Paths("changed-file-open");
            Put(Live(blocked, "a.properties"), "old-a"); Put(Live(blocked, "z.properties"), "old-z");
            Put(Saved(blocked, "destiny-lite", "a.properties"), "new-a"); Put(Saved(blocked, "destiny-lite", "z.properties"), "new-z");
            using (var locked = new FileStream(Live(blocked, "z.properties"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                try { await using var unexpected = await GameProfileSession.BeginAsync(blocked, "destiny-lite", CancellationToken.None); throw new Exception("Changed locked file was silently skipped"); }
                catch (ProfileConfigurationAccessException ex)
                { Check(ex.Message.Contains("z.properties"), "A genuinely locked changed file is named in the actionable error"); }
                Check(File.ReadAllText(Live(blocked, "a.properties")) == "old-a" && File.ReadAllText(Live(blocked, "z.properties")) == "old-z",
                    "A blocked second replacement rolls back the first without touching the locked original");
                Check(!Directory.Exists(Path.Combine(blocked.GameDirectory, ".destiny-profile-backup"))
                    && !File.Exists(Path.Combine(blocked.GameDirectory, ".destiny-profile-session.json")), "Successful rollback cleans its recovery state after a file lock");
            }
            await using (var retry = await GameProfileSession.BeginAsync(blocked, "destiny-lite", CancellationToken.None))
                Check(File.ReadAllText(Live(blocked, "z.properties")) == "new-z", "Closing a changed file allows a complete, safe retry");
        }

        var readOnlyBackup = Paths("read-only-backup");
        Put(Live(readOnlyBackup, "read-only.properties"), "original");
        Put(Saved(readOnlyBackup, "destiny-lite", "read-only.properties"), "original");
        Put(Saved(readOnlyBackup, "destiny-lite", "added.properties"), "new");
        var readOnlyFile = Live(readOnlyBackup, "read-only.properties");
        File.SetAttributes(readOnlyFile, File.GetAttributes(readOnlyFile) | FileAttributes.ReadOnly);
        await using (var session = await GameProfileSession.BeginAsync(readOnlyBackup, "destiny-lite", CancellationToken.None))
        { await session.AttachProcessAsync(Process.GetCurrentProcess(), CancellationToken.None); await session.CompleteAsync(); }
        Check((File.GetAttributes(readOnlyFile) & FileAttributes.ReadOnly) != 0 && !Directory.Exists(Path.Combine(readOnlyBackup.GameDirectory, ".destiny-profile-backup")),
            "Internal backup cleanup handles copies of read-only files without modifying live attributes");

        var shape = Paths("file-directory-collisions");
        Put(Live(shape, "a/nested.properties"), "old-tree"); Put(Live(shape, "b"), "old-file");
        Put(Saved(shape, "destiny-lite", "a"), "new-file"); Put(Saved(shape, "destiny-lite", "b/nested.properties"), "new-tree");
        await using (var session = await GameProfileSession.BeginAsync(shape, "destiny-lite", CancellationToken.None))
            Check(File.ReadAllText(Live(shape, "a")) == "new-file" && File.ReadAllText(Live(shape, "b/nested.properties")) == "new-tree",
                "Necessary file/directory type changes apply in place without retaining the previous profile's files");
        Check(File.ReadAllText(Live(shape, "a/nested.properties")) == "old-tree" && File.ReadAllText(Live(shape, "b")) == "old-file",
            "Rollback restores original file/directory types after preparation without a game start");

        // Recover backups from the already-published directory-move implementation.
        var legacy = Paths("legacy-open-folder-recovery");
        var legacyBackup = Path.Combine(legacy.GameDirectory, ".destiny-profile-backup");
        Put(Path.Combine(legacyBackup, "config", "original.properties"), "original");
        Put(Path.Combine(legacyBackup, "existing-roots.json"), "[\"config\",\"scripts\"]");
        Put(Live(legacy, "other-profile.properties"), "interrupted");
        Put(Path.Combine(legacy.GameDirectory, "scripts", "unmoved.js"), "unmoved-original");
        Put(Path.Combine(legacy.GameDirectory, ".destiny-profile-session.json"), JsonSerializer.Serialize(new { Slug = "destiny-lite", Phase = "prepared" }));
        using (var directory = OpenDirectory(Path.Combine(legacy.GameDirectory, "config")))
        {
            await using var recovery = await GameProfileSession.BeginAsync(legacy, "destiny", CancellationToken.None);
            Check(File.ReadAllText(Live(legacy, "original.properties")) == "original" && !File.Exists(Live(legacy, "other-profile.properties")),
                "A 1.2.0 recovery backup is restored while the live config folder is open");
            Check(File.ReadAllText(Path.Combine(legacy.GameDirectory, "scripts", "unmoved.js")) == "unmoved-original",
                "Partial legacy backups preserve roots that were never moved");
        }

        foreach (var ready in new[] { false, true })
        {
            var recover = Paths("copy-recovery-" + ready);
            var backup = Path.Combine(recover.GameDirectory, ".destiny-profile-backup");
            Put(Live(recover, "original.properties"), ready ? "half-applied" : "complete-live");
            Put(Path.Combine(backup, "config", "original.properties"), "backup-original");
            Put(Path.Combine(backup, "in-place.json"), JsonSerializer.Serialize(new { Schema = 1, Ready = ready }));
            if (ready) Put(Live(recover, "interrupted.properties.destiny-sync-fixture"), "partial-write");
            Put(Path.Combine(recover.GameDirectory, ".destiny-profile-session.json"), JsonSerializer.Serialize(new { Slug = "destiny-lite", Phase = "preparing" }));
            await using var session = await GameProfileSession.BeginAsync(recover, "destiny", CancellationToken.None);
            Check(File.ReadAllText(Live(recover, "original.properties")) == (ready ? "backup-original" : "complete-live"),
                ready ? "A durable ready snapshot restores a partially applied file transaction" : "An interrupted backup copy never replaces complete live data with an incomplete snapshot");
            if (ready) Check(!File.Exists(Live(recover, "interrupted.properties.destiny-sync-fixture")), "Recovery removes interrupted atomic-write files without discarding original configuration");
        }

        var malformed = Paths("malformed-copy-backup");
        var retainedBackup = Path.Combine(malformed.GameDirectory, ".destiny-profile-backup");
        Put(Path.Combine(retainedBackup, "config", "original.properties"), "original-backup");
        Put(Path.Combine(retainedBackup, "in-place.json"), "{\"Schema\":1}");
        Put(Path.Combine(malformed.GameDirectory, ".destiny-profile-session.json"), JsonSerializer.Serialize(new { Slug = "destiny", Phase = "preparing" }));
        try { await using var unexpected = await GameProfileSession.BeginAsync(malformed, "destiny", CancellationToken.None); throw new Exception("Malformed backup was accepted"); }
        catch (InvalidDataException) { Check(File.Exists(Path.Combine(retainedBackup, "config", "original.properties")), "Incomplete recovery metadata is rejected while its original backup is retained"); }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
            handle = CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        else if (OperatingSystem.IsLinux())
            handle = new SafeFileHandle(new IntPtr(Open(path, 0x10000)), ownsHandle: true);
        else throw new PlatformNotSupportedException();
        if (handle.IsInvalid) throw new IOException("Cannot open directory fixture", Marshal.GetLastPInvokeError());
        return handle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
}
