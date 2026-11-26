using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DestinyLauncher.Linux;

public sealed partial class App : Application
{
    internal static bool IsLocalTestBuild
    {
        get
        {
#if LOCAL_TEST_BUILD
            return true;
#else
            return false;
#endif
        }
    }
    internal static string? DataRoot { get; set; }
    internal static DestinyLauncher.Models.PackEdition? InitialEdition { get; set; }
    internal static string? ExportPath { get; set; }
    internal static string ExportPage { get; set; } = "home";
    internal static bool StaticPreview { get; set; }
    internal static double ExportWidth { get; set; } = 1000;
    internal static double ExportHeight { get; set; } = 620;
    internal static bool IsVisualAudit => ExportPath is not null;

    public override void Initialize() { AvaloniaXamlLoader.Load(this); UiMotion.Initialize(); }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (IsVisualAudit) desktop.MainWindow = new MainWindow(DataRoot);
            else
            {
                try
                {
                    desktop.MainWindow = new IntroWindow(() =>
                    {
                        var launcher = new MainWindow(DataRoot);
                        desktop.MainWindow = launcher;
                        launcher.Show();
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Intro omitida: {ex}");
                    desktop.MainWindow = new MainWindow(DataRoot);
                }
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
