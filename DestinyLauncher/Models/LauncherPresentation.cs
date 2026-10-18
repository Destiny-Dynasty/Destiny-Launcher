using System.Text.RegularExpressions;

namespace DestinyLauncher.Models;

internal sealed record LauncherPresentation(string Action, string State, string Status, string Detail, string Tone)
{
    internal static LauncherPresentation Describe(string installed, PackUpdateCheck? check, bool checking, string? error, bool failed, bool localFallbackAllowed = true)
    {
        var hasInstall = !string.IsNullOrWhiteSpace(installed);
        if (checking) return new("COMPROBANDO…", "Comprobando", "Comprobando modpack", "", "Gold");
        if (failed) return new("REINTENTAR", "Requiere atención", "La operación no se completó", "Puedes volver a intentarlo o revisar la carpeta de instalación en Ajustes.", "Error");
        if (error is not null) return new(hasInstall && localFallbackAllowed ? "JUGAR" : "REINTENTAR", hasInstall ? "Sin verificar" : "Sin instalar", "No se pudo comprobar el modpack", error, "Warning");
        if (check is null) return new(hasInstall ? "JUGAR" : "INSTALAR", hasInstall ? "Sin verificar" : "Sin instalar", "", "", "Gold");
        if (!hasInstall) return new("INSTALAR", "Sin instalar", "", "", "Gold");
        if (check.HasChanges || installed != check.Manifest.Version) return new("ACTUALIZAR", "Actualización disponible", "", "", "Gold");
        return new("JUGAR", "Actualizado", "", "", "Success");
    }

    internal static double? Progress(string status, double value)
    {
        var match = Regex.Match(status, @"·\s*(\d+)/(\d+)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var done) && long.TryParse(match.Groups[2].Value, out var total) && total > 0)
            return Math.Clamp((double)done / total, 0, 1);
        if (status.Equals("Preparando Minecraft", StringComparison.Ordinal) && value is >= .05 and <= .60)
            return Math.Clamp((value - .05) / .55, 0, 1);
        return null;
    }
    internal static string ProgressAction(string status) => status switch
    {
        var s when s.StartsWith("Descargando", StringComparison.OrdinalIgnoreCase) => "DESCARGANDO…",
        var s when s.StartsWith("Comprobando", StringComparison.OrdinalIgnoreCase) => "VERIFICANDO…",
        var s when s.StartsWith("Instalando", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Aplicando", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Extrayendo", StringComparison.OrdinalIgnoreCase) => "INSTALANDO…",
        _ => "PREPARANDO…"
    };

    internal static string ProgressStatus(string status)
    {
        var match = Regex.Match(status, @"^Descargando modpack · (\d+)/(\d+) bytes$");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var received) || !long.TryParse(match.Groups[2].Value, out var total)) return status;
        static string Size(long bytes) => bytes >= 1_048_576 ? $"{bytes / 1_048_576d:0.#} MB" : $"{bytes / 1024d:0.#} KB";
        return $"Descargando modpack · {Size(received)} de {Size(total)}";
    }
}
