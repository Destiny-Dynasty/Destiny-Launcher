namespace DestinyLauncher.Models;

internal static class ProfileFileAccessNotice
{
    internal const string Title = "No se pudo abrir Minecraft";
    internal const string Heading = "Un archivo necesita tu atención";
    internal const string Explanation = "El launcher no pudo actualizar un archivo. Puede estar abierto en otro programa o no tener permisos de escritura.";
    internal const string PathLabel = "ARCHIVO O CARPETA AFECTADOS";
    internal const string Instructions = "1. Cierra el archivo en editores u otros programas.\n2. Vuelve al launcher y pulsa Jugar.";
    internal const string Fallback = "Si sigue ocurriendo, reinicia el equipo y comprueba los permisos de esa carpeta.";
    internal const string Copy = "Copiar ruta";
    internal const string Copied = "Ruta copiada";
    internal const string CopyFailed = "No se pudo copiar";
    internal const string Close = "Entendido";
}
