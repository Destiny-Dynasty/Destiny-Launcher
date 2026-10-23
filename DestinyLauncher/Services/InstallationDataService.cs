using System.Text.Json;

namespace DestinyLauncher.Services;

internal sealed record OwnedInstance(string Path, string Owner);

// A separate ownership marker and a history of exact instance roots make uninstall
// independent of the current settings, without treating .minecraft as our property.
internal sealed class InstallationDataService
{
    private readonly LauncherPaths _paths;
    private string Index => Path.Combine(_paths.Root, "installations.json");
    private const string Marker = ".destiny-instance.json";
    internal InstallationDataService(LauncherPaths paths) => _paths = paths;
    internal IReadOnlyList<OwnedInstance> Read()
    {
        if (!File.Exists(Index)) return [];
        RejectLinks(Index);
        return JsonSerializer.Deserialize<List<OwnedInstance>>(File.ReadAllText(Index)) ?? throw new InvalidDataException("Registro de instalaciones inválido.");
    }
    internal void Register(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!File.Exists(Path.Combine(directory, "destiny-installed.json"))) return;
        ValidateRoot(directory);
        RejectLinks(directory);
        var entries = Read().ToList();
        var known = entries.FirstOrDefault(p => p.Path.Equals(directory, StringComparison.OrdinalIgnoreCase));
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
    internal void RegisterConfigured(string directory)
    {
        Register(directory);
        Register(directory + "-Lite");
        Register(directory + "-Staff");
    }
    internal IReadOnlyList<string> PlanGameRemoval()
    {
        var entries = Read();
        foreach (var item in entries.Where(p => Directory.Exists(p.Path))) Validate(item);
        return entries.Where(p => Directory.Exists(p.Path)).Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal void RemoveSelected(bool profiles, bool gameFiles)
    {
        // Validate the complete plan before deleting anything, including profile data.
        var instances = gameFiles ? PlanGameRemoval() : [];
        RejectLinks(_paths.Root);
        var retained = Read().Where(p => Directory.Exists(p.Path)).Select(p => Path.GetFullPath(p.Path)).ToArray();
        var profileFolders = profiles ? new[] { "cache", "logs", "temp", "updates" }.Select(name => Path.Combine(_paths.Root, name))
            .Where(path => !retained.Any(game => game.Equals(path, StringComparison.OrdinalIgnoreCase)
                || game.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).Where(Directory.Exists).ToArray() : [];
        foreach (var path in profileFolders) RejectLinks(path);
        if (profiles) RejectLinks(_paths.ConfigFile);
        foreach (var directory in instances) DeleteTree(directory);
        if (gameFiles && File.Exists(Index)) File.Delete(Index);
        if (profiles)
        {
            // Keep ownership records if installations are intentionally retained.
            // Never recursively delete the data root: a customized game may live inside it.
            foreach (var file in new[] { "launcher.json" })
            {
                var path = Path.Combine(_paths.Root, file);
                if (File.Exists(path)) { RejectLinks(path); File.Delete(path); }
            }
            foreach (var path in profileFolders) if (Directory.Exists(path)) DeleteTree(path);
        }
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
        var protectedRoots = new[] { Path.GetPathRoot(full)!, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.GetFullPath(_paths.Root), AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "versions") };
        if (protectedRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Any(p =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)).Equals(full, StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(p).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("No se puede borrar una carpeta compartida: " + full);
    }
    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("La ruta contiene un enlace: " + path);
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
