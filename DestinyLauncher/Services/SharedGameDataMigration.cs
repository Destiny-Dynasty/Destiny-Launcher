using System.Security.Cryptography;
using System.Text.Json;

namespace DestinyLauncher.Services;

/// <summary>Moves existing player data and reusable Minecraft files into the shared game directory.</summary>
internal sealed class SharedGameDataMigration(LauncherPaths paths)
{
    private static readonly string[] SharedRoots =
    ["saves", "screenshots", "Distant_Horizons_server_data", "resourcepacks", "shaderpacks", "assets", "libraries", "versions", "natives", ".fabric"];

    internal async Task MigrateAsync(CancellationToken token)
    {
        Directory.CreateDirectory(paths.GameDirectory);
        using var gameLock = SharedGameDirectoryLock.Acquire(paths, "Minecraft está usando la carpeta compartida. Cierra el juego antes de migrar los datos de perfil.");
        foreach (var (slug, suffix) in new[] { ("destiny-lite", "Lite"), ("destiny-staff", "Staff") })
        {
            token.ThrowIfCancellationRequested();
            var legacy = paths.Instance(slug);
            if (!Directory.Exists(legacy) || File.Exists(Marker(slug))) continue;
            InstallationDataService.RejectLinks(legacy);
            foreach (var name in SharedRoots)
            {
                token.ThrowIfCancellationRequested();
                var source = Path.Combine(legacy, name);
                var target = Path.Combine(paths.GameDirectory, name);
                if (!Directory.Exists(source)) continue;
                await MergeDirectoryAsync(source, target, suffix, token);
                if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
            }
            await WriteMarkerAsync(slug, token);
        }
    }

    private string Marker(string slug) => Path.Combine(paths.GameDirectory, $".destiny-shared-data-{slug}.json");

    private async Task WriteMarkerAsync(string slug, CancellationToken token)
    {
        var path = Marker(slug);
        var temporary = path + ".next";
        InstallationDataService.RejectLinks(path);
        InstallationDataService.RejectLinks(temporary);
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { edition = slug, migrated = DateTimeOffset.UtcNow }), token);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task MergeDirectoryAsync(string source, string target, string suffix, CancellationToken token)
    {
        GameProfileSession.RejectTreeLinks(source);
        InstallationDataService.RejectLinks(target);
        if (!Directory.Exists(target))
        {
            try { Directory.Move(source, target); return; }
            catch (IOException) when (Directory.Exists(source)) { }
        }
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source).ToArray())
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetFileName(directory));
            InstallationDataService.RejectLinks(directory);
            if (Directory.Exists(destination))
            {
                // Worlds and Distant Horizons server folders are independent units. Keep a
                // colliding old copy intact under a recognizable edition suffix.
                var rootName = Path.GetFileName(source);
                if (rootName.Equals("saves", StringComparison.OrdinalIgnoreCase)
                    || rootName.Equals("Distant_Horizons_server_data", StringComparison.OrdinalIgnoreCase))
                    destination = UniquePath(target, Path.GetFileName(directory), suffix, directory: true);
            }
            await MergeDirectoryAsync(directory, destination, suffix, token);
        }
        foreach (var file in Directory.EnumerateFiles(source).ToArray())
        {
            token.ThrowIfCancellationRequested();
            InstallationDataService.RejectLinks(file);
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                if (await SameFileAsync(file, destination, token)) { File.Delete(file); continue; }
                destination = UniquePath(target, Path.GetFileName(file), suffix, directory: false);
            }
            await MoveFileAsync(file, destination, token);
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    private static async Task<bool> SameFileAsync(string left, string right, CancellationToken token)
    {
        var a = new FileInfo(left); var b = new FileInfo(right);
        if (a.Length != b.Length) return false;
        await using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var leftHash = await SHA256.HashDataAsync(leftStream, token);
        var rightHash = await SHA256.HashDataAsync(rightStream, token);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static string UniquePath(string dirPath, string name, string suffix, bool directory)
    {
        var extension = directory ? "" : Path.GetExtension(name);
        var stem = directory ? name : Path.GetFileNameWithoutExtension(name);
        var candidate = Path.Combine(dirPath, $"{stem} ({suffix}){extension}");
        for (var index = 2; Directory.Exists(candidate) || File.Exists(candidate); index++)
            candidate = Path.Combine(dirPath, $"{stem} ({suffix} {index}){extension}");
        return candidate;
    }

    internal static async Task MoveFileAsync(string source, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InstallationDataService.RejectLinks(source);
        InstallationDataService.RejectLinks(target);
        try { File.Move(source, target); }
        catch (IOException) when (File.Exists(source) && !File.Exists(target) && !Directory.Exists(target))
        {
            // Unix mount points can be on different devices under the same '/' root.
            // Copy, verify and commit before removing the source on a cross-device move.
            StorageSpaceGuard.RequireBytes(Path.GetDirectoryName(target)!, new FileInfo(source).Length);
            var temporary = target + ".migrating-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, false);
                if (!await SameFileAsync(source, temporary, token)) throw new IOException("La copia de migración no coincide con el archivo original.");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target);
                File.Delete(source);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
