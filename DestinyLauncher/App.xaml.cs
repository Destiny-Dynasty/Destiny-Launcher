using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using DestinyLauncher.Models;

namespace DestinyLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
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
        if (e.Args.Length == 2 && e.Args[0].Equals("--render-settings-preview", StringComparison.OrdinalIgnoreCase))
        {
            RenderSettingsPreview(e.Args[1]);
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0].Equals("--render-profile-preview", StringComparison.OrdinalIgnoreCase))
        {
            RenderProfilePreview(e.Args[1]);
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private async Task RenderPreviewAsync(string outputPath, bool includePlayerSkin)
    {
        const int previewWidth = 1320;
        const int previewHeight = 830;
        var window = new MainWindow(previewMode: true, loadRemoteSkinInPreview: includePlayerSkin)
        {
            WindowState = WindowState.Normal,
            Width = previewWidth,
            Height = previewHeight,
            ShowInTaskbar = false,
            ShowActivated = false,
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

    private static void RenderSettingsPreview(string outputPath)
    {
        var config = new LauncherConfig
        {
            PlayerName = "Shady935",
            Destiny = new PackDefinition { Name = "Destiny", Slug = "destiny", Version = "1.4.7" },
            DestinyLite = new PackDefinition { Name = "Destiny Lite", Slug = "destiny-lite", Version = "0.1.0" }
        };
        RenderDialog(new SettingsDialog(config), outputPath, 560, 360);
    }

    private static void RenderProfilePreview(string outputPath) => RenderDialog(new ProfileDialog("Shady935"), outputPath, 420, 252);

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

