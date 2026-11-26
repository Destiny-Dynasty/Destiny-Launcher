using System.Text.Json;

namespace DestinyLauncher.Services;

internal sealed record OwnedInstance(string Path, string Owner);

// A separate ownership marker and a history of exact instance roots make uninstall
// independent of the current settings, without treating .minecraft as our property.
public sealed class InstallationDataService
{
    private readonly LauncherPaths _paths;
    private string Index => Path.Combine(_paths.Root, "installations.json");
    private const string Marker = ".destiny-instance.json";
    public InstallationDataService(LauncherPaths paths) => _paths = paths;
    internal IReadOnlyList<OwnedInstance> Read()
    {
        if (!File.Exists(Index)) return [];
        RejectLinks(Index);
        return JsonSerializer.Deserialize<List<OwnedInstance>>(File.ReadAllText(Index)) ?? throw new InvalidDataException("Registro de instalaciones inválido.");
    }
    public void Register(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!File.Exists(Path.Combine(directory, "destiny-installed.json"))) return;
        ValidateRoot(directory);
        RejectLinks(directory);
        var entries = Read().ToList();
        var known = entries.FirstOrDefault(p => p.Path.Equals(directory, FileSystemPathPolicy.Comparison));
        if (known is not null) { Validate(known); return; }
        var markerFile = Path.Combine(directory, Marker);
        // Do not take ownership of another launcher's instance.
        if (File.Exists(markerFile)) throw new InvalidDataException("Esta instalación pertenece a otra raíz de datos.");
        var entry = new OwnedInstance(directory, Guid.NewGuid().ToString("N"));
        WriteAtomic(markerFile, JsonSerializer.Serialize(entry));
        entries.Add(entry);
        Directory.CreateDirectory(_paths.Root);
        try { WriteAtomic(Index, JsonSerializer.Serialize(entries)); }
        catch { File.Delete(markerFile); throw; }
    }
    public void RegisterConfigured(string directory)
    {
        Register(directory);
        Register(directory + "-Lite");
        Register(directory + "-Staff");
    }
    public IReadOnlyList<string> PlanGameRemoval()
    {
        var entries = Read();
        foreach (var item in entries.Where(p => Directory.Exists(p.Path))) Validate(item);
        return entries.Where(p => Directory.Exists(p.Path)).Select(p => p.Path).Distinct(FileSystemPathPolicy.Comparer).ToArray();
    }
    public void RemoveInstance(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var entries = Read().ToList();
        var entry = entries.SingleOrDefault(item => Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path)).Equals(full, FileSystemPathPolicy.Comparison))
            ?? throw new InvalidOperationException("Esta instalación no está registrada como propiedad del launcher.");
        Validate(entry);
        foreach (var other in entries.Where(item => item != entry))
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other.Path));
            if (path.StartsWith(full + Path.DirectorySeparatorChar, FileSystemPathPolicy.Comparison) ||
                full.StartsWith(path + Path.DirectorySeparatorChar, FileSystemPathPolicy.Comparison))
                throw new InvalidDataException("La carpeta se superpone con otra instalación registrada.");
        }
        using var gameLock = SharedGameDirectoryLock.Acquire(_paths, "Cierra Minecraft antes de desinstalar este perfil.");
        if (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.GameDirectory)), FileSystemPathPolicy.Comparison))
        {
            var mods = Path.Combine(full, "mods");
            if (Directory.Exists(mods)) DeleteTree(mods);
            foreach (var fileName in new[] { "destiny-installed.json", ".destiny-managed-files.json", ".destiny-instance.json" })
            {
                var file = Path.Combine(full, fileName);
                if (File.Exists(file)) { RejectLinks(file); File.Delete(file); }
            }
            var profileConfig = _paths.ProfileConfigDirectory("destiny");
            if (Directory.Exists(profileConfig)) DeleteTree(profileConfig);
        }
        else
        {
            DeleteTree(full);
            foreach (var slug in new[] { "destiny-lite", "destiny-staff" })
            {
                if (!_paths.Instance(slug).Equals(full, FileSystemPathPolicy.Comparison)) continue;
                var profileConfig = _paths.ProfileConfigDirectory(slug);
                if (Directory.Exists(profileConfig)) DeleteTree(profileConfig);
                break;
            }
        }
        entries.Remove(entry);
        WriteAtomic(Index, JsonSerializer.Serialize(entries));
    }

    public void RemoveSelected(bool profiles, bool gameFiles)
    {
        GameProfileSession.EnsureNoRunningGame(_paths);
        // Validate the complete plan before deleting anything, including profile data.
        var instances = gameFiles ? PlanGameRemoval() : [];
        var locks = new List<FileStream>();
        try
        {
            foreach (var instance in instances.Append(_paths.GameDirectory).Where(Directory.Exists).Distinct(FileSystemPathPolicy.Comparer).Order(FileSystemPathPolicy.Comparer))
            {
                var instancePaths = new LauncherPaths(_paths.Root);
                instancePaths.SetInstallDirectory(instance);
                locks.Add(SharedGameDirectoryLock.Acquire(instancePaths, "Cierra Minecraft antes de eliminar los datos del launcher."));
            }
            RejectLinks(_paths.Root);
            var retained = Read().Where(p => Directory.Exists(p.Path)).Select(p => Path.GetFullPath(p.Path)).ToArray();
            var profileFolders = profiles ? new[] { _paths.Cache, _paths.Logs, _paths.Temp, Path.Combine(_paths.Root, "updates") }.Distinct(FileSystemPathPolicy.Comparer)
                .Where(path => !retained.Any(game => game.Equals(path, FileSystemPathPolicy.Comparison)
                    || game.StartsWith(path + Path.DirectorySeparatorChar, FileSystemPathPolicy.Comparison))).Where(Directory.Exists).ToArray() : [];
            foreach (var path in profileFolders) RejectLinks(path);
            if (profiles) { RejectLinks(_paths.ConfigFile); RejectLinks(Path.Combine(_paths.Root, "discord-session.bin")); RejectLinks(Path.Combine(_paths.Root, "microsoft-accounts.bin")); }
            foreach (var directory in instances) DeleteTree(directory);
            if (gameFiles && File.Exists(Index)) File.Delete(Index);
            if (profiles)
            {
                // Keep ownership records if installations are intentionally retained.
                // Never recursively delete the data root: a customized game may live inside it.
                foreach (var file in new[] { "launcher.json", "discord-session.bin", "microsoft-accounts.bin" })
                {
                    var path = file == "launcher.json" ? _paths.ConfigFile : Path.Combine(_paths.Root, file);
                    if (File.Exists(path)) { RejectLinks(path); File.Delete(path); }
                }
                foreach (var path in profileFolders) if (Directory.Exists(path)) DeleteTree(path);
            }
        }
        finally { foreach (var fileLock in locks) fileLock.Dispose(); }
    }
    private void Validate(OwnedInstance item)
    {
        ValidateRoot(item.Path); RejectLinks(item.Path);
        var file = Path.Combine(item.Path, Marker); RejectLinks(file);
        var actual = JsonSerializer.Deserialize<OwnedInstance>(File.ReadAllText(file));
        if (actual != item || string.IsNullOrWhiteSpace(item.Owner)) throw new InvalidDataException("No se pudo verificar la propiedad de la instalación: " + item.Path);
    }
    private void ValidateRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains(".." + Path.DirectorySeparatorChar)) throw new InvalidDataException("Ruta de instalación insegura.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var minecraftRoot = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");
        var protectedRoots = new[] { Path.GetPathRoot(full)!, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.GetFullPath(_paths.Root), AppContext.BaseDirectory,
            minecraftRoot,
            Path.Combine(minecraftRoot, "versions") };
        if (protectedRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Any(p =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)).Equals(full, FileSystemPathPolicy.Comparison)
            || Path.GetFullPath(p).StartsWith(full + Path.DirectorySeparatorChar, FileSystemPathPolicy.Comparison)))
            throw new InvalidDataException("No se puede borrar una carpeta compartida: " + full);
    }
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("La ruta contiene un enlace: " + path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void DeleteTree(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) Directory.Delete(child);
            else DeleteTree(child);
        }
        Directory.Delete(directory);
    }
    private static void WriteAtomic(string file, string text)
    {
        RejectLinks(file);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
