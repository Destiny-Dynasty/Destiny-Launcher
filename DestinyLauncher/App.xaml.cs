using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _instance;
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase))
        {
            await RenderPreviewAsync(e.Args[1], includePlayerSkin: false);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0].Equals("--render-preview-skin", StringComparison.OrdinalIgnoreCase))
        {
            await RenderPreviewAsync(e.Args[1], includePlayerSkin: true);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0].Equals("--render-preview-compact", StringComparison.OrdinalIgnoreCase))
        {
            await RenderPreviewAsync(e.Args[1], includePlayerSkin: false, previewWidth: 820, previewHeight: 560);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0].Equals("--render-settings-preview", StringComparison.OrdinalIgnoreCase))
        {
            RenderSettingsPreview(e.Args[1]);
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0].Equals("--render-settings-preview-compact", StringComparison.OrdinalIgnoreCase))
        {
            RenderSettingsPreview(e.Args[1], 820, 560);
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0].Equals("--render-profile-preview", StringComparison.OrdinalIgnoreCase))
        {
            RenderProfilePreview(e.Args[1]);
            Shutdown();
            return;
        }

        var uninstall = e.Args.FirstOrDefault() == "--uninstall";
        if (!LauncherStartupOptions.TryParse(uninstall ? e.Args.Skip(1).ToArray() : e.Args, out var startup)) { Shutdown(2); return; }
        var installation = LauncherInstallation.Read(AppContext.BaseDirectory);
        var dataRoot = startup.DataRoot ?? installation?.DataRoot;
        if (SisuExperiment.Enabled && dataRoot is null)
            dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DestinyLauncher-SisuExperiment");
        // Installed sessions use the same mutex even when shortcuts specify the default data root.
        var defaultRoot = new LauncherPaths().Root;
        if (dataRoot is not null && Path.GetFullPath(dataRoot).Equals(defaultRoot, StringComparison.OrdinalIgnoreCase)) dataRoot = null;
        var mutexRoot = dataRoot is not null && !Path.GetFullPath(dataRoot).Equals(defaultRoot, StringComparison.OrdinalIgnoreCase) ? dataRoot : null;
        _instance = new Mutex(false, LauncherInstallation.MutexName(mutexRoot), out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Cierra Destiny Launcher antes de continuar.", "Destiny Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1); return;
        }
        if (uninstall)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var paths = new LauncherPaths(dataRoot); paths.Ensure();
            var data = new InstallationDataService(paths);
            try { if (File.Exists(paths.ConfigFile)) data.RegisterConfigured(new ConfigService(paths).Load().InstallDirectory); }
            catch (Exception ex) { MessageBox.Show("No se pudo registrar la instalación: " + ex.Message, "Destiny Launcher"); }
            var dialog = new UninstallDialog(data);
            Shutdown(dialog.ShowDialog() == true ? 0 : 1); return;
        }
        // Closing the temporary intro must not shut down the application before
        // the real main window exists. Preview commands above remain unchanged.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        IntroWindow? intro = null;
        try
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                try { intro = new IntroWindow(await IntroWindow.LoadFramesAsync()); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Intro omitida: {ex}"); }
            }

            var playback = intro?.PlayAsync();
            // Let the first logo frame paint before preparing the main view.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var launcher = new MainWindow(dataRoot: dataRoot, initialEdition: startup.Edition);
            MainWindow = launcher;
            if (playback is not null && !await playback)
            {
                launcher.Close();
                Shutdown();
                return;
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            launcher.Show();
        }
        finally { intro?.Close(); }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }

    private async Task RenderPreviewAsync(string outputPath, bool includePlayerSkin, int previewWidth = 1000, int previewHeight = 620)
    {
        var window = new MainWindow(previewMode: true, loadRemoteSkinInPreview: includePlayerSkin)
        {
            WindowState = WindowState.Normal,
            Width = previewWidth,
            Height = previewHeight,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };
        try
        {
            window.Show();
            if (includePlayerSkin)
                await Task.WhenAny(window.PlayerHeadLoadTask, Task.Delay(TimeSpan.FromSeconds(9)));

            window.Measure(new Size(previewWidth, previewHeight));
            window.Arrange(new Rect(0, 0, previewWidth, previewHeight));
            window.UpdateLayout();

            var bitmap = new RenderTargetBitmap(previewWidth, previewHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            using var stream = File.Create(outputPath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }
        finally
        {
            window.Close();
            Shutdown();
        }
    }

    private static void RenderSettingsPreview(string outputPath, int width = 1000, int height = 620)
    {
        var window = new MainWindow(previewMode: true, startOnSettingsPage: true);
        RenderDialog(window, outputPath, width, height);
    }

    private static void RenderProfilePreview(string outputPath) => RenderDialog(new ProfileDialog("Shady935"), outputPath, 450, 326);

    private static void RenderDialog(Window dialog, string outputPath, int width, int height)
    {
        dialog.Width = width;
        dialog.Height = height;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.ShowInTaskbar = false;
        dialog.ShowActivated = false;
        dialog.Left = -32000;
        dialog.Top = -32000;
        dialog.Show();
        dialog.Measure(new Size(width, height));
        dialog.Arrange(new Rect(0, 0, width, height));
        dialog.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dialog);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var stream = File.Create(outputPath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        dialog.Close();
    }
}

