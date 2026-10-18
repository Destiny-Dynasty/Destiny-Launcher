using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using DestinyLauncher;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private static readonly List<string> Skipped = [];
    private static string _output = "";
    private static bool _remoteSkin;
    [STAThread]
    private static int Main(string[] args)
    {
        // A real, isolated child process exercises OS exit notifications without installing
        // Minecraft, starting Java, or reading any of the player's files.
        if (args.Length == 3 && args[0] == "--game-process")
        {
            Thread.Sleep(int.Parse(args[1]));
            return int.Parse(args[2]);
        }
        _output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-refinement");
        _remoteSkin = args.Contains("--remote-skin");
        Directory.CreateDirectory(_output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DestinyLauncher;component/Themes/DestinyTheme.xaml", UriKind.Relative) });
        application.Startup += async (_, _) =>
        {
            try
            {
                if (args.Contains("--headless")) await AuditHeadless();
                else if (args.Contains("--lifecycle-only")) await AuditGameLifetime();
                else await Audit();
                File.WriteAllText(Path.Combine(_output, "ui-test-results.json"), JsonSerializer.Serialize(new { passed = Passed.Count, checks = Passed, skipped = Skipped, note = "State fixtures and WPF render targets; DPI images do not change the Windows display setting." }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {Passed.Count} UI assertions; captures at {_output}");
                application.Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); application.Shutdown(1); }
        };
        return application.Run();
    }

    private static async Task Audit()
    {
        foreach (var (foreground, background) in new[] { ("Ink", "Canvas"), ("Muted", "Canvas"), ("Muted", "Panel"), ("GoldBright", "Panel"), ("Error", "Panel"), ("Success", "Panel") })
            Assert(Contrast(((SolidColorBrush)Application.Current.FindResource(foreground)).Color, ((SolidColorBrush)Application.Current.FindResource(background)).Color) >= 4.5, $"Readable contrast: {foreground} on {background}");
        Assert(Contrast(Color.FromRgb(0x21, 0x1A, 0x0E), Color.FromRgb(0xB8, 0x92, 0x50)) >= 4.5, "Primary button text meets readable contrast");
        var fixedShell = new MainWindow(dataRoot: Path.Combine(_output, "fixed-window-data", Guid.NewGuid().ToString("N")))
            { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        var fixedHandle = new WindowInteropHelper(fixedShell).EnsureHandle();
        Assert(fixedShell.ResizeMode == ResizeMode.CanMinimize && fixedShell.FindName("MaximizeButton") is null,
            "Normal launcher allows minimization without maximize or resize controls");
        Assert(fixedShell.MinWidth == fixedShell.MaxWidth && fixedShell.Width == fixedShell.MaxWidth
            && fixedShell.MinHeight == fixedShell.MaxHeight && fixedShell.Height == fixedShell.MaxHeight,
            "Normal launcher locks both dimensions to the size chosen at startup");
        var windowStyle = GetWindowLong(fixedHandle, -16);
        Assert((windowStyle & 0x10000) == 0 && (windowStyle & 0x40000) == 0 && (windowStyle & 0x20000) != 0,
            "Native window style removes maximize and thick resize frames while preserving minimize");
        Assert(System.Windows.Shell.WindowChrome.GetWindowChrome(fixedShell).ResizeBorderThickness == new Thickness(0),
            "Custom title bar exposes no resizing hit areas");
        SendMessage(fixedHandle, 0x0112, new IntPtr(0xF030), IntPtr.Zero);
        SendMessage(fixedHandle, 0x00A3, new IntPtr(2), IntPtr.Zero);
        await Idle();
        Assert(fixedShell.WindowState == WindowState.Normal, "Native maximize command and title-bar double click cannot maximize the launcher");
        fixedShell.Close();
        var preview = new MainWindow(previewMode: true, dataRoot: Path.Combine(_output, "fixture-data", Guid.NewGuid().ToString("N"))) { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        preview.Show();
        preview.Width = 1000; preview.Height = 620;
        await Idle();
        preview.SetAuditState("", playerName: "");
        var openedLinks = new List<Uri>();
        preview.OpenExternalLink = openedLinks.Add;
        foreach (var (name, address) in new[] { ("DiscordLink", "https://dsc.gg/destiny-dynasty-mc"), ("YouTubeLink", "https://www.youtube.com/@destiny_dynasty"), ("XLink", "https://x.com/DestinyDynasty_") })
        {
            var button = Get<Button>(preview, name);
            Assert(button.IsVisible && button.Focusable && button.ToolTip is string && button.MinHeight >= 36, $"{name} is visible, keyboard accessible and labelled");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(openedLinks[^1].AbsoluteUri == address && Get<Grid>(preview, "HomeView").IsVisible,
                $"{name} opens the exact HTTPS address without changing the launcher page");
        }
        var news = Get<NewsCarousel>(preview, "News");
        Assert(news.SlideCount == 2, "Two bundled samples appear before any network request");
        Assert(((Border)news.FindName("CaptionSurface")).Opacity == 0, "News is image-only until hover or keyboard focus");
        Assert(news.FindName("PauseButton") is null, "Carousel has no redundant pause button");
        var nextNews = (Button)news.FindName("NextButton");
        var previousNews = (Button)news.FindName("PreviousButton");
        var incomingNews = (Image)news.FindName("CurrentImage");
        var outgoingNews = (Image)news.FindName("PreviousImage");
        nextNews.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(80);
        if (SystemParameters.ClientAreaAnimation)
        {
            var incomingX = ((TranslateTransform)incomingNews.RenderTransform).X;
            var outgoingX = ((TranslateTransform)outgoingNews.RenderTransform).X;
            Assert(incomingX > 0 && incomingX < news.ActualWidth && outgoingX < 0, "Forward news enters from the right while the old image leaves to the left");
            Assert(Math.Abs(incomingX - outgoingX - news.ActualWidth) < 1.5 && incomingNews.Opacity == 1 && outgoingNews.Opacity == 1,
                "Full-width opaque images remain adjacent throughout the swipe without a fade or empty gap");
            Capture(preview, "news-swipe-forward");
        }
        await NewsSettled(news);
        Assert(news.CurrentIndex == 1, "Manual navigation advances the news image");
        Capture(preview, "news-second-sample");
        nextNews.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await NewsSettled(news);
        Assert(news.CurrentIndex == 0, "News navigation wraps around");
        previousNews.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(80);
        if (SystemParameters.ClientAreaAnimation)
        {
            Assert(((TranslateTransform)incomingNews.RenderTransform).X < 0 && ((TranslateTransform)outgoingNews.RenderTransform).X > 0,
                "Previous news enters from the left even when wrapping from the first item to the last");
            Capture(preview, "news-swipe-backward");
        }
        await NewsSettled(news);
        Assert(news.CurrentIndex == 1, "Previous news wraps in the opposite direction");
        nextNews.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        previousNews.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await NewsSettled(news);
        Assert(news.CurrentIndex == 1, "A quick reversal completes both requests without replacing an image mid-swipe");
        for (var click = 0; click < 3; click++) nextNews.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await NewsSettled(news);
        Assert(news.CurrentIndex == 0 && outgoingNews.Source is null, "Rapid repeated arrows preserve the requested final image and release the outgoing frame");
        nextNews.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var newsWidth = news.Width; news.Width = newsWidth - 10;
        await Idle();
        Assert(outgoingNews.Source is null && incomingNews.RenderTransform.Transform(new Point()).X == 0,
            "Resizing during a swipe settles to a complete image without a stale offset");
        news.Width = newsWidth;
        news.SetSlides(NewsFeedService.BundledSamples());
        Assert(!news.IsPlaying, "Inactive offscreen preview does not spend time rotating images");
        news.SetSlides([]);
        Assert(news.Visibility == Visibility.Collapsed && !news.IsPlaying, "Empty editorial feed hides the carousel and stops its timer");
        news.SetSlides(NewsFeedService.BundledSamples().Take(1).ToArray());
        Assert(((StackPanel)news.FindName("Indicators")).Visibility == Visibility.Collapsed && !news.IsPlaying,
            "A single news image has no misleading pagination or rotation");
        news.SetSlides(NewsFeedService.BundledSamples());
        Assert(Text(preview, "PrimaryLabel") == "INSTALAR", "First run offers installation");
        Assert(Text(preview, "PackLatestVersionText") == "No disponible", "Unknown remote version has no invented fallback");
        Assert(preview.DataPaths.InstallDirectory.StartsWith(_output, StringComparison.OrdinalIgnoreCase), "Preview installation is isolated");
        Capture(preview, "home-first-run");
        Assert(!Get<RadioButton>(preview, "DestinyButton").IsVisible, "Edition selection is absent from Home");
        var filesAtFirstRun = Directory.GetFiles(preview.DataPaths.Root, "*", SearchOption.AllDirectories).Length;
        Get<Button>(preview, "PrimaryButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Idle();
        Assert(Get<Grid>(preview, "InstallationView").IsVisible, "First Install opens installation management before profile or downloads");
        Assert(Get<RadioButton>(preview, "DestinyButton").IsVisible, "Variants are available in installation management");
        Assert(Directory.GetFiles(preview.DataPaths.Root, "*", SearchOption.AllDirectories).Length == filesAtFirstRun, "Opening installation management does not write game or profile files");
        Assert(Get<Button>(preview, "InstallationNav").Tag?.ToString() == "Selected" && Get<Button>(preview, "HomeNav").Tag?.ToString() != "Selected", "Installation navigation has one active indicator");
        Capture(preview, "installation-first-run");
        Get<RadioButton>(preview, "LiteButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Idle();
        Assert(Get<TextBlock>(preview, "InstallationPathText").Text.EndsWith("-Lite", StringComparison.Ordinal), "Installation details follow the selected edition path");
        Capture(preview, "installation-lite");
        Get<Button>(preview, "HomeNav").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        preview.SetAuditState("", playerName: "");
        await Idle();
        var roundedImage = new RenderTargetBitmap((int)preview.ActualWidth, (int)preview.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        roundedImage.Render(preview);
        byte AlphaAt(int x, int y) { var pixel = new byte[4]; roundedImage.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel[3]; }
        Assert(AlphaAt(0, 0) == 0 && AlphaAt(roundedImage.PixelWidth - 1, roundedImage.PixelHeight - 1) == 0, "Restored window visibly cuts out both opposite corners");
        Assert(AlphaAt(40, 20) == 255, "Rounding preserves the opaque title bar content");
        Assert(AlphaAt(0, 30) == 255 && AlphaAt(roundedImage.PixelWidth - 1, 30) == 255
            && AlphaAt(30, 0) == 255 && AlphaAt(30, roundedImage.PixelHeight - 1) == 255,
            "Fading outline reveals opaque window surfaces instead of the black HWND backing");
        AssertNativeCorners(preview);
        CaptureLogoComparison(preview);

        preview.SetAuditState("1.4.6", Check("1.4.7", true));
        Assert(Text(preview, "PrimaryLabel") == "ACTUALIZAR", "Changed pack offers update");
        Assert(Text(preview, "PackStateText") == "Actualización disponible", "Update state is explicit");
        Capture(preview, "home-update");
        preview.SetAuditState("1.4.7", Check("1.4.7", false));
        Assert(Text(preview, "PrimaryLabel") == "JUGAR", "Current pack offers play");
        Capture(preview, "home-ready");
        Get<Button>(preview, "PrimaryButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(Get<Grid>(preview, "HomeView").IsVisible && !Get<Grid>(preview, "InstallationView").IsVisible, "Play with an installed pack does not reopen variant selection");
        preview.SetAuditState("1.4.7", Check("1.4.7", true));
        Assert(Text(preview, "PrimaryLabel") == "JUGAR", "File differences alone do not force a same-version update");
        preview.SetAuditState("1.4.7", error: "No se pudo conectar con el servidor. Revisa tu conexión y vuelve a intentarlo.");
        Assert(Text(preview, "PackStateText") == "Sin verificar", "Offline installation is never called current");
        Assert(Text(preview, "PrimaryLabel") == "JUGAR", "Local installation remains launchable during a connection failure");
        Assert(!MainWindow.AllowsLocalFallback(new HttpRequestException("", null, System.Net.HttpStatusCode.NotFound)), "404 errors keep the backend recovery policy");
        Assert(LauncherPresentation.Describe("1.4.7", null, false, "Manifest unavailable", false, false).Action == "REINTENTAR", "Unsupported local fallback offers retry instead of play");
        Capture(preview, "home-offline");
        preview.SetAuditState("", error: "El modpack no está disponible en el servidor. Vuelve a comprobarlo más tarde.");
        Assert(Text(preview, "PrimaryLabel") == "REINTENTAR", "Missing manifest without installation offers recovery");
        Capture(preview, "home-connection-error");
        preview.SetAuditState("", checking: true);
        Assert(!Get<Button>(preview, "PrimaryButton").IsEnabled, "Primary action waits for manifest check");
        Capture(preview, "home-loading");
        preview.SetAuditState("0.1.0", Check("0.1.0", false, "destiny-lite"), edition: PackEdition.DestinyLite);
        Assert(Get<RadioButton>(preview, "LiteButton").IsChecked == true && Get<RadioButton>(preview, "DestinyButton").IsChecked == false, "Only one edition can be selected");
        Assert(Text(preview, "EditionText") == "Destiny Lite", "Installation summary follows the selected edition");
        Capture(preview, "home-lite");
        var filesBefore = Directory.GetFiles(preview.DataPaths.Instances, "*", SearchOption.AllDirectories).Length;
        Get<RadioButton>(preview, "DestinyButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(Text(preview, "EditionText") == "Destiny", "Edition control changes the active pack");
        Assert(Directory.GetFiles(preview.DataPaths.Instances, "*", SearchOption.AllDirectories).Length == filesBefore, "Selecting an edition does not install or modify game files");

        preview.SetAuditState("1.4.6", Check("1.4.7", true));
        foreach (var (status, value, label, name) in new[] {
            ("Descargando modpack · 3145728/10485760 bytes", .544, "DESCARGANDO…", "home-downloading"),
            ("Comprobando archivos · 4/10", .172, "VERIFICANDO…", "home-verifying"),
            ("Instalando Fabric 0.19.5", .74, "INSTALANDO…", "home-installing"),
            ("Preparando Minecraft", .325, "PREPARANDO…", "home-preparing") })
        {
            preview.SetAuditProgress(status, value);
            Assert(Text(preview, "PrimaryLabel") == label, $"Action matches {status}");
            Assert(!Get<Button>(preview, "PrimaryButton").IsEnabled && !Get<RadioButton>(preview, "LiteButton").IsEnabled && !Get<Button>(preview, "ProfileButton").IsEnabled && !Get<Button>(preview, "SettingsNav").IsEnabled && !Get<Button>(preview, "InstallationNav").IsEnabled && !Get<Button>(preview, "InstallPrimaryButton").IsEnabled, "Busy operation locks conflicting actions");
            Capture(preview, name);
            preview.EndAuditProgress();
        }
        Assert(LauncherPresentation.Progress("Descargando modpack · 3/10", .544) == .3, "Progress uses actual file counts");
        Assert(LauncherPresentation.Progress("Descargando modpack · 1024/2048 bytes", .544) == .5, "Download progress uses real received bytes");
        Assert(LauncherPresentation.ProgressStatus("Descargando modpack · 1048576/2097152 bytes").Contains("1 MB de 2 MB"), "Download size is readable without invented percentages");
        Assert(LauncherPresentation.Progress("Instalando Fabric", .74) is null, "Unmeasured stages do not show artificial percentages");
        Assert(LauncherPresentation.Progress("Preparando Minecraft", .325) is double p && Math.Abs(p - .5) < .001, "Minecraft byte progress is mapped to its own stage");
        Assert(LauncherPresentation.Describe("1.4.7", Check("1.4.7", false), false, null, true).Action == "REINTENTAR", "Failed operation offers retry");
        preview.SetAuditFailure(); Capture(preview, "home-operation-error");

        preview.SetAuditState("1.4.7", Check(new string('9', 64), true), playerName: "MMMMMMMMMMMMMMMM");
        foreach (var (width, height) in new[] { (1000d, 620d), (820d, 560d), (760d, 500d), (680d, 480d), (1280d, 760d) })
        {
            preview.MinWidth = 640; preview.MinHeight = 440; preview.Width = width; preview.Height = height;
            await Idle();
            AssertNativeCorners(preview);
            var button = Get<Button>(preview, "PrimaryButton"); button.BringIntoView(); await Idle();
            HorizontalBounds(preview, button, $"Primary action at {width}x{height}");
            var primaryBounds = button.TransformToAncestor(preview).TransformBounds(new Rect(button.RenderSize));
            var noticeTop = Get<Border>(preview, "StatusFooter").Visibility == Visibility.Visible
                ? Get<TextBlock>(preview, "StatusText").TransformToAncestor(preview).Transform(new Point(0, 0)).Y : preview.ActualHeight;
            Assert(primaryBounds.Top >= 40 && primaryBounds.Bottom < noticeTop, $"Primary action is fully visible at {width}x{height}");
            HorizontalBounds(preview, Get<Button>(preview, "ProfileButton"), $"Long profile at {width}x{height}");
            HorizontalBounds(preview, news, $"News carousel fits at {width}x{height}");
            var newsBounds = news.TransformToAncestor(preview).TransformBounds(new Rect(news.RenderSize));
            var logoBounds = Get<Image>(preview, "HeroMark").TransformToAncestor(preview).TransformBounds(new Rect(Get<Image>(preview, "HeroMark").RenderSize));
            var profileBounds = Get<StackPanel>(preview, "ProfilePanel").TransformToAncestor(preview).TransformBounds(new Rect(Get<StackPanel>(preview, "ProfilePanel").RenderSize));
            Assert(newsBounds.Top >= logoBounds.Bottom && newsBounds.Right <= profileBounds.Left,
                $"News, logo and profile do not overlap at {width}x{height}");
            Assert(Get<ScrollViewer>(preview, "HomeScroll").ExtentWidth <= Get<ScrollViewer>(preview, "HomeScroll").ViewportWidth + 1, $"No horizontal scroll at {width}x{height}");
            Capture(preview, $"layout-{width}x{height}");
        }
        preview.Width = 1000; preview.Height = 620; await Idle();
        Get<Button>(preview, "InstallationNav").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Idle();
        Capture(preview, "installation-update");
        preview.Width = 760; preview.Height = 500; await Idle();
        var installAction = Get<Button>(preview, "InstallPrimaryButton");
        HorizontalBounds(preview, installAction, "Installation action fits the minimum width");
        var installBounds = installAction.TransformToAncestor(preview).TransformBounds(new Rect(installAction.RenderSize));
        Assert(installBounds.Bottom <= preview.ActualHeight && Get<Border>(preview, "StatusFooter").Visibility == Visibility.Collapsed,
            "Installation action fits without a redundant idle footer");
        Capture(preview, "installation-compact");
        Get<Button>(preview, "HomeNav").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        preview.Width = 1000; preview.Height = 620; await Idle();
        preview.SetAuditState("1.4.7", Check("1.4.7", false), playerName: "Player_Destiny16");
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 }) Capture(preview, $"dpi-{scale * 100:0}", scale);
        Assert(Get<Image>(preview, "PlayerHead").Stretch == Stretch.Uniform, "Head image preserves its aspect ratio");
        Assert(Get<TextBlock>(preview, "PlayerInitial").IsVisible, "Missing skin keeps the initial fallback");

        Get<Button>(preview, "SettingsNav").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Idle();
        Assert(!news.IsPlaying, "Hidden Home page stops carousel playback");
        Capture(preview, "settings");
        var settings = (SettingsPage)Get<ContentControl>(preview, "SettingsPageHost").Content;
        var fields = Descendants<TextBox>(settings).ToArray();
        var pathInput = fields.Single(x => System.Windows.Automation.AutomationProperties.GetAutomationId(x) == "InstallDirectoryInput");
        var ramInput = fields.Single(x => System.Windows.Automation.AutomationProperties.GetAutomationId(x) == "RamInput");
        pathInput.Text = "relative-path";
        var save = Descendants<Button>(settings).Single(b => Equals(b.Content, "Guardar ajustes"));
        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(Descendants<TextBlock>(settings).Any(t => t.Text.Contains("Elige una ruta completa") && t.IsVisible), "Invalid install path has an inline message");
        Capture(preview, "settings-path-validation");
        pathInput.Text = LauncherPaths.InstallationBaseDirectory(preview.DataPaths.Instances);
        ramInput.Text = "0"; save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(Descendants<TextBlock>(settings).Any(t => t.Text.Contains("entre 1 y 1024") && t.IsVisible), "Invalid memory has an inline message");
        Capture(preview, "settings-memory-validation");
        ramInput.Text = "12";
        Assert(settings.HasChanges, "Unsaved edits are tracked");
        LauncherConfig? saved = null;
        settings.SaveRequested = c => { saved = c; settings.MarkSaved(); return Task.CompletedTask; };
        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(saved?.RamGb == 12 && saved.InstallDirectory == preview.DataPaths.Instances, "Valid settings are submitted to the real save contract");
        Assert(!settings.HasChanges, "Saved edits do not trigger a discard prompt");
        var store = new ConfigService(preview.DataPaths); store.Save(saved!);
        Assert(store.Load().RamGb == 12 && store.Load().PlayerName == "Player_Destiny16", "Offline profile and settings round-trip through JSON");
        preview.Width = 760; preview.Height = 500; await Idle(); Capture(preview, "settings-compact");
        foreach (var control in Descendants<Button>(settings).Where(x => x.IsVisible)) HorizontalBounds(preview, control, "Settings action at minimum size");
        if (_remoteSkin)
        {
            Get<Button>(preview, "HomeNav").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            preview.Width = 1000; preview.Height = 620; await Idle();
            preview.SetAuditState("1.4.7", Check("1.4.7", false), playerName: "Notch");
            using var heads = new PlayerHeadService(preview.DataPaths);
            BitmapImage? head = null;
            try { head = await heads.GetHeadAsync("Notch", CancellationToken.None); }
            catch (Exception ex) { Skipped.Add($"Remote skin unavailable: {ex.GetType().Name}"); }
            if (head is not null)
            {
                Assert(head.IsFrozen && head.PixelWidth > 0 && head.PixelHeight > 0, "External head service produces a usable image");
                Get<Image>(preview, "PlayerHead").Source = head; Get<Image>(preview, "PlayerHead").Visibility = Visibility.Visible;
                Get<TextBlock>(preview, "PlayerInitial").Visibility = Visibility.Collapsed;
                Assert(Directory.GetFiles(Path.Combine(preview.DataPaths.Cache, "player-heads")).Length > 0, "Head cache stays inside isolated test data");
                Capture(preview, "home-remote-skin");
            }
        }
        preview.Width = 680; preview.Height = 480; await Idle();
        var settingsBounds = Get<Button>(preview, "SettingsNav").TransformToAncestor(preview).TransformBounds(new Rect(Get<Button>(preview, "SettingsNav").RenderSize));
        var socialBounds = Get<StackPanel>(preview, "SocialLinks").TransformToAncestor(preview).TransformBounds(new Rect(Get<StackPanel>(preview, "SocialLinks").RenderSize));
        Assert(socialBounds.Top >= settingsBounds.Bottom && socialBounds.Bottom <= preview.ActualHeight, "Social links fit below navigation at the smallest audited height");
        preview.Close();

        var profile = new ProfileDialog("MMMMMMMMMMMMMMMM") { ShowActivated = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        profile.Show(); await Idle(); Capture(profile, "profile");
        var nameInput = Descendants<TextBox>(profile).Single();
        var profileSave = Descendants<Button>(profile).Single(b => Equals(b.Content, "Guardar nombre"));
        nameInput.Text = "ab";
        Assert(!profileSave.IsEnabled, "Too-short offline name cannot be saved");
        nameInput.Text = "bad name!";
        Assert(!profileSave.IsEnabled, "Invalid offline characters cannot be saved"); Capture(profile, "profile-validation");
        nameInput.Text = "Player_123";
        Assert(profileSave.IsEnabled, "Valid offline name can be saved");
        Assert(profileSave.IsDefault && Descendants<Button>(profile).Any(b => b.IsCancel), "Profile supports Enter and Escape");
        profile.Close();
        await AuditGameLifetime();
    }

    private static async Task AuditHeadless()
    {
        // No Show, Activate, native handle creation, input injection, or game-session watcher.
        // The WPF content is detached and laid out only in memory while the user is playing.
        var window = new MainWindow(previewMode: true, dataRoot: Path.Combine(_output, "isolated-data", Guid.NewGuid().ToString("N")));
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        System.Windows.Documents.TextElement.SetForeground(root, (Brush)Application.Current.FindResource("Ink"));
        void Layout(double width = 1000, double height = 620)
        {
            window.MinWidth = 640; window.MinHeight = 440;
            window.Width = width; window.Height = height;
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        }
        void CaptureContent(string name)
        {
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(_output, name + ".png")); encoder.Save(file);
        }
        Layout();
        Assert(!window.IsVisible && PresentationSource.FromVisual(root) is null, "Headless checks create no visible window or presentation source");
        Assert(window.FindName("ActionHint") is null, "Primary button has no redundant hint");
        Assert(Get<TextBlock>(window, "LauncherVersionText").Opacity == .55, "Sidebar version has subdued opacity");
        foreach (var name in new[] { "HomeNav", "InstallationNav", "SettingsNav" })
        {
            var path = Descendants<System.Windows.Shapes.Path>(Get<Button>(window, name)).Single();
            Assert(path.Fill is not null && path.Stroke is null && !path.Data.Bounds.IsEmpty, $"{name} uses a filled vector icon in the launcher palette");
        }
        var footer = Get<Border>(window, "StatusFooter");
        foreach (var installed in new[] { "", "1.4.7" })
        {
            window.SetAuditState(installed, Check("1.4.7", false)); Layout();
            Assert(footer.Visibility == Visibility.Collapsed, $"Idle state '{installed}' has no bottom microcopy");
        }
        CaptureContent("home");
        window.SetAuditState("1.4.7", Check("1.4.7", true));
        Assert(Text(window, "PrimaryLabel") == "JUGAR", "A same-version modpack stays playable despite local file differences");
        window.SetAuditState("1.4.7", checking: true); Layout();
        Assert(footer.Visibility == Visibility.Visible, "Manifest checks retain useful status");
        window.SetAuditState("1.4.7", error: "Sin conexión"); Layout();
        Assert(footer.Visibility == Visibility.Visible && Get<TextBlock>(window, "StatusDetail").Visibility == Visibility.Visible, "Connection errors retain their explanation");
        CaptureContent("connection-error");
        window.SetAuditState("1.4.7", Check("1.4.7", false));
        window.SetAuditProgress("Descargando modpack · 1024/2048 bytes", .3); Layout();
        Assert(footer.Visibility == Visibility.Visible && Get<ProgressBar>(window, "Progress").Value == .5, "Downloads retain real progress without filler text");
        Assert(Get<TextBlock>(window, "StatusDetail").Visibility == Visibility.Collapsed, "Progress does not repeat keep-open instructions");
        window.EndAuditProgress();
        window.SetAuditState("1.4.7", Check("1.4.7", false));
        Get<Button>(window, "InstallationNav").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Layout();
        Assert(!Descendants<TextBlock>(root).Any(t => t.Text.Contains("Seleccionar una variante no descarga") || t.Text.Contains("La variante que usarás")), "Installation omits redundant explanations");
        var openedFolders = new List<string>(); window.OpenDirectory = openedFolders.Add;
        var folderButton = Get<Button>(window, "OpenInstallationFolderButton");
        Assert(folderButton.IsEnabled, "Existing installation exposes Open folder");
        folderButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(openedFolders.Single() == window.DataPaths.Instance("destiny"), "Open folder targets the selected installation without launching Explorer in the test");
        window.SetAuditState("", edition: PackEdition.DestinyLite); Layout();
        Assert(!folderButton.IsEnabled, "Uncreated Lite folder cannot be opened or created by the button");
        folderButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(openedFolders.Count == 1 && !Directory.Exists(window.DataPaths.Instance("destiny-lite")), "Opening an absent folder never creates a new installation");
        window.SetAuditState("1.4.7", Check("1.4.7", false)); Layout();
        CaptureContent("installation");
        Layout(680, 480);
        var folderBounds = folderButton.TransformToAncestor(root).TransformBounds(new Rect(folderButton.RenderSize));
        Assert(folderBounds.Left >= 0 && folderBounds.Right <= root.ActualWidth, "Open folder fits the compact installation page");
        CaptureContent("installation-compact");
        Layout();
        Get<Button>(window, "SettingsNav").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
        var settings = (SettingsPage)Get<ContentControl>(window, "SettingsPageHost").Content;
        var multi = Descendants<CheckBox>(settings).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "MultiAccountToggle");
        Assert(multi.IsChecked == false && Get<Button>(window, "ProfileButton").ContextMenu is null, "Multicuentas is opt-in and leaves the single-profile control unchanged");
        var baseInput = Descendants<TextBox>(settings).Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "InstallDirectoryInput");
        var javaInput = Descendants<TextBox>(settings).Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "JavaArgumentsInput");
        var fixedFolder = Descendants<TextBlock>(settings).Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "FixedInstallationFolder");
        Assert(baseInput.Text == LauncherPaths.InstallationBaseDirectory(window.DataPaths.Instances) && fixedFolder.Text == "\\Destiny",
            "Only the installation parent is editable; the existing final folder is fixed");
        Assert(javaInput.Text == LauncherConfig.DefaultJavaArguments, "Java arguments start with the launcher defaults");
        Assert(!Descendants<TextBlock>(settings).Any(t => t.Text.Contains("carpeta hermana") || t.Text.Contains("Prepara el launcher") || t.Text.Contains("Deja memoria libre")), "Settings keep labels and validation without filler paragraphs");
        var slider = Descendants<Slider>(settings).Single();
        var ram = Descendants<TextBox>(settings).Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "RamInput");
        Assert(slider.Minimum == 2 && slider.Maximum == 16 && slider.SmallChange == 1 && slider.LargeChange == 2, "RAM slider retains one-GB precision and two-GB major steps");
        Assert(Descendants<RamTickBar>(slider).Count() == 1 && slider.Height == 40, "RAM rail has integrated divisions without an extra scale row");
        var track = (Track)slider.Template.FindName("PART_Track", slider);
        var ticks = Descendants<RamTickBar>(slider).Single();
        Assert(Math.Abs(ticks.ActualWidth - (track.ActualWidth - track.Thumb.ActualWidth)) < .1, "RAM divisions align exactly with the thumb center range");
        slider.Value = 10;
        Assert(ram.Text == "10", "RAM slider updates the numeric badge");
        Slider.IncreaseSmall.Execute(null, slider);
        Assert(slider.Value == 11 && ram.Text == "11", "Standard slider commands move RAM by one GB without physical keyboard input");
        ram.Text = "7";
        Assert(slider.Value == 7, "Odd RAM values remain supported");
        ram.Text = "32";
        Assert(ram.Text == "32" && slider.Value == 16, "Manual RAM values above the quick scale are preserved");
        LauncherConfig? saved = null;
        settings.SaveRequested = c => { saved = c; settings.MarkSaved(); return Task.CompletedTask; };
        var save = Descendants<Button>(settings).Single(b => Equals(b.Content, "Guardar ajustes"));
        ram.Text = "0";
        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(saved is null && Descendants<TextBlock>(settings).Any(t => t.Text.Contains("entre 1 y 1024")), "RAM validation remains available after removing help paragraphs");
        ram.Text = "12";
        javaInput.Text = "-Ddestiny.test=custom";
        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(saved?.RamGb == 12 && saved.JavaArguments == "-Ddestiny.test=custom" && saved.InstallDirectory == window.DataPaths.Instances,
            "Saving JVM flags and RAM preserves the existing full installation path");
        javaInput.Text = "-Dtest=\"unfinished";
        saved = null;
        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(saved is null && Descendants<TextBlock>(settings).Any(t => t.Text.Contains("comillas")), "Invalid JVM quoting is reported without saving");
        Descendants<Button>(settings).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "ResetJavaArguments")
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(javaInput.Text == LauncherConfig.DefaultJavaArguments, "Restore defaults replaces only the Java arguments field");
        var newBase = Path.Combine(_output, "new base");
        baseInput.Text = newBase;
        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(saved?.InstallDirectory == Path.Combine(newBase, "Destiny") && !Directory.Exists(newBase),
            "Changing the base preserves the fixed name without moving or creating files");
        baseInput.Text = LauncherPaths.InstallationBaseDirectory(window.DataPaths.Instances);
        multi.IsChecked = true;
        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(saved?.MultiAccountEnabled == true && saved.PlayerName == "Player_Destiny16", "Settings save the multicuentas preference without changing the active offline name");
        Layout(); CaptureContent("settings");
        foreach (var (width, height) in new[] { (820d, 560d), (760d, 500d), (680d, 480d) })
        {
            Layout(width, height);
            foreach (var control in Descendants<Button>(settings))
            {
                var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                Assert(bounds.Left >= 0 && bounds.Right <= width, $"Settings button fits headless layout {width}x{height}");
            }
            CaptureContent($"settings-{width}x{height}");
        }
        Assert(!window.IsVisible && PresentationSource.FromVisual(root) is null, "Headless checks finish without opening or activating any window");
        var liteSettings = new SettingsPage(new LauncherConfig { InstallDirectory = window.DataPaths.Instances, SelectedEdition = PackEdition.DestinyLite });
        liteSettings.Measure(new Size(800, 540)); liteSettings.Arrange(new Rect(0, 0, 800, 540)); liteSettings.UpdateLayout();
        Assert(Descendants<TextBlock>(liteSettings).Any(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "FixedInstallationFolder" && t.Text == "\\Destiny-Lite"),
            "Lite displays its fixed sibling folder while sharing the same editable base");
        window.SetAuditAccounts(true, ["First_Player", "Second_Player"], "First_Player");
        var profileButton = Get<Button>(window, "ProfileButton");
        Assert(profileButton.ContextMenu.Items.OfType<MenuItem>().Count() == 3 && profileButton.ContextMenu.Items.OfType<MenuItem>().Count(m => m.IsChecked) == 1,
            "Account dropdown contains saved names, exactly one selection and Manage accounts");
        var menu = profileButton.ContextMenu;
        menu.Measure(new Size(250, 340)); menu.Arrange(new Rect(0, 0, 250, menu.DesiredSize.Height)); menu.UpdateLayout();
        var menuBitmap = new RenderTargetBitmap(250, (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32); menuBitmap.Render(menu);
        var menuEncoder = new PngBitmapEncoder(); menuEncoder.Frames.Add(BitmapFrame.Create(menuBitmap));
        using (var file = File.Create(Path.Combine(_output, "account-menu.png"))) menuEncoder.Save(file);
        Assert(!menu.IsOpen && PresentationSource.FromVisual(menu) is null, "Account menu is rendered without opening its native popup");
        Assert(window.SetActiveAccount("Second_Player") && Text(window, "PlayerNameText") == "Second_Player", "Selecting a saved account changes the active offline name");
        Assert(!window.SetActiveAccount("Not_Saved"), "Unknown account cannot become active through the selector");
        window.SetAuditProgress("Preparando Minecraft", .2);
        Assert(!window.SetActiveAccount("First_Player") && !window.SaveAccounts(["First_Player"]), "Game preparation locks account selection and editing");
        window.EndAuditProgress();
        Assert(window.SaveAccounts(["First_Player"]) && Text(window, "PlayerNameText") == "First_Player", "Removing the active account selects a remaining name");
        Assert(window.SaveAccounts([]) && Text(window, "PlayerNameText") == "Añadir nombre", "Deleting all accounts requires a new valid name before play");
        window.SetAuditAccounts(false, ["First_Player", "Second_Player"], "Second_Player");
        Assert(profileButton.ContextMenu is null, "Disabling multicuentas restores the edit-name control");
        window.Close();
        var profile = new ProfileDialog("Player_123");
        var profileRoot = (FrameworkElement)profile.Content;
        profile.Content = null;
        profileRoot.Measure(new Size(450, 280)); profileRoot.Arrange(new Rect(0, 0, 450, 280)); profileRoot.UpdateLayout();
        var profileName = Descendants<TextBox>(profileRoot).Single();
        var profileSave = Descendants<Button>(profileRoot).Single(b => Equals(b.Content, "Guardar nombre"));
        Assert(profileSave.IsEnabled && !Descendants<TextBlock>(profileRoot).Any(t => t.Text.StartsWith("De 3 a 16")), "Valid offline profile has no permanent instruction paragraph");
        profileName.Text = "ab";
        Assert(!profileSave.IsEnabled && Descendants<TextBlock>(profileRoot).Any(t => t.Visibility == Visibility.Visible && t.Text.StartsWith("Usa de 3 a 16")), "Invalid profile retains its useful validation message");
        profile.Close();
        AuditAccountsEditor();
        AuditAccountPersistence();
        await AuditSkinCache();
    }

    private static void AuditAccountsEditor()
    {
        var original = new[] { "First_Player", "Second_Player" };
        var editor = new AccountsEditor(original, "First_Player");
        var dialog = new Window(); DialogChrome.Create(dialog, "Cuentas", editor, 520, 440);
        var root = (FrameworkElement)dialog.Content; dialog.Content = null;
        System.Windows.Documents.TextElement.SetForeground(root, (Brush)Application.Current.FindResource("Ink"));
        void Layout() { root.Measure(new Size(520, 440)); root.Arrange(new Rect(0, 0, 520, 440)); root.UpdateLayout(); }
        Layout();
        var input = Descendants<TextBox>(root).Single();
        var add = Descendants<Button>(root).Single(b => Equals(b.Content, "Añadir"));
        var remove = Descendants<Button>(root).Single(b => Equals(b.Content, "Eliminar"));
        var save = Descendants<Button>(root).Single(b => Equals(b.Content, "Guardar"));
        input.Text = "first_player"; add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(editor.AccountNames.Count == 2 && Descendants<TextBlock>(root).Any(t => t.Text == "Esta cuenta ya está guardada."), "Duplicate offline names are rejected regardless of case");
        input.Text = "ab"; add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(editor.AccountNames.Count == 2 && Descendants<TextBlock>(root).Any(t => t.Text.StartsWith("Usa de 3 a 16")), "Account manager validates offline names");
        input.Text = "Third_Player"; add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(editor.AccountNames.Count == 3 && input.Text == "", "Adding a valid account selects it and clears the input");
        remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(editor.AccountNames.SequenceEqual(original) && original.Length == 2, "Removing a draft account leaves the original list untouched");
        var accepted = false; editor.SaveRequested += () => accepted = true;
        input.Text = "bad name"; save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(!accepted, "Saving cannot accept an invalid pending account");
        input.Text = "Fourth_Player"; save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(accepted && editor.AccountNames.Contains("Fourth_Player"), "Save includes a valid name still typed in the input");
        Layout();
        var bitmap = new RenderTargetBitmap(520, 440, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(_output, "accounts.png"))) encoder.Save(file);
        Assert(!dialog.IsVisible && PresentationSource.FromVisual(root) is null, "Account manager is rendered without opening a window");
        dialog.Close();
    }

    private static void AuditAccountPersistence()
    {
        var paths = new LauncherPaths(Path.Combine(_output, "accounts-data", Guid.NewGuid().ToString("N")));
        var service = new ConfigService(paths);
        File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(new { PlayerName = "Legacy_User", InstallDirectory = paths.InstallDirectory, RamGb = 9, JavaArguments = "-Dtest=retained" }));
        var config = service.Load();
        Assert(!config.MultiAccountEnabled && config.Accounts.SequenceEqual(new[] { "Legacy_User" }), "Legacy profile migrates in memory without enabling multicuentas");
        config.MultiAccountEnabled = true; config.Accounts.AddRange(["Second_User", "second_user", "bad name", "ab"]);
        config.PlayerName = "Second_User"; service.Save(config);
        var loaded = service.Load();
        Assert(loaded.MultiAccountEnabled && loaded.PlayerName == "Second_User" && loaded.Accounts.Count == 2
            && loaded.InstallDirectory == paths.InstallDirectory && loaded.RamGb == 9 && loaded.JavaArguments == "-Dtest=retained", "Account selection persists alongside existing paths, RAM and JVM flags");
        loaded.MultiAccountEnabled = false; service.Save(loaded);
        loaded = service.Load();
        Assert(!loaded.MultiAccountEnabled && loaded.Accounts.Count == 2, "Disabling multicuentas keeps the saved account list for later use");
        loaded.Accounts.Clear(); loaded.PlayerName = ""; loaded.MultiAccountEnabled = true; service.Save(loaded);
        Assert(service.Load().Accounts.Count == 0 && service.Load().PlayerName == "", "An empty account list persists without resurrecting a removed profile");
    }

    private static async Task AuditSkinCache()
    {
        var paths = new LauncherPaths(Path.Combine(_output, "skin-data", Guid.NewGuid().ToString("N")));
        var handler = new SkinFixtureHandler(); using var http = new HttpClient(handler);
        using var service = new PlayerHeadService(paths, http);
        var image = await service.GetHeadAsync("Skin_Player", CancellationToken.None);
        Assert(image is { PixelWidth: 8, PixelHeight: 8, IsFrozen: true } && handler.Requests == 3, "Verified public skin is cropped to an undeformed frozen 8x8 head");
        var pixel = new byte[4]; new FormatConvertedBitmap(image!, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(1, 1, 1, 1), pixel, 4, 0);
        Assert(pixel[2] == 200 && pixel[1] == 80 && pixel[0] == 30, "Cached head uses the face region rather than the whole skin");
        new FormatConvertedBitmap(image!, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert(pixel[0] == 220 && pixel[2] == 10, "Skin hat layer is composited over the face");
        using var secondService = new PlayerHeadService(paths, http);
        Assert(await secondService.GetCachedHeadAsync("skin_player", CancellationToken.None) is not null
            && await secondService.GetHeadAsync("SKIN_PLAYER", CancellationToken.None) is not null && handler.Requests == 3,
            "Disk cache survives new service instances and case changes without network requests");
        var cacheFile = Directory.GetFiles(Path.Combine(paths.Cache, "player-heads"), "*.png").Single();
        File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddDays(-8)); handler.Unavailable = true;
        Assert(await service.GetHeadAsync("Skin_Player", CancellationToken.None) is not null && handler.Requests == 4,
            "Expired cache remains visible when the skin service fails");
        Assert(await service.GetHeadAsync("Skin_Player", CancellationToken.None) is not null && handler.Requests == 4,
            "Failed refreshes back off instead of repeatedly requesting a skin");
        Assert(await service.GetHeadAsync("Unknown_User", CancellationToken.None) is null
            && Directory.GetFiles(Path.Combine(paths.Cache, "player-heads"), "*.png").Length == 1,
            "Unknown or unavailable profiles never cache a service's Steve fallback");
        Assert(await service.GetHeadAsync("bad name", CancellationToken.None) is null && handler.Requests == 5,
            "Invalid offline names never trigger a skin lookup");
        handler.Unavailable = false;
        await File.WriteAllTextAsync(cacheFile, "corrupt image"); File.Delete(cacheFile + ".retry");
        Assert(await service.GetHeadAsync("Skin_Player", CancellationToken.None) is not null && handler.Requests == 8,
            "Corrupt cache entries recover from the verified texture without breaking the profile");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.GetHeadAsync("Cancelled_User", cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Assert(true, "Skin requests honor cancellation when switching profiles or starting a game"); }
        if (_remoteSkin)
        {
            using var live = new PlayerHeadService(new LauncherPaths(Path.Combine(_output, "public-skin-data", Guid.NewGuid().ToString("N"))));
            var publicHead = await live.GetHeadAsync("Notch", CancellationToken.None);
            Assert(publicHead is { PixelWidth: 8, PixelHeight: 8 }, "Live public Mojang texture resolves and renders a head in an isolated cache");
        }
    }

    private static async Task AuditGameLifetime()
    {
        var root = Path.Combine(_output, "game-lifetime-data", Guid.NewGuid().ToString("N"));
        var window = new MainWindow(previewMode: true, dataRoot: root)
            { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        window.Show();
        await Idle();
        window.SetAuditState("1.4.7", Check("1.4.7", false));
        var news = Get<NewsCarousel>(window, "News");
        var width = window.Width;
        var height = window.Height;
        using (var unstarted = new Process())
        {
            try { await window.FollowGameProcessAsync(unstarted); throw new Exception("Unstarted game accepted"); }
            catch (InvalidOperationException) { }
            Assert(window.IsVisible, "An unstarted game process cannot hide the launcher");
        }

        window.SetAuditProgress("Preparando Minecraft", .4);
        using var unrelated = StartGameFixture(30000, 0);
        using (var game = StartGameFixture(11000, 0))
        {
            var filesBefore = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".log")).ToDictionary(f => f, File.ReadAllBytes);
            var session = window.FollowGameProcessAsync(game);
            Assert(!window.IsVisible && !session.IsCompleted, "Launcher hides while the actual game process remains alive");
            Assert(!Get<Button>(window, "PrimaryButton").IsEnabled && !Get<Button>(window, "InstallationNav").IsEnabled,
                "Play and installation stay locked during the game session");
            Assert(!news.IsPlaying && !Get<ProgressBar>(window, "Progress").IsIndeterminate,
                "Hidden launcher stops carousel and indeterminate progress animations");
            var heartbeats = 0;
            var responsiveness = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            responsiveness.Tick += (_, _) => heartbeats++;
            responsiveness.Start();
            await Task.Delay(650);
            responsiveness.Stop();
            Assert(heartbeats >= 2, "OS process wait leaves the UI dispatcher responsive");
            var rejectedDuplicate = false;
            try { await window.FollowGameProcessAsync(game); }
            catch (InvalidOperationException) { rejectedDuplicate = true; }
            Assert(rejectedDuplicate, "A second game watcher cannot replace the active session");
            var slide = news.CurrentIndex;
            using var self = Process.GetCurrentProcess();
            self.Refresh();
            var cpuBefore = self.TotalProcessorTime;
            var elapsed = Stopwatch.StartNew();
            await Task.Delay(6000);
            self.Refresh();
            var cpuMs = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var cpuPercentOneCore = cpuMs / elapsed.Elapsed.TotalMilliseconds * 100;
            File.WriteAllText(Path.Combine(_output, "background-measurement.json"), JsonSerializer.Serialize(new
            {
                sampleSeconds = elapsed.Elapsed.TotalSeconds,
                cpuMilliseconds = cpuMs,
                cpuPercentOfOneCore = cpuPercentOneCore,
                workingSetMiB = self.WorkingSet64 / 1048576d,
                privateMiB = self.PrivateMemorySize64 / 1048576d,
                note = "Isolated WPF test host, potentially including prior render tests; not the published launcher or a real Minecraft session. No forced GC or working-set trimming."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert(!session.IsCompleted && !window.IsVisible && !news.IsPlaying && news.CurrentIndex == slide,
                "Carousel remains stopped beyond its normal five-second interval throughout the game");
            var exitCode = await session.WaitAsync(TimeSpan.FromSeconds(10));
            window.EndAuditProgress();
            Assert(filesBefore.All(f => File.Exists(f.Key) && f.Value.SequenceEqual(File.ReadAllBytes(f.Key))),
                "Background wait and restoration preserve the isolated profile and installation files");
            window.SetAuditState("1.4.7", Check("1.4.7", false));
            Assert(exitCode == 0 && window.IsVisible && window.WindowState == WindowState.Normal,
                "Normal game exit restores the launcher automatically");
            Assert(!unrelated.HasExited, "Restoration follows the launched process, independent of unrelated processes");
            Assert(window.Width == width && window.Height == height && Get<Button>(window, "PrimaryButton").IsEnabled,
                "Restored launcher retains its size and can launch another session");
            Capture(window, "game-exit-restored");
        }
        // Never leave the test's own long-lived helper behind. This is not Java or user data.
        unrelated.Kill();
        await unrelated.WaitForExitAsync();

        window.SetAuditProgress("Preparando Minecraft", .7);
        window.WindowState = WindowState.Minimized;
        using (var crash = StartGameFixture(350, 23))
        {
            var exitCode = await window.FollowGameProcessAsync(crash).WaitAsync(TimeSpan.FromSeconds(5));
            window.EndAuditProgress();
            Assert(exitCode == 23 && window.IsVisible && window.WindowState == WindowState.Normal,
                "Abnormal game exit restores even a previously minimized launcher and retains the error code");
        }
        using (var instant = StartGameFixture(0, 7))
        {
            await instant.WaitForExitAsync();
            window.SetAuditProgress("Preparando Minecraft", .7);
            Assert(await window.FollowGameProcessAsync(instant) == 7 && window.IsVisible,
                "An already-exited game is handled without losing the exit notification");
            window.EndAuditProgress();
        }
        window.Close();
    }

    private static Process StartGameFixture(int milliseconds, int exitCode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--game-process");
        start.ArgumentList.Add(milliseconds.ToString());
        start.ArgumentList.Add(exitCode.ToString());
        return Process.Start(start)!;
    }
    private static PackUpdateCheck Check(string version, bool changed, string slug = "destiny") => new(new PackManifest { PackId = slug, Version = version }, changed ? [new PackManifestFile { Path = "mods/fixture.jar" }] : [], [], []);
    private static string Text(Window w, string name) => Get<TextBlock>(w, name).Text;
    private static T Get<T>(Window w, string name) where T : FrameworkElement => (T)w.FindName(name);
    private static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); Passed.Add(name); }
    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte c) { var s = c / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        static double Light(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        var a = Light(first); var b = Light(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(210); }
    private static async Task NewsSettled(NewsCarousel news)
    {
        var outgoing = (Image)news.FindName("PreviousImage");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (outgoing.Source is not null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert(outgoing.Source is null, "News transition finishes and releases its animation frames");
    }
    private static void HorizontalBounds(Window window, FrameworkElement control, string description)
    {
        var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
        Assert(bounds.Left >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Width > 0, description);
    }
    private static void Capture(Window window, string name, double scale = 1)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_output, name + ".png")); encoder.Save(file);
    }

    private static void AssertNativeCorners(Window window)
    {
        // RenderTargetBitmap cannot reveal the opaque HWND outside the WPF clip.
        var handle = new WindowInteropHelper(window).Handle;
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            var hasBounds = GetWindowRect(handle, out var bounds);
            Assert(GetWindowRgn(handle, region) > 0 && hasBounds, "Native window has a readable clipping region");
            var dpi = VisualTreeHelper.GetDpi(window);
            var dx = (int)Math.Round(2 * dpi.DpiScaleX);
            var dy = (int)Math.Round(6 * dpi.DpiScaleY);
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            foreach (var (x, y) in new[] { (dx, dy), (width - 1 - dx, dy), (dx, height - 1 - dy), (width - 1 - dx, height - 1 - dy) })
                Assert(!PtInRegion(region, x, y), $"Native corner excludes the transparent outer band at {width}x{height} ({x}, {y})");
            Assert(PtInRegion(region, (int)Math.Round(10 * dpi.DpiScaleX), (int)Math.Round(10 * dpi.DpiScaleY)), "Native rounding preserves content inside the visible curve");
        }
        finally { DeleteObject(region); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);

    private static void CaptureLogoComparison(Window window)
    {
        var board = new Grid { Width = 720, Height = 150, Background = new SolidColorBrush(Color.FromRgb(18, 19, 16)) };
        var original = new BitmapImage(new Uri("pack://application:,,,/DestinyLauncher;component/Assets/experiment/obsidian-gold-v1/destiny-emblem.png"));
        var optimized = Get<Image>(window, "TitleEmblem").Source;
        var variants = new[] { ("Antes · 22 px", 22d, (ImageSource)original, BitmapScalingMode.LowQuality), ("Alta calidad · 22 px", 22d, optimized, BitmapScalingMode.HighQuality), ("Título · 28 px", 28d, optimized, BitmapScalingMode.HighQuality), ("Sidebar · 40 px", 40d, optimized, BitmapScalingMode.HighQuality) };
        for (var i = 0; i < variants.Length; i++)
        {
            board.ColumnDefinitions.Add(new ColumnDefinition());
            var (title, size, source, sampling) = variants[i];
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var logo = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(logo, sampling);
            panel.Children.Add(new Border { Height = 50, Child = logo });
            panel.Children.Add(new TextBlock { Text = title, Foreground = Brushes.Wheat, FontSize = 11, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(panel, i); board.Children.Add(panel);
        }
        board.Measure(new Size(720, 150)); board.Arrange(new Rect(0, 0, 720, 150)); board.UpdateLayout();
        var image = new RenderTargetBitmap(720, 150, 96, 96, PixelFormats.Pbgra32); image.Render(board);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(_output, "logo-sampling-comparison.png")); encoder.Save(file);
    }
}
