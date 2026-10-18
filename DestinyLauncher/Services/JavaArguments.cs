using System.Text;
using System.Diagnostics;
using CmlLib.Core.CommandParser;

namespace DestinyLauncher.Services;

internal static class JavaArguments
{
    // Windows argument quoting: a backslash is literal unless it precedes a double quote.
    // Do not use CmlLib's command parser here: it consumes backslashes in Windows paths.
    internal static string[] Parse(string? text)
    {
        text ??= "";
        if (text.Contains('\0')) throw new FormatException("Los argumentos de Java contienen un carácter no válido.");
        var result = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i == text.Length) break;
            var value = new StringBuilder();
            var quoted = false;
            while (i < text.Length && (quoted || !char.IsWhiteSpace(text[i])))
            {
                var slashes = 0;
                while (i < text.Length && text[i] == '\\') { slashes++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) value.Append('"');
                    else quoted = !quoted;
                    i++;
                }
                else
                {
                    value.Append('\\', slashes);
                    if (i < text.Length && (quoted || !char.IsWhiteSpace(text[i]))) value.Append(text[i++]);
                }
            }
            if (quoted) throw new FormatException("Cierra las comillas de los argumentos de Java.");
            result.Add(value.ToString());
        }
        return result.ToArray();
    }

    internal static void CorrectWindowsQuoting(ProcessStartInfo start, IEnumerable<string> arguments)
    {
        // CmlLib 4.0.6 does not double a trailing backslash before a closing quote.
        // Correct only the serialized custom arguments; preserve its launch construction.
        foreach (var argument in arguments.OrderByDescending(x => x.Length))
        {
            var libraryQuoted = KeyValueArgument.Escape(argument);
            var windowsQuoted = Quote(argument);
            if (libraryQuoted != windowsQuoted)
                start.Arguments = start.Arguments.Replace(libraryQuoted, windowsQuoted, StringComparison.Ordinal);
        }
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }
}
