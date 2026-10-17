using System.Text.RegularExpressions;

namespace DestinyLauncher.Models;

internal sealed record LauncherPresentation(string Action, string State, string Hint, string Status, string Detail, string Tone)
{
    internal static LauncherPresentation Describe(string installed, PackUpdateCheck? check, bool checking, string? error, bool failed, bool localFallbackAllowed = true)
    {
        var hasInstall = !string.IsNullOrWhiteSpace(installed);
        if (checking) return new("COMPROBANDO…", "Comprobando", "Consultando el estado del modpack", "Comprobando modpack", "Consultando la versión disponible y los archivos locales.", "Gold");
        if (failed) return new("REINTENTAR", "Requiere atención", "Vuelve a intentar la operación", "La operación no se completó", "Puedes volver a intentarlo o revisar la carpeta de instalación en Ajustes.", "Error");
        if (error is not null) return new(hasInstall && localFallbackAllowed ? "JUGAR" : "REINTENTAR", hasInstall ? "Sin verificar" : "Sin instalar", hasInstall && localFallbackAllowed ? "Intentará usar la instalación local" : hasInstall ? "Volver a comprobar el modpack" : "Volver a comprobar e instalar", "No se pudo comprobar el modpack", error, "Warning");
        if (check is null) return new(hasInstall ? "JUGAR" : "INSTALAR", hasInstall ? "Sin verificar" : "Sin instalar", hasInstall ? "Comprobará los archivos antes de jugar" : "Instala el modpack y abre Minecraft", hasInstall ? "Instalación local disponible" : "Prepara tu primera partida", "Elige una variante y un nombre de jugador para continuar.", "Gold");
        if (!hasInstall) return new("INSTALAR", "Sin instalar", "Instala el modpack y abre Minecraft", "Listo para instalar", "Se descargarán el modpack, Minecraft y Fabric antes de abrir el juego.", "Gold");
        if (check.HasChanges || installed != check.Manifest.Version) return new("ACTUALIZAR", "Actualización disponible", "Actualiza el modpack y abre Minecraft", "Hay archivos por actualizar", "El launcher actualizará los archivos del modpack antes de jugar.", "Warning");
        return new("JUGAR", "Actualizado", "Todo preparado para entrar al reino", "Listo para jugar", "Los archivos del modpack están al día.", "Success");
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
