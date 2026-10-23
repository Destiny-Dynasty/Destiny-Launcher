using System.Text.RegularExpressions;

namespace DestinyLauncher.Services;

internal static class GameOptionsFile
{
    internal static async Task SetFullscreenAsync(string instance, bool fullscreen, CancellationToken token)
    {
        var file = Path.Combine(instance, "options.txt");
        var original = File.Exists(file) ? await File.ReadAllTextAsync(file, token) : "";
        var property = "fullscreen:" + (fullscreen ? "true" : "false");
        var updated = Regex.IsMatch(original, @"(?m)^fullscreen:")
            ? Regex.Replace(original, @"(?m)^fullscreen:[^\r\n]*", property)
            : original + (original.Length > 0 && !original.EndsWith('\n') ? "\n" : "") + property + "\n";
        if (original == updated) return;
        Directory.CreateDirectory(instance);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, updated, token); token.ThrowIfCancellationRequested(); File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
