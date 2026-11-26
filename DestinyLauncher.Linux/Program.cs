using Avalonia;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace DestinyLauncher.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsLinux())
            NativeLibrary.SetDllImportResolver(typeof(Core).Assembly, (name, assembly, searchPath) =>
                name == "libvlc" && NativeLibrary.TryLoad("libvlc.so.5", assembly, searchPath, out var handle)
                    ? handle : IntPtr.Zero);
        App.DataRoot = ReadDataRoot(args);
        App.StaticPreview = args.Contains("--no-video");
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--render-preview") App.ExportPath = Path.GetFullPath(args[++i]);
            else if (args[i] == "--edition" && Enum.TryParse<DestinyLauncher.Models.PackEdition>(args[++i], true, out var edition) && Enum.IsDefined(edition)) App.InitialEdition = edition;
            else if (args[i] == "--page") App.ExportPage = args[++i];
            else if (args[i] == "--size")
            {
                var dimensions = args[++i].Split('x');
                if (dimensions.Length != 2 || !double.TryParse(dimensions[0], out var width) || !double.TryParse(dimensions[1], out var height) || width < 760 || height < 500)
                    throw new ArgumentException("La vista previa requiere al menos 760x500.");
                App.ExportWidth = width; App.ExportHeight = height;
            }
        }
        if (App.IsVisualAudit) App.DataRoot ??= Path.Combine(Path.GetTempPath(), "DestinyLauncher-visual-audit", Guid.NewGuid().ToString("N"));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    private static string? ReadDataRoot(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--data-root=", StringComparison.Ordinal)) return args[i][12..];
            if (args[i] == "--data-root" && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = "destiny-launcher" })
        .LogToTrace();
}
