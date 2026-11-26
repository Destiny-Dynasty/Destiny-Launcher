namespace DestinyLauncher.Services;

internal static class SharedGameDirectoryLock
{
    internal static FileStream Acquire(LauncherPaths paths, string message)
    {
        InstallationDataService.RejectLinks(paths.GameDirectory);
        Directory.CreateDirectory(paths.GameDirectory);
        // Keep the lock outside the tree: uninstall must hold the same lock while
        // deleting that tree, on Windows as well as Unix.
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.GameDirectory));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
        var lockPath = Path.Combine(Path.GetDirectoryName(canonical)!, ".destiny-game-lock-" + key + ".lock");
        InstallationDataService.RejectLinks(lockPath);
        FileStream fileLock;
        try { fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException(message, ex); }
        try
        {
            // Respect a still-running local build using the previous lock location.
            var legacy = Path.Combine(paths.GameDirectory, ".destiny-profile-session.lock");
            InstallationDataService.RejectLinks(legacy);
            if (File.Exists(legacy)) { using var probe = new FileStream(legacy, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            GameProfileSession.EnsureNoRunningGame(paths); return fileLock;
        }
        catch { fileLock.Dispose(); throw; }
    }
}
