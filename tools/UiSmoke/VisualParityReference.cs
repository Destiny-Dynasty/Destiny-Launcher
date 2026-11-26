using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DestinyLauncher;

internal static partial class Program
{
    private static void ExportTextMetrics(FrameworkElement target, string path)
    {
        IEnumerable<DependencyObject> Walk(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var descendant in Walk(child)) yield return descendant;
            }
        }
        var text = Walk(target).OfType<TextBlock>().Where(item => item.IsVisible).Select(item =>
        {
            var point = item.TranslatePoint(default, target);
            return new { item.Text, font = item.FontFamily.Source, weight = item.FontWeight.ToString(), size = item.FontSize, x = point.X, y = point.Y, width = item.ActualWidth, height = item.ActualHeight };
        });
        File.WriteAllText(path, JsonSerializer.Serialize(text, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task AuditParityReference()
    {
        var window = new MainWindow(previewMode: true, dataRoot: Path.Combine(_output, "isolated-parity-data"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            Width = 1000, Height = 620, ShowActivated = false, ShowInTaskbar = false
        };
        window.Show();
        await Idle();
        Get<NewsCarousel>(window, "News").SuspendPlayback(true);
        void Snapshot(string page)
        {
            Capture(window, "windows-" + page);
            ExportTextMetrics(window, Path.Combine(_output, "windows-" + page + ".text.json"));
            var names = new[] { "Hero", "HeroMark", "News", "ProfileButton", "LaunchPanel", "PrimaryButton", "GameOptionsButton", "HomeNav", "InstallationNav", "SettingsNav", "EditionGrid", "InstallationView", "InstallationContent", "InstallationActions", "InstallPrimaryButton" };
            var layout = names.Select(name => (name, control: window.FindName(name) as FrameworkElement))
                .Where(item => item.control is { IsVisible: true })
                .ToDictionary(item => item.name, item =>
                {
                    var rect = item.control!.TransformToAncestor(window).TransformBounds(new Rect(item.control.RenderSize));
                    return new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
                });
            if (window.FindName("SettingsPageHost") is ContentControl { Content: UserControl { Content: FrameworkElement settingsLayout } } && settingsLayout.IsVisible)
            {
                var rect = settingsLayout.TransformToAncestor(window).TransformBounds(new Rect(settingsLayout.RenderSize));
                layout["SettingsView"] = new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
            }
            if (Get<TextBlock>(window, "InstallationPathText").Parent is Grid detailsGrid && detailsGrid.Parent is Border details && details.IsVisible)
            {
                var rect = details.TransformToAncestor(window).TransformBounds(new Rect(details.RenderSize));
                layout["InstallDetailsPanel"] = new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
            }
            File.WriteAllText(Path.Combine(_output, "windows-" + page + ".png.layout.json"), JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
            if (page == "home")
            {
                var hero = Get<Grid>(window, "Hero");
                var poster = hero.Children.OfType<Image>().First();
                var origin = poster.TranslatePoint(default, hero);
                File.WriteAllText(Path.Combine(_output, "windows-poster.json"), JsonSerializer.Serialize(new { poster.ActualWidth, poster.ActualHeight, x = origin.X, y = origin.Y, sourceWidth = poster.Source.Width, sourceHeight = poster.Source.Height, uri = poster.Source.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        Snapshot("home");
        Get<Button>(window, "InstallationNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(); Snapshot("installation");
        Get<Button>(window, "SettingsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(); Snapshot("settings");
        var settingsPage = (UserControl)Get<ContentControl>(window, "SettingsPageHost").Content;
        Descendants<ScrollViewer>(settingsPage).First().ScrollToEnd(); await Idle(); Snapshot("settings-bottom");
        window.Width = 820; window.Height = 560;
        Get<Button>(window, "HomeNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(); Snapshot("home-compact");
        Get<Button>(window, "InstallationNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(); Snapshot("installation-compact");
        Get<Button>(window, "SettingsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle(); Snapshot("settings-compact");
        window.Width = 1000; window.Height = 620;
        Get<Button>(window, "HomeNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        Get<Button>(window, "GameOptionsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        var menu = Get<Button>(window, "GameOptionsButton").ContextMenu;
        var menuBitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        menuBitmap.Render(menu);
        var menuEncoder = new PngBitmapEncoder(); menuEncoder.Frames.Add(BitmapFrame.Create(menuBitmap));
        using (var stream = File.Create(Path.Combine(_output, "windows-game-menu.png"))) menuEncoder.Save(stream);
        menu.IsOpen = false;
        var profile = new ProfileDialog("", true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        profile.Show(); await Idle(); Capture(profile, "windows-profile"); ExportTextMetrics(profile, Path.Combine(_output, "windows-profile.text.json")); profile.Close();
        foreach (var (page, dialog) in new (string, Window)[]
        {
            ("accounts", new AccountsDialog(["DestinyPlayer", "Explorador"], "DestinyPlayer", _ => true)),
            ("add-account", new AddAccountDialog([], true)),
            ("repair", new RepairDialog("Destiny"))
        })
        {
            dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -32000; dialog.Top = -32000; dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
            dialog.Show(); await Idle(); Capture(dialog, "windows-" + page); ExportTextMetrics(dialog, Path.Combine(_output, "windows-" + page + ".text.json")); dialog.Close();
        }
        window.Close();
        Passed.Add("Exported Windows home, installation, settings, game menu and profile with isolated data and no game operations.");
    }
}

