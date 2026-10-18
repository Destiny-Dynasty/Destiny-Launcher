using System.Runtime.InteropServices;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;

internal sealed record LauncherStartupOptions(string? DataRoot, PackEdition? Edition)
{
    internal static bool TryParse(string[] args, out LauncherStartupOptions options)
    {
        string? root = null; PackEdition? edition = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        options = new(null, null);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i])) return false;
            if (args[i].Equals("--data-root", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(args[i + 1]))
                root = Path.GetFullPath(args[i + 1]);
            else if (args[i].Equals("--edition", StringComparison.OrdinalIgnoreCase)
                     && Enum.TryParse<PackEdition>(args[i + 1], true, out var value) && Enum.IsDefined(value)) edition = value;
            else return false;
        }
        options = new(root, edition); return true;
    }
}

internal static class GameShortcutService
{
    internal static string Create(string desktop, string executable, PackEdition edition, string? dataRoot = null)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("No se encontró el ejecutable del launcher.", executable);
        Directory.CreateDirectory(desktop);
        var name = edition == PackEdition.DestinyLite ? "Destiny Lite" : "Destiny";
        var destination = Path.Combine(desktop, $"Destiny Launcher - {name}.lnk");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows no pudo crear el acceso directo.");
        object? shell = null; object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(type)!;
            shortcut = ((dynamic)shell).CreateShortcut(destination);
            dynamic link = shortcut;
            link.TargetPath = Path.GetFullPath(executable);
            link.Arguments = "--edition " + edition + (dataRoot is null ? "" : " --data-root " + JavaArguments.Quote(dataRoot));
            link.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
            link.IconLocation = executable + ",0";
            link.Description = "Destiny Launcher · " + name;
            link.Save(); return destination;
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
