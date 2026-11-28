using System.Security.Cryptography;

namespace DestinyLauncher.Services;

/// <summary>Synchronizes configuration files without replacing their containing directories.</summary>
internal static class ProfileConfigFileSync
{
    internal sealed record Plan(string Source, string Target, bool CreateRoot, string[] Writes,
        string[] Removes, string[] Directories, string[] ObsoleteDirectories, long RequiredBytes)
    {
        internal bool HasChanges => CreateRoot || Writes.Length != 0 || Removes.Length != 0 || Directories.Length != 0;
    }

    private sealed record Tree(bool Exists, Dictionary<string, long> Files, HashSet<string> Directories);

    internal static async Task<Plan> PlanAsync(string source, string target, CancellationToken token)
    {
        var desired = ReadTree(source, token);
        var current = ReadTree(target, token);
        var writes = new List<string>();
        foreach (var (relative, size) in desired.Files.OrderBy(pair => pair.Key, FileSystemPathPolicy.Comparer))
        {
            token.ThrowIfCancellationRequested();
            if (!current.Files.TryGetValue(relative, out var currentSize) || currentSize != size
                || !await SameFileAsync(Path.Combine(source, relative), Path.Combine(target, relative), token))
                writes.Add(relative);
        }
        return new Plan(source, target, desired.Exists && !current.Exists, writes.ToArray(),
            current.Files.Keys.Except(desired.Files.Keys, FileSystemPathPolicy.Comparer).Order(FileSystemPathPolicy.Comparer).ToArray(),
            desired.Directories.Except(current.Directories, FileSystemPathPolicy.Comparer).OrderBy(Depth).ThenBy(path => path, FileSystemPathPolicy.Comparer).ToArray(),
            current.Directories.Except(desired.Directories, FileSystemPathPolicy.Comparer).OrderByDescending(Depth).ToArray(),
            writes.Sum(relative => desired.Files[relative]));
    }

    internal static async Task SynchronizeAsync(string source, string target, CancellationToken token)
        => Apply(await PlanAsync(source, target, token), token);

    internal static void Apply(Plan plan, CancellationToken token)
    {
        if (!plan.HasChanges) return;
        string currentPath = plan.Target;
        try
        {
            token.ThrowIfCancellationRequested();
            InstallationDataService.RejectLinks(plan.Target);
            if (plan.CreateRoot) Directory.CreateDirectory(plan.Target);
            foreach (var relative in plan.Removes)
            {
                token.ThrowIfCancellationRequested();
                currentPath = Path.Combine(plan.Target, relative);
                InstallationDataService.RejectLinks(currentPath);
                File.Delete(currentPath);
            }
            foreach (var relative in plan.ObsoleteDirectories)
            {
                token.ThrowIfCancellationRequested();
                currentPath = Path.Combine(plan.Target, relative);
                InstallationDataService.RejectLinks(currentPath);
                try { Directory.Delete(currentPath); }
                // An unused empty container can remain open in Explorer or a terminal.
                // Only a directory that must become a file needs to be removed.
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                    && !plan.Writes.Contains(relative, FileSystemPathPolicy.Comparer)) { }
            }
            foreach (var relative in plan.Directories)
            {
                token.ThrowIfCancellationRequested();
                currentPath = Path.Combine(plan.Target, relative);
                InstallationDataService.RejectLinks(currentPath);
                Directory.CreateDirectory(currentPath);
            }
            foreach (var relative in plan.Writes)
            {
                token.ThrowIfCancellationRequested();
                currentPath = Path.Combine(plan.Target, relative);
                InstallationDataService.RejectLinks(currentPath);
                Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
                var source = Path.Combine(plan.Source, relative);
                InstallationDataService.RejectLinks(source);
                // The temporary file stays beside its destination so replacement is atomic.
                var temporary = currentPath + ".destiny-sync-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(source, temporary);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, currentPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.SetAttributes(temporary, File.GetAttributes(temporary) & ~FileAttributes.ReadOnly);
                        File.Delete(temporary);
                    }
                }
            }
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && ex is not ProfileConfigurationAccessException)
        { throw new ProfileConfigurationAccessException(currentPath, ex); }
    }

    private static Tree ReadTree(string directory, CancellationToken token)
    {
        GameProfileSession.RejectTreeLinks(directory);
        if (File.Exists(directory)) throw new ProfileConfigurationAccessException(directory,
            new IOException("Un archivo ocupa la ruta de una carpeta de configuración."));
        var files = new Dictionary<string, long>(FileSystemPathPolicy.Comparer);
        var directories = new HashSet<string>(FileSystemPathPolicy.Comparer);
        if (!Directory.Exists(directory)) return new Tree(false, files, directories);
        Walk(directory);
        return new Tree(true, files, directories);

        void Walk(string current)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                InstallationDataService.RejectLinks(entry);
                var relative = Path.GetRelativePath(directory, entry);
                if (Directory.Exists(entry)) { directories.Add(relative); Walk(entry); }
                else files.Add(relative, new FileInfo(entry).Length);
            }
        }
    }

    private static int Depth(string path) => path.Count(character => character == Path.DirectorySeparatorChar);

    internal static FileStream OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ProfileConfigurationAccessException(path, ex); }
    }

    private static async Task<bool> SameFileAsync(string source, string target, CancellationToken token)
    {
        await using var left = OpenRead(source);
        await using var right = OpenRead(target);
        var leftHash = await SHA256.HashDataAsync(left, token);
        var rightHash = await SHA256.HashDataAsync(right, token);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}

internal sealed class ProfileConfigurationAccessException(string path, Exception inner) : IOException(
    $"No se pudo modificar «{path}». Si está abierto en otro programa, cierra ese archivo y vuelve a intentarlo. Comprueba también los permisos de la carpeta.", inner);
