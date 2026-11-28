using System.Diagnostics;
using System.Text.Json;

namespace DestinyLauncher.Services;

/// <summary>
/// Applies one edition's mutable configuration to the shared Minecraft directory and saves
/// the resulting files back to that edition after Java exits. A journal lets the next launch
/// recover a launcher crash without applying one edition over another.
/// </summary>
internal sealed class GameProfileSession : IAsyncDisposable
{
    private static readonly string[] Roots = ["config", "defaultconfigs", "kubejs", "scripts"];
    private readonly LauncherPaths _paths;
    private readonly string _slug;
    private readonly string _journal;
    private readonly string _backup;
    private readonly FileStream _lock;
    private bool _processStarted;
    private bool _completed;
    private bool _ownsJournal;

    private sealed record Journal(string Slug, string Phase, int ProcessId = 0, long ProcessStartUtcTicks = 0);
    private sealed record InPlaceBackup(int Schema, bool? Ready);

    private GameProfileSession(LauncherPaths paths, string slug, FileStream fileLock)
    {
        _paths = paths;
        _slug = slug;
        _lock = fileLock;
        _journal = Path.Combine(paths.GameDirectory, ".destiny-profile-session.json");
        _backup = Path.Combine(paths.GameDirectory, ".destiny-profile-backup");
    }

    internal static async Task<GameProfileSession> BeginAsync(LauncherPaths paths, string slug, CancellationToken token)
    {
        if (slug is not ("destiny" or "destiny-lite" or "destiny-staff")) throw new InvalidOperationException("Variante de modpack desconocida.");
        var fileLock = SharedGameDirectoryLock.Acquire(paths, "Ya hay otra sesión de Minecraft usando esta carpeta compartida. Cierra esa partida antes de cambiar de perfil.");

        var session = new GameProfileSession(paths, slug, fileLock);
        try
        {
            var previous = await session.ReadJournalAsync(token);
            await session.RecoverAsync(token);
            if (previous is null || previous.Slug == "destiny" || previous.Phase is not ("active" or "idle"))
                await SeedProfileConfigAsync(paths, "destiny", token);
            else
                await SeedProfileConfigAsync(paths, "destiny", token, allowLegacySeed: false);
            await SeedProfileConfigAsync(paths, slug, token);
            await session.WriteJournalAsync(new Journal(slug, "preparing"), token);
            session._ownsJournal = true;
            await session.ApplyAsync(token);
            await session.WriteJournalAsync(new Journal(slug, "prepared"), token);
            return session;
        }
        catch
        {
            if (session._ownsJournal)
            {
                // Keep the recovery journal and backup if rollback itself fails.
                try { await session.RestoreBackupAsync(CancellationToken.None); session.DeleteJournal(); } catch { }
                try { var staging = Path.Combine(paths.GameDirectory, ".destiny-profile-staging"); if (Directory.Exists(staging)) DeleteTree(staging); } catch { }
            }
            await fileLock.DisposeAsync();
            throw;
        }
    }

    internal async Task AttachProcessAsync(Process process, CancellationToken token)
    {
        var startTicks = process.StartTime.ToUniversalTime().Ticks;
        await WriteJournalAsync(new Journal(_slug, "active", process.Id, startTicks), token);
        _processStarted = true;
    }

    internal async Task CompleteAsync()
    {
        if (_completed) return;
        _completed = true;
        try
        {
            if (_processStarted) await CaptureAsync(CancellationToken.None);
            else await RestoreBackupAsync(CancellationToken.None);
            if (_processStarted)
                await WriteJournalAsync(new Journal(_slug, "idle"), CancellationToken.None);
            else
            {
                DeleteJournal();
                _ownsJournal = false;
            }
            DeleteBackup();
        }
        finally
        {
            await _lock.DisposeAsync();
        }
    }

