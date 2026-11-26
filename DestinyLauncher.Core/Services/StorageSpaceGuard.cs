namespace DestinyLauncher.Services;

internal static class StorageSpaceGuard
{
    internal static void RequireBytes(string directory, long bytes, long? availableBytes = null)
    {
        if (bytes <= 0) return;
        long available;
        try
        {
            var full = Path.GetFullPath(directory);
            var drive = OperatingSystem.IsWindows() ? new DriveInfo(Path.GetPathRoot(full)!)
                : DriveInfo.GetDrives().Where(d => full == d.Name.TrimEnd('/') || full.StartsWith(d.Name.TrimEnd('/') + "/", StringComparison.Ordinal))
                    .OrderByDescending(d => d.Name.Length).First();
            available = availableBytes ?? drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return; } // Some filesystems cannot report capacity; normal write/rollback checks still apply.
        if (available < checked(bytes + 16L * 1024 * 1024))
            throw new InsufficientStorageException(directory, bytes);
    }

    internal static long TreeBytes(string directory)
    {
        GameProfileSession.RejectTreeLinks(directory);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;
    }
}

internal sealed class InsufficientStorageException(string directory, long bytes) : IOException(
    $"No hay espacio libre suficiente en «{directory}». Se necesitan al menos {Math.Ceiling(bytes / 1048576d)} MB adicionales y un margen de seguridad. Libera espacio y vuelve a intentarlo.");
