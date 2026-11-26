namespace DestinyLauncher.Services;

/// <summary>File-system path comparisons follow the case rules of the current operating system.</summary>
public static class FileSystemPathPolicy
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
