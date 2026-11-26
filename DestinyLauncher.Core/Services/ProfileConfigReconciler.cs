using System.Security.Cryptography;
using System.Text.Json;

namespace DestinyLauncher.Services;

internal static class ProfileConfigReconciler
{
    private static readonly string[] Roots = ["config", "defaultconfigs", "kubejs", "scripts"];
    private sealed record Baseline(string Slug, Dictionary<string, string> Files);
    private static string Marker(LauncherPaths paths) => Path.Combine(paths.GameDirectory, ".destiny-profile-idle-baseline.json");

    internal static async Task SnapshotAsync(LauncherPaths paths, string slug, CancellationToken token)
    {
        var marker = Marker(paths);
        InstallationDataService.RejectLinks(marker);
        var next = marker + ".next";
        InstallationDataService.RejectLinks(next);
        try
        {
            await File.WriteAllTextAsync(next, JsonSerializer.Serialize(new Baseline(slug, await HashTreeAsync(paths.GameDirectory, token))), token);
            File.Move(next, marker, true);
        }
        finally { if (File.Exists(next)) File.Delete(next); }
    }

    internal static async Task ReconcileAsync(LauncherPaths paths, string slug, CancellationToken token)
    {
        var marker = Marker(paths);
        InstallationDataService.RejectLinks(marker);
        if (!File.Exists(marker)) return; // Older builds have no trustworthy comparison baseline.
        var baseline = JsonSerializer.Deserialize<Baseline>(await File.ReadAllTextAsync(marker, token));
        if (baseline is null || baseline.Slug != slug) return;
        var live = await HashTreeAsync(paths.GameDirectory, token);
        var profile = paths.ProfileConfigDirectory(slug);
        var saved = await HashTreeAsync(profile, token);
        var changes = new List<string>();
        foreach (var relative in baseline.Files.Keys.Concat(live.Keys).Distinct(FileSystemPathPolicy.Comparer))
        {
            baseline.Files.TryGetValue(relative, out var previous);
            live.TryGetValue(relative, out var current);
            saved.TryGetValue(relative, out var edited);
            if (current == previous || current == edited) continue;
            if (edited != previous)
                throw new ProfileConfigurationConflictException(relative);
            changes.Add(relative);
        }
        // Validate every conflict before writing anything. On an I/O failure, a retry
        // recognizes files already synchronized, without discarding either newer copy.
        foreach (var relative in changes)
        {
            token.ThrowIfCancellationRequested();
            var source = SafePath(paths.GameDirectory, relative);
            var target = SafePath(profile, relative);
            if (!live.ContainsKey(relative)) { if (File.Exists(target)) File.Delete(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var next = target + ".destiny-merge-" + Guid.NewGuid().ToString("N");
            try { File.Copy(source, next); token.ThrowIfCancellationRequested(); File.Move(next, target, true); }
            finally { if (File.Exists(next)) File.Delete(next); }
        }
    }

    private static string SafePath(string directory, string relative)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(root, FileSystemPathPolicy.Comparison) || !Roots.Contains(relative.Split('/', '\\')[0], StringComparer.Ordinal))
            throw new InvalidDataException("Ruta de configuración no permitida.");
        InstallationDataService.RejectLinks(result);
        return result;
    }

    private static async Task<Dictionary<string, string>> HashTreeAsync(string directory, CancellationToken token)
    {
        var files = new Dictionary<string, string>(FileSystemPathPolicy.Comparer);
        foreach (var root in Roots)
        {
            var path = Path.Combine(directory, root);
            GameProfileSession.RejectTreeLinks(path);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                await using var stream = File.OpenRead(file);
                files.Add(Path.GetRelativePath(directory, file).Replace('\\', '/'), Convert.ToHexString(await SHA256.HashDataAsync(stream, token)));
            }
        }
        return files;
    }
}

internal sealed class ProfileConfigurationConflictException(string relative) : IOException(
    $"Se modificó «{relative}» tanto en la carpeta compartida como en el perfil guardado. Se han conservado ambas copias. Abre la configuración del perfil y conserva la versión deseada antes de volver a jugar.");