    private async Task RecoverAsync(CancellationToken token)
    {
        if (File.Exists(_journal))
        {
            InstallationDataService.RejectLinks(_journal);
            var journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllTextAsync(_journal, token))
                ?? throw new InvalidDataException("El registro de configuración del perfil está vacío.");
            if (journal.Slug is not ("destiny" or "destiny-lite" or "destiny-staff")) throw new InvalidDataException("El registro de configuración contiene un perfil desconocido.");
            if (journal.Phase is not ("preparing" or "prepared" or "active" or "idle")) throw new InvalidDataException("El registro de configuración contiene un estado desconocido.");
            if (journal.Phase == "active" && IsProcessRunning(journal))
                throw new InvalidOperationException("Minecraft sigue abierto con otro perfil. Cierra el juego antes de cambiar de perfil.");
            if (journal.Phase == "active") await CaptureAsync(token, journal.Slug);
            else if (journal.Phase == "idle") await ProfileConfigReconciler.ReconcileAsync(_paths, journal.Slug, token);
            else await RestoreBackupAsync(token);
            DeleteBackup();
            DeleteJournal();
            _ownsJournal = false;
        }
        else if (Directory.Exists(_backup))
        {
            // A crash between creating the backup and writing the next journal state.
            await RestoreBackupAsync(token);
        }
    }

    private static bool IsProcessRunning(Journal journal)
    {
        if (journal.ProcessId <= 0 || journal.ProcessStartUtcTicks <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(journal.ProcessId);
            return Math.Abs(process.StartTime.ToUniversalTime().Ticks - journal.ProcessStartUtcTicks) < TimeSpan.FromSeconds(2).Ticks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    internal static void EnsureNoRunningGame(LauncherPaths paths)
    {
        var path = Path.Combine(paths.GameDirectory, ".destiny-profile-session.json");
        InstallationDataService.RejectLinks(path);
        if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path))
            ?? throw new InvalidDataException("El registro de configuración está vacío.");
        if (journal.Phase == "active" && IsProcessRunning(journal))
            throw new InvalidOperationException("Minecraft sigue abierto. Cierra el juego antes de modificar la carpeta compartida.");
    }

    internal static async Task SeedProfileConfigAsync(LauncherPaths paths, string slug, CancellationToken token, bool allowLegacySeed = true)
    {
        var profileRoot = paths.ProfileConfigDirectory(slug);
        InstallationDataService.RejectLinks(profileRoot);
        Directory.CreateDirectory(profileRoot);
        var marker = Path.Combine(profileRoot, ".seeded-v1");
        InstallationDataService.RejectLinks(marker);
        if (File.Exists(marker)) return;
        var journalPath = Path.Combine(paths.GameDirectory, ".destiny-profile-session.json");
        if (slug == "destiny" && File.Exists(journalPath))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllTextAsync(journalPath, token));
                if (journal is { Slug: not "destiny", Phase: "active" or "idle" }) allowLegacySeed = false;
            }
            catch (JsonException) { }
        }
        foreach (var root in Roots)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(profileRoot, root);
            if (!allowLegacySeed) continue;
            var legacyRoot = Path.GetFullPath(paths.LegacyInstance(slug));
            if (legacyRoot.Equals(Path.GetFullPath(profileRoot), FileSystemPathPolicy.Comparison)) continue;
            var legacy = Path.Combine(legacyRoot, root);
            if (Directory.Exists(legacy)) CopyMissingTree(legacy, target);
        }
        await File.WriteAllTextAsync(marker, "1", token);
    }

    private async Task<Journal?> ReadJournalAsync(CancellationToken token)
    {
        if (!File.Exists(_journal)) return null;
        InstallationDataService.RejectLinks(_journal);
        var content = await File.ReadAllTextAsync(_journal, token);
        return JsonSerializer.Deserialize<Journal>(content);
    }

    private static void CopyMissingTree(string source, string target)
    {
        InstallationDataService.RejectLinks(source);
        InstallationDataService.RejectLinks(target);
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            InstallationDataService.RejectLinks(directory);
            CopyMissingTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            InstallationDataService.RejectLinks(file);
            var destination = Path.Combine(target, Path.GetFileName(file));
            InstallationDataService.RejectLinks(destination);
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
    }

    private async Task ApplyAsync(CancellationToken token)
    {
        var profileRoot = _paths.ProfileConfigDirectory(_slug);
        var plans = new List<ProfileConfigFileSync.Plan>();
        foreach (var root in Roots)
        {
            var plan = await ProfileConfigFileSync.PlanAsync(Path.Combine(profileRoot, root), Path.Combine(_paths.GameDirectory, root), token);
            if (plan.HasChanges) plans.Add(plan);
        }
        // Most launches already have exactly this profile in place. In particular, a
        // reader without delete sharing must not force us to rename its entire folder.
        if (plans.Count == 0) return;
        var backupBytes = Roots.Sum(root => StorageSpaceGuard.TreeBytes(Path.Combine(_paths.GameDirectory, root)));
        StorageSpaceGuard.RequireBytes(_paths.GameDirectory, checked(backupBytes + plans.Sum(plan => plan.RequiredBytes)));
        DeleteBackup();
        Directory.CreateDirectory(_backup);
        await WriteBackupStateAsync(ready: false, token);
        foreach (var root in Roots)
        {
            token.ThrowIfCancellationRequested();
            var live = Path.Combine(_paths.GameDirectory, root);
            if (Directory.Exists(live)) CopyTree(live, Path.Combine(_backup, root));
        }
        // A partial copy is never used as a restore source. The durable ready marker
        // is written before the first live file changes, so a killed process can recover.
        await WriteBackupStateAsync(ready: true, token);
        foreach (var plan in plans) ProfileConfigFileSync.Apply(plan, token);
    }

    private async Task CaptureAsync(CancellationToken token, string? slug = null)
    {
        var profileRoot = _paths.ProfileConfigDirectory(slug ?? _slug);
        InstallationDataService.RejectLinks(profileRoot);
        Directory.CreateDirectory(profileRoot);
        var plans = new List<ProfileConfigFileSync.Plan>();
        foreach (var root in Roots)
        {
            var plan = await ProfileConfigFileSync.PlanAsync(Path.Combine(_paths.GameDirectory, root), Path.Combine(profileRoot, root), token);
            if (plan.HasChanges) plans.Add(plan);
        }
        StorageSpaceGuard.RequireBytes(profileRoot, plans.Sum(plan => plan.RequiredBytes));
        // The live files remain authoritative until capture completes and the journal
        // becomes idle. An interrupted capture is therefore safe to repeat file by file.
        foreach (var plan in plans) ProfileConfigFileSync.Apply(plan, token);
        var staging = Path.Combine(profileRoot, ".capture-staging");
        if (Directory.Exists(staging)) DeleteTree(staging);
        await ProfileConfigReconciler.SnapshotAsync(_paths, slug ?? _slug, token);
    }

    private async Task RestoreBackupAsync(CancellationToken token)
    {
        var stateFile = Path.Combine(_backup, "in-place.json");
        InstallationDataService.RejectLinks(stateFile);
        if (File.Exists(stateFile))
        {
            var state = JsonSerializer.Deserialize<InPlaceBackup>(await File.ReadAllTextAsync(stateFile, token));
            if (state is null || state.Schema != 1 || state.Ready is null) throw new InvalidDataException("El respaldo de configuración tiene un formato desconocido.");
            if (state.Ready == true)
                foreach (var root in Roots)
                    await ProfileConfigFileSync.SynchronizeAsync(Path.Combine(_backup, root), Path.Combine(_paths.GameDirectory, root), token);
            DeleteBackup();
            return;
        }
        // Backward-compatible recovery for the directory-move backups written by 1.2.0.
        string[]? existing = null;
        var manifest = Path.Combine(_backup, "existing-roots.json");
        InstallationDataService.RejectLinks(manifest);
        if (File.Exists(manifest))
            existing = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(manifest, token)) ?? [];
        foreach (var root in Roots)
        {
            token.ThrowIfCancellationRequested();
            var backup = Path.Combine(_backup, root);
            var live = Path.Combine(_paths.GameDirectory, root);
            if (Directory.Exists(backup))
                await ProfileConfigFileSync.SynchronizeAsync(backup, live, token);
            else if (existing is not null && !existing.Contains(root, StringComparer.Ordinal) && Directory.Exists(live))
                await ProfileConfigFileSync.SynchronizeAsync(backup, live, token);
        }
        DeleteBackup();
    }

    private async Task WriteBackupStateAsync(bool ready, CancellationToken token)
    {
        var target = Path.Combine(_backup, "in-place.json");
        var next = target + ".next";
        InstallationDataService.RejectLinks(target);
        InstallationDataService.RejectLinks(next);
        await File.WriteAllTextAsync(next, JsonSerializer.Serialize(new InPlaceBackup(1, ready)), token);
        File.Move(next, target, overwrite: true);
    }

    private async Task WriteJournalAsync(Journal journal, CancellationToken token)
    {
        var temporary = _journal + ".next";
        InstallationDataService.RejectLinks(_journal);
        InstallationDataService.RejectLinks(temporary);
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(journal), token);
            File.Move(temporary, _journal, true);
            _ownsJournal = true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void DeleteJournal()
    {
        if (File.Exists(_journal)) { InstallationDataService.RejectLinks(_journal); File.Delete(_journal); }
        var next = _journal + ".next";
        if (File.Exists(next)) { InstallationDataService.RejectLinks(next); File.Delete(next); }
    }

    private void DeleteBackup() { if (Directory.Exists(_backup)) DeleteTree(_backup); }
    private static void DeleteTree(string path)
    {
        RejectTreeLinks(path);
        // These are private backup/staging trees. Copies of read-only user files
        // must not prevent their disposal; the user's live attributes stay untouched.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, true);
    }

    internal static void RejectTreeLinks(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        InstallationDataService.RejectLinks(path);
        if (Directory.Exists(path))
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) RejectTreeLinks(child);
    }

    private static void CopyTree(string source, string target)
    {
        InstallationDataService.RejectLinks(source);
        InstallationDataService.RejectLinks(target);
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            InstallationDataService.RejectLinks(directory);
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            InstallationDataService.RejectLinks(file);
            var destination = Path.Combine(target, Path.GetFileName(file));
            InstallationDataService.RejectLinks(destination);
            File.Copy(file, destination, true);
        }
    }

    public async ValueTask DisposeAsync() => await CompleteAsync();
}
