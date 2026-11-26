using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher.Linux;

public sealed partial class MainWindow : Window
{
    private readonly LinuxNewsFeedService _newsFeed;
    private IReadOnlyList<LinuxNewsSlide> _newsSlides = [];
    private readonly LinuxLauncherService _launcher;
    private readonly LinuxDiscordLinkService _discord;
    private readonly LinuxPlayerHeadService _heads;
    private readonly bool _requestedStaff;
    private bool _discordChecked;
    private readonly DispatcherTimer _newsTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _uiLifetime = new();
    private CancellationTokenSource? _packCancellation;
    private IReadOnlyDictionary<PackEdition, PackCatalogEntry> _catalog = new Dictionary<PackEdition, PackCatalogEntry>();
    private HeroVideoPlayer? _video;
    private Window? _activeDialog;
    private Bitmap? _smallEmblem;
    private int _newsIndex;
    private bool _settingsLoaded, _isBusy, _syncingRam, _checkingCatalog;
    private bool _removingLauncher;
    private bool _allowClose;
    private string _page = "home";
    private string? _catalogError;
    private GameWindowMode _windowMode;
    private string _installationFolderName = "Destiny";

    public MainWindow() : this(null) { }
    public MainWindow(string? dataRoot)
    {
        InitializeComponent();
        WaylandProperties.SetAppId(this, "destiny-launcher");
        if (App.IsVisualAudit) { Width = App.ExportWidth; Height = App.ExportHeight; }
        else if (Screens.Primary is { } screen)
        {
            Width = Math.Min(1000, screen.WorkingArea.Width / screen.Scaling * .94);
            Height = Math.Min(620, screen.WorkingArea.Height / screen.Scaling * .94);
            MinWidth = Math.Min(760, Width); MinHeight = Math.Min(500, Height);
        }
        _launcher = new LinuxLauncherService(dataRoot);
        if (App.InitialEdition is { } initialEdition) _launcher.Settings.SelectedEdition = initialEdition;
        _heads = new LinuxPlayerHeadService(_launcher.Paths);
        _newsFeed = new LinuxNewsFeedService(_launcher.Paths);
        _newsSlides = LinuxNewsFeedService.BundledNews();
        _discord = new LinuxDiscordLinkService(_launcher.Paths);
        _requestedStaff = _launcher.Settings.SelectedEdition == PackEdition.DestinyStaff;
        _launcher.SetStaffAccess(false);
        foreach (var button in new[] { DestinyButton, LiteButton, StaffButton })
            if (button.Content is Grid artwork)
                artwork.SizeChanged += (_, _) => artwork.Clip = new RectangleGeometry(new Rect(artwork.Bounds.Size), 7, 7);
        DestinyButton.PointerEntered += (_, _) => DestinyArtwork.Opacity = 1;
        LiteButton.PointerEntered += (_, _) => LiteArtwork.Opacity = 1;
        DestinyButton.PointerExited += (_, _) => DestinyArtwork.Opacity = _launcher.Settings.SelectedEdition == PackEdition.Destiny ? 1 : .8;
        LiteButton.PointerExited += (_, _) => LiteArtwork.Opacity = _launcher.Settings.SelectedEdition == PackEdition.DestinyLite ? 1 : .8;
        StaffButton.PointerEntered += (_, _) => StaffArtwork.Opacity = 1;
        StaffButton.PointerExited += (_, _) => StaffArtwork.Opacity = _launcher.Settings.SelectedEdition == PackEdition.DestinyStaff ? 1 : .8;
        TestModeText.IsVisible = dataRoot is not null && !App.IsVisualAudit;
        LauncherVersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        LoadArtwork();
        RefreshProfile();
        LoadSettings();
        UpdateVersions();
        ShowNews(0);
        _newsTimer.Tick += (_, _) => ShowNews((_newsIndex + 1) % _newsSlides.Count);
        if (!App.IsVisualAudit) _newsTimer.Start();
        ConfigureNewsInteraction();
        WindowFrame.SizeChanged += (_, _) =>
        {
            WindowFrame.Clip = new RectangleGeometry(new Rect(WindowFrame.Bounds.Size), 18, 18);
            ApplyResponsiveLayout();
        };
        HeroRegion.SizeChanged += (_, _) => ApplyResponsiveLayout();
        InstallationContent.SizeChanged += (_, _) => UpdateEditionLayout();
        Opened += async (_, _) =>
        {
            ApplyResponsiveLayout();
            UpdateVideo();
            if (!App.IsVisualAudit) _ = CheckLauncherUpdateAsync();
            if (App.IsVisualAudit)
            {
                if (App.ExportPage == "profile") _ = ShowProfileDialogAsync();
                else if (App.ExportPage == "accounts")
                {
                    _launcher.Settings.Accounts = ["DestinyPlayer", "Explorador"];
                    _launcher.Settings.PlayerName = "DestinyPlayer";
                    _ = ShowAccountsAsync();
                }
                else if (App.ExportPage == "add-account") _ = ShowAddAccountAsync();
                else if (App.ExportPage == "repair") _ = ShowRepairAsync();
                else if (App.ExportPage == "game-menu") GameOptions_Click(null, new RoutedEventArgs());
                else if (App.ExportPage == "settings-bottom") { ShowPage("settings"); await Task.Delay(50); SettingsScroll.ScrollToEnd(); }
                else ShowPage(App.ExportPage);
                await Task.Delay(250);
                ExportVisualAudit();
                Close();
            }
        };
        Activated += (_, _) => { UpdateVideo(); UpdateNewsPlayback(); };
        Deactivated += (_, _) => { UpdateVideo(); _newsTimer.Stop(); SettleNewsTransition(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty) { UpdateVideo(); UpdateNewsPlayback(); }
        };
        Closed += (_, _) => ReleaseResources();
        Closing += async (_, e) =>
        {
            if (_removingLauncher) { e.Cancel = true; return; }
            if (_allowClose || _page != "settings" || !SettingsHaveChanges()) return;
            e.Cancel = true;
            if (await ConfirmDiscardSettingsAsync()) { _allowClose = true; Close(); }
        };
        if (!App.IsVisualAudit) { _ = RefreshCatalogAsync(); _ = LoadNewsAsync(); }
        else SetStatus("", "");
    }

    private void ReleaseResources()
    {
        _uiLifetime.Cancel();
        _headRequest?.Cancel(); _headRequest?.Dispose(); _headRequest = null;
        _newsTimer.Stop(); SettleNewsTransition();
        _video?.Dispose(); _video = null;
        TitleEmblem.Source = SidebarEmblem.Source = null;
        _smallEmblem?.Dispose(); _smallEmblem = null;
        foreach (var image in new[] { PlayerHead, HeroPosterImage, DestinyArtwork, LiteArtwork, StaffArtwork })
        {
            var source = image.Source as IDisposable;
            image.Source = null;
            source?.Dispose();
        }
        NewsImage.Source = null;
        foreach (var slide in _newsSlides) slide.Image.Dispose();
        _newsSlides = [];
        _heads.Dispose(); _newsFeed.Dispose(); _launcher.Dispose();
    }

    private void ExportVisualAudit()
    {
        var path = App.ExportPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Visual target = WindowFrame;
        var size = new Size(Width, Height);
        if (App.ExportPage is "profile" or "accounts" or "add-account" or "repair" && _activeDialog?.Content is Control dialogSurface) { target = dialogSurface; size = new Size(_activeDialog.Width, _activeDialog.Height); }
        if (App.ExportPage == "game-menu" && GameOptionsButton.ContextMenu is { } menu) { target = menu; size = menu.Bounds.Size; }
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
        bitmap.Render(target);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        var textMetrics = target.GetVisualDescendants().OfType<TextBlock>().Where(item => item.IsEffectivelyVisible).Select(item =>
        {
            var point = item.TranslatePoint(default, target) ?? default;
            FontManager.Current.TryGetGlyphTypeface(new Typeface(item.FontFamily, item.FontStyle, item.FontWeight, item.FontStretch), out var glyph);
            return new { item.Text, font = item.FontFamily.ToString(), actualFont = glyph?.FamilyName, actualWeight = glyph?.Weight.ToString(), weight = item.FontWeight.ToString(), size = item.FontSize, x = point.X, y = point.Y, width = item.Bounds.Width, height = item.Bounds.Height };
        });
        File.WriteAllText(Path.ChangeExtension(path, ".text.json"), System.Text.Json.JsonSerializer.Serialize(textMetrics, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var controls = new Control[] { HeroRegion, HeroMark, NewsCard, ProfileButton, LaunchPanel, PrimaryButton, GameOptionsButton, HomeNav, InstallationNav, SettingsNav, EditionGrid, SettingsView, InstallationContent, InstallDetailsPanel, InstallationActions, InstallPrimaryButton };
        var layout = controls.ToDictionary(control => control.Name ?? control.GetType().Name, control =>
        {
            var point = control.TranslatePoint(default, WindowFrame) ?? default;
            return new { x = point.X, y = point.Y, width = control.Bounds.Width, height = control.Bounds.Height };
        });
        File.WriteAllText(path + ".layout.json", System.Text.Json.JsonSerializer.Serialize(layout, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (App.ExportPage == "home") File.WriteAllText(path + ".poster.json", System.Text.Json.JsonSerializer.Serialize(new { bounds = HeroPosterImage.Bounds.ToString(), source = HeroPosterImage.Source?.Size.ToString() }));
        var primitives = WindowFrame.GetVisualDescendants().OfType<Control>()
            .Where(control => control is Thumb or RepeatButton or ScrollBar)
            .Select(control => { var p = control.TranslatePoint(default, WindowFrame) ?? default; return new { type = control.GetType().Name, control.Name, p.X, p.Y, width = control.Bounds.Width, height = control.Bounds.Height }; });
        File.WriteAllText(path + ".primitives.json", System.Text.Json.JsonSerializer.Serialize(primitives, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        _activeDialog?.Close();
    }

    private static Bitmap LoadBitmap(string asset, int? width = null)
    {
        using var stream = AssetLoader.Open(new Uri("avares://DestinyLauncher.Linux/Assets/" + asset));
        return width is { } target ? Bitmap.DecodeToWidth(stream, target, BitmapInterpolationMode.HighQuality) : new Bitmap(stream);
    }
    private void LoadArtwork()
    {
        try { HeroPosterImage.Source = LoadBitmap("Video/reino-poster.jpg"); }
        catch (Exception ex) { LogVideoError("Póster: " + ex.Message); }
        try { _smallEmblem = LoadBitmap("experiment/obsidian-gold-v1/destiny-emblem.png", 112); TitleEmblem.Source = SidebarEmblem.Source = _smallEmblem; }
        catch (Exception ex) { LogVideoError("Emblema: " + ex.Message); }
        DestinyArtwork.Source = LoadBitmap("Modpacks/destiny-v1.jpg", 600);
        LiteArtwork.Source = LoadBitmap("Modpacks/lite-v1.jpg", 600);
        StaffArtwork.Source = LoadBitmap("Modpacks/staff-v1.jpg", 600);
    }
    private void LogVideoError(string message)
    {
        try { File.AppendAllText(Path.Combine(_launcher.Paths.Logs, "interface.log"), $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}"); } catch { }
        if (!App.IsVisualAudit) SetStatus("Vídeo de fondo no disponible", message, true);
    }

    private void UpdateVideo()
    {
        if (_uiLifetime.IsCancellationRequested || App.StaticPreview || App.IsVisualAudit) return;
        var action = VideoPlaybackPolicy.Select(_isBusy, IsVisible, IsActive, WindowState == WindowState.Minimized, _page == "home");
        if (action == VideoPlaybackAction.Release)
        {
            _video?.Dispose(); _video = null;
            return;
        }
        if (action == VideoPlaybackAction.Pause) { _video?.Pause(); return; }
        if (_video is null)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Video", "reino-rayos-v6.mp4");
                if (File.Exists(path)) _video = new HeroVideoPlayer(HeroVideoImage, path, LogVideoError);
                else LogVideoError("No se encontró el vídeo de fondo.");
            }
            catch (Exception ex) { LogVideoError(ex.Message); }
        }
        else _video.Play();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    private void Home_Click(object? sender, RoutedEventArgs e) => ShowPage("home");
    private void Installation_Click(object? sender, RoutedEventArgs e) => ShowPage("installation");
    private void Settings_Click(object? sender, RoutedEventArgs e) => ShowPage("settings");
    private async void ShowPage(string page)
    {
        if (_page == page) return;
        if (_page == "settings" && SettingsHaveChanges() && !await ConfirmDiscardSettingsAsync()) return;
        _page = page;
        HomeView.IsVisible = page == "home";
        InstallationView.IsVisible = page == "installation";
        SettingsView.IsVisible = page == "settings";
        StatusFooter.IsVisible = page != "settings" && (!string.IsNullOrEmpty(StatusText.Text) || _isBusy);
        HomeNav.Classes.Set("selected", page == "home");
        InstallationNav.Classes.Set("selected", page == "installation");
        SettingsNav.Classes.Set("selected", page == "settings");
        if (page == "settings") LoadSettings();
        UpdateVideo();
        UpdateNewsPlayback();
        ApplyResponsiveLayout();
        UiMotion.Enter(page == "home" ? HomeView : page == "installation" ? InstallationView : SettingsView);
    }

    private void ApplyResponsiveLayout()
    {
        if (HeroMark is null) return;
        var narrow = Bounds.Width < 860;
        // WPF's poster/video retain their natural aspect height in the scroll viewport.
        // Top-align the same canvas before clipping, rather than recropping its center.
        HeroPosterImage.Height = HeroVideoImage.Height = Math.Max(HeroRegion.Bounds.Height, Snap(HeroRegion.Bounds.Width * 9 / 16));
        ShellGrid.ColumnDefinitions[0].Width = new GridLength(narrow ? 70 : 80);
        InstallPrimaryButton.MinWidth = Snap(210);
        var compact = HeroRegion.Bounds.Height < 380;
        HeroMark.Width = compact ? 190 : narrow ? 230 : 250;
        HeroMark.Height = Snap(compact ? 106 : 178);
        HeroMark.Margin = new Thickness(narrow ? 20 : 28, compact ? 8 : 22, 0, 0);
        NewsCard.Width = narrow || compact ? 300 : 350;
        NewsCard.Height = Snap(NewsCard.Width / 2.24);
        NewsCard.Margin = new Thickness(narrow ? 20 : 28, 0, 0, 22);
        ProfileButton.Width = Snap(narrow ? 214 : 234);
        ProfileButton.Margin = new Thickness(0, 0, narrow ? 20 : 28, 22);
        InstallationView.Margin = new Thickness(narrow ? 20 : 28, 24, narrow ? 20 : 28, 20);
        LaunchPanel.Margin = new Thickness(narrow ? 20 : 28, 0, narrow ? 20 : 28, Bounds.Height < 560 ? 12 : 18);
        LaunchLayout.ColumnDefinitions[1].Width = new GridLength(narrow ? 264 : 290);
        UpdateEditionLayout();
    }
    private void UpdateEditionLayout()
    {
        if (EditionGrid is null || InstallationContent.Bounds.Width <= 0) return;
        var height = EditionGrid.Bounds.Height > 0 ? EditionGrid.Bounds.Height : 300;
        var count = _launcher.StaffAllowed ? 3 : 2;
        EditionGrid.ColumnDefinitions[3].Width = new GridLength(_launcher.StaffAllowed ? 16 : 0);
        EditionGrid.ColumnDefinitions[4].Width = _launcher.StaffAllowed ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        EditionGrid.Width = Snap(Math.Min(InstallationContent.Bounds.Width, count * Math.Max(160, height * .82) + (count - 1) * 16));
        var cardWidth = (EditionGrid.Width - (count - 1) * 16) / count;
        DestinyTitle.FontSize = LiteTitle.FontSize = StaffTitle.FontSize = cardWidth < 180 ? 19 : cardWidth < 210 ? 21 : 26;
    }
    private double Snap(double value) => Math.Round(value * RenderScaling, MidpointRounding.ToEven) / RenderScaling;

    private void Destiny_Click(object? sender, RoutedEventArgs e) => Select(PackEdition.Destiny);
    private void Lite_Click(object? sender, RoutedEventArgs e) => Select(PackEdition.DestinyLite);
    private async void Staff_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        await RefreshDiscordAsync();
        if (_launcher.StaffAllowed) Select(PackEdition.DestinyStaff);
    }
    private void Select(PackEdition edition)
    {
        if (_isBusy) return;
        try { _launcher.SelectEdition(edition); UpdateVersions(); UpdateReadyStatus(); }
        catch (Exception ex) { SetStatus("No se pudo cambiar la edición", ex.Message, true); }
    }
    private string InstancePath(PackEdition? edition = null) => _launcher.Paths.Instance((edition ?? _launcher.Settings.SelectedEdition) switch { PackEdition.DestinyLite => "destiny-lite", PackEdition.DestinyStaff => "destiny-staff", _ => "destiny" });
    private string InstalledVersion(PackEdition? edition = null)
    {
        var slug = (edition ?? _launcher.Settings.SelectedEdition) == PackEdition.DestinyLite ? "destiny-lite" : "destiny";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(InstancePath(edition), "destiny-installed.json")));
            return document.RootElement.TryGetProperty("Version", out var value) ? value.GetString() ?? "" : "";
        }
        catch { return ""; }
    }
    private void UpdateVersions()
    {
        var lite = _launcher.Settings.SelectedEdition == PackEdition.DestinyLite;
        var staff = _launcher.Settings.SelectedEdition == PackEdition.DestinyStaff;
        var destiny = !lite && !staff;
        EditionText.Text = staff ? "Destiny Staff" : lite ? "Destiny Lite" : "Destiny";
        InstallEdition.Text = EditionText.Text;
        var current = InstalledVersion();
        var latest = _catalog.TryGetValue(_launcher.Settings.SelectedEdition, out var item) ? item.Version : _checkingCatalog ? "Consultando…" : "No disponible";
        PackInstalledVersionText.Text = InstallInstalledVersion.Text = string.IsNullOrEmpty(current) ? "No instalado" : current;
        PackLatestVersionText.Text = InstallLatestVersion.Text = latest;
        PackStateText.Text = string.IsNullOrEmpty(current) ? "Sin instalar" : current == latest ? "Actualizado" : _catalogError is null ? "Actualización disponible" : "Sin verificar";
        PackStateText.Foreground = Brush(string.IsNullOrEmpty(current) ? "#B3AC9E" : "#B3C09A");
        PrimaryLabel.Text = InstallPrimaryLabel.Text = _checkingCatalog ? "COMPROBANDO" : string.IsNullOrEmpty(current) ? (_catalogError is null ? "INSTALAR" : "REINTENTAR") : current == latest || !_catalog.ContainsKey(_launcher.Settings.SelectedEdition) ? "JUGAR" : "ACTUALIZAR";
        PrimaryButton.IsEnabled = InstallPrimaryButton.IsEnabled = GameOptionsButton.IsEnabled = !_isBusy && !_checkingCatalog;
        InstallationPathText.Text = _launcher.Paths.GameDirectory;
        ToolTip.SetTip(InstallationPathText, InstallationPathText.Text);
        OpenInstallationFolderButton.IsEnabled = !_isBusy && Directory.Exists(_launcher.Paths.GameDirectory);
        DestinyButton.Classes.Set("selected", destiny); LiteButton.Classes.Set("selected", lite); StaffButton.Classes.Set("selected", staff);
        StaffButton.IsVisible = _launcher.StaffAllowed;
        DestinyTick.IsVisible = DestinyAccent.IsVisible = destiny;
        LiteTick.IsVisible = LiteAccent.IsVisible = lite;
        StaffTick.IsVisible = StaffAccent.IsVisible = staff;
        DestinyArtwork.Opacity = destiny ? 1 : .8; LiteArtwork.Opacity = lite ? 1 : .8; StaffArtwork.Opacity = staff ? 1 : .8;
        DestinyState.Foreground = Brush(destiny ? "#E2C58B" : "#B3AC9E"); LiteState.Foreground = Brush(lite ? "#E2C58B" : "#B3AC9E"); StaffState.Foreground = Brush(staff ? "#E2C58B" : "#B3AC9E");
        DestinyRing.Background = Brush(destiny ? "#E2C58B" : "#A010110E"); LiteRing.Background = Brush(lite ? "#E2C58B" : "#A010110E"); StaffRing.Background = Brush(staff ? "#E2C58B" : "#A010110E");
        DestinyRing.BorderBrush = Brush(destiny ? "#E2C58B" : "#B0C6B68E"); LiteRing.BorderBrush = Brush(lite ? "#E2C58B" : "#B0C6B68E"); StaffRing.BorderBrush = Brush(staff ? "#E2C58B" : "#B0C6B68E");
        DestinyState.Text = string.IsNullOrEmpty(InstalledVersion(PackEdition.Destiny)) ? "Sin instalar" : "Instalada · " + InstalledVersion(PackEdition.Destiny);
        LiteState.Text = string.IsNullOrEmpty(InstalledVersion(PackEdition.DestinyLite)) ? "Sin instalar" : "Instalada · " + InstalledVersion(PackEdition.DestinyLite);
        StaffState.Text = string.IsNullOrEmpty(InstalledVersion(PackEdition.DestinyStaff)) ? "Sin instalar" : "Instalada · " + InstalledVersion(PackEdition.DestinyStaff);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshCatalogAsync();
    private async Task RefreshCatalogAsync()
    {
        if (_checkingCatalog || _uiLifetime.IsCancellationRequested) return;
        _checkingCatalog = true; _catalogError = null;
        SetStatus("Comprobando modpack", "Buscando la versión disponible…");
        Progress.IsVisible = Progress.IsIndeterminate = true;
        RetryCheckButton.IsVisible = false;
        UpdateVersions();
        try
        {
            if (!_discordChecked && !App.IsVisualAudit) await RefreshDiscordAsync();
            _catalog = (await _launcher.RefreshCatalogAsync(_uiLifetime.Token)).ToDictionary(entry => entry.Edition);
        }
        catch (OperationCanceledException) when (_uiLifetime.IsCancellationRequested) { }
        catch (Exception ex) { _catalogError = ex.Message; }
        finally
        {
            _checkingCatalog = false; Progress.IsVisible = false;
            if (!_uiLifetime.IsCancellationRequested) { UpdateVersions(); UpdateReadyStatus(); }
        }
    }
    private void UpdateReadyStatus()
    {
        RetryCheckButton.IsVisible = _catalogError is not null;
        if (_catalogError is not null) SetStatus("No se pudo comprobar el modpack", _catalogError, true);
        else SetStatus("", "");
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_page == "home" && string.IsNullOrEmpty(InstalledVersion())) { ShowPage("installation"); return; }
        if (!LauncherConfig.ValidPlayerName(_launcher.Settings.PlayerName)) { await ShowProfileDialogAsync(); return; }
        if (_catalogError is not null && string.IsNullOrEmpty(InstalledVersion())) { await RefreshCatalogAsync(); return; }
        ShowPage("home");
        await LaunchAsync();
    }
    private async Task LaunchAsync()
    {
        if (_isBusy) return;
        if (_launcher.Settings.SelectedEdition == PackEdition.DestinyStaff)
        {
            await RefreshDiscordAsync();
            if (!_launcher.StaffAllowed) { await InformAsync("Destiny Staff", "Tu cuenta de Discord no tiene acceso a este modpack."); return; }
        }
        var signedIn = !string.IsNullOrWhiteSpace(_launcher.Settings.MicrosoftAccountId);
        if (!signedIn && !LauncherConfig.ValidPlayerName(_launcher.Settings.PlayerName))
        {
            await ShowProfileDialogAsync(); return;
        }
        SetBusy(true, "Preparando el modpack y Java…");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_uiLifetime.Token);
        _packCancellation = cancellation;
        CancelDownloadButton.IsEnabled = true;
        try
        {
            var progress = new Progress<(string status, double value)>(SetDownloadProgress);
            using var session = signedIn
                ? await _launcher.LaunchMicrosoftAsync(_launcher.Settings.SelectedEdition, progress, cancellation.Token)
                : await _launcher.LaunchOfflineAsync(_launcher.Settings.SelectedEdition, _launcher.Settings.PlayerName, progress, cancellation.Token);
            CancelDownloadButton.IsVisible = false;
            SetStatus("Minecraft se está ejecutando", "");
            if (!session.Process.HasExited) Hide();
            try { await session.Process.WaitForExitAsync(_uiLifetime.Token); }
            finally { if (session.Process.HasExited) await session.CompleteAsync(); }
            SetStatus(session.Process.ExitCode == 0 ? "" : $"Minecraft terminó con código {session.Process.ExitCode}.", "");
        }
        catch (OperationCanceledException) when (_uiLifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { SetStatus("Descarga cancelada", "Puedes volver a intentarlo cuando quieras."); }
        catch (Exception ex) { SetStatus("No se pudo iniciar Minecraft", ex.Message, true); }
        finally { _packCancellation = null; if (!_uiLifetime.IsCancellationRequested) { if (!IsVisible) { WindowState = WindowState.Normal; Show(); Activate(); } SetBusy(false); } }
    }

    private void LoadSettings()
    {
        _settingsLoaded = false;
        var settings = _launcher.Settings;
        RefreshDiscordStatus();
        _installationFolderName = LauncherPaths.InstallationFolderName(settings.InstallDirectory);
        InstallDirectory.Text = LauncherPaths.InstallationBaseDirectory(settings.InstallDirectory);
        InstallDirectorySuffix.Text = Path.DirectorySeparatorChar + _installationFolderName;
        RamSlider.Maximum = Math.Max(1, MemoryPolicy.System.MaximumGb);
        RamSlider.Value = Math.Clamp(settings.RamGb, 1, RamSlider.Maximum);
        RamValue.Text = ((int)RamSlider.Value).ToString();
        JavaArguments.Text = settings.JavaArguments;
        MultiAccountToggle.IsChecked = settings.MultiAccountEnabled;
        AutoJoinToggle.IsChecked = settings.AutoJoinServer;
        GameWidth.Text = settings.GameWidth.ToString(); GameHeight.Text = settings.GameHeight.ToString();
        _windowMode = settings.WindowMode;
        UpdateWindowMode(); SettingsValidation.IsVisible = false;
        _settingsLoaded = true;
    }
    private bool SettingsHaveChanges()
    {
        if (!_settingsLoaded) return false;
        var settings = _launcher.Settings;
        return InstallDirectory.Text != LauncherPaths.InstallationBaseDirectory(settings.InstallDirectory)
            || RamValue.Text != settings.RamGb.ToString() || JavaArguments.Text != settings.JavaArguments
            || (MultiAccountToggle.IsChecked == true) != settings.MultiAccountEnabled || (AutoJoinToggle.IsChecked == true) != settings.AutoJoinServer
            || _windowMode != settings.WindowMode || GameWidth.Text != settings.GameWidth.ToString() || GameHeight.Text != settings.GameHeight.ToString();
    }
    private void WindowMode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode } && Enum.TryParse<GameWindowMode>(mode, out var value)) { _windowMode = value; UpdateWindowMode(); }
    }
    private void UpdateWindowMode()
    {
        WindowedMode.Classes.Set("selected", _windowMode == GameWindowMode.Windowed);
        MaximizedMode.Classes.Set("selected", _windowMode == GameWindowMode.Maximized);
        FullscreenMode.Classes.Set("selected", _windowMode == GameWindowMode.Fullscreen);
        GameWidth.IsEnabled = GameHeight.IsEnabled = _windowMode == GameWindowMode.Windowed;
    }
    private void RamSlider_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_settingsLoaded || _syncingRam) return;
        _syncingRam = true; RamValue.Text = ((int)Math.Round(e.NewValue)).ToString(); _syncingRam = false;
    }
    private void RamValue_Changed(object? sender, TextChangedEventArgs e)
    {
        if (!_settingsLoaded || _syncingRam || !int.TryParse(RamValue.Text, out var value)) return;
        _syncingRam = true; RamSlider.Value = Math.Clamp(value, 1, RamSlider.Maximum); _syncingRam = false;
        SettingsValidation.IsVisible = value > MemoryPolicy.System.MaximumGb;
        if (SettingsValidation.IsVisible) SettingsValidation.Text = MemoryPolicy.System.Warning;
    }
    private void ResetJava_Click(object? sender, RoutedEventArgs e) => JavaArguments.Text = LauncherConfig.DefaultJavaArguments;
    private void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = (InstallDirectory.Text ?? "").Trim();
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Elige una ruta completa para la instalación.");
            if (!int.TryParse(RamValue.Text, out var ram) || ram < 1) throw new InvalidOperationException("Introduce una cantidad de RAM válida en GB.");
            if (ram > MemoryPolicy.System.MaximumGb) throw new InvalidOperationException(MemoryPolicy.System.Warning);
            if (!int.TryParse(GameWidth.Text, out var width) || width is < 640 or > 7680) throw new InvalidOperationException("El ancho debe estar entre 640 y 7680 píxeles.");
            if (!int.TryParse(GameHeight.Text, out var height) || height is < 360 or > 4320) throw new InvalidOperationException("El alto debe estar entre 360 y 4320 píxeles.");
            DestinyLauncher.Services.JavaArguments.ValidateMemoryOverrides(JavaArguments.Text ?? "");
            var settings = _launcher.Settings;
            settings.InstallDirectory = Path.Combine(Path.GetFullPath(path), _installationFolderName);
            settings.RamGb = ram; settings.JavaArguments = JavaArguments.Text ?? "";
            settings.MultiAccountEnabled = MultiAccountToggle.IsChecked == true; settings.AutoJoinServer = AutoJoinToggle.IsChecked == true;
            settings.WindowMode = _windowMode; settings.GameWidth = width; settings.GameHeight = height;
            _launcher.SetInstallDirectory(settings.InstallDirectory); _launcher.SaveSettings();
            SettingsValidation.IsVisible = false; LoadSettings(); ShowPage("home"); UpdateVersions(); RefreshProfile();
            SetStatus("Ajustes guardados", "Se aplicarán a la próxima partida.");
            StatusDot.Fill = Brush("#B3C09A");
        }
        catch (Exception ex) { SettingsValidation.Text = ex.Message; SettingsValidation.IsVisible = true; }
    }
    private async void BrowseInstallDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Carpeta de instalación", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].Path.IsFile) InstallDirectory.Text = folders[0].Path.LocalPath;
    }

    private void OpenInstallationFolder_Click(object? sender, RoutedEventArgs e) { if (Directory.Exists(_launcher.Paths.GameDirectory)) OpenPath(_launcher.Paths.GameDirectory); }
    private void Social_Click(object? sender, RoutedEventArgs e) { if (sender is Button { Tag: string url }) OpenPath(url); }
    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("No se pudo abrir el enlace o la carpeta", ex.Message, true); }
    }
    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy; Progress.IsVisible = Progress.IsIndeterminate = busy;
        if (!busy) CancelDownloadButton.IsVisible = false;
        PrimaryButton.IsEnabled = InstallPrimaryButton.IsEnabled = GameOptionsButton.IsEnabled = !busy && !_checkingCatalog;
        DestinyButton.IsEnabled = LiteButton.IsEnabled = StaffButton.IsEnabled = !busy;
        UpdateVideo();
        UpdateNewsPlayback();
        if (message is not null) SetStatus(message, "");
    }
    private void SetDownloadProgress((string status, double value) update)
    {
        if (!_isBusy || _uiLifetime.IsCancellationRequested || _packCancellation?.IsCancellationRequested == true) return;
        var downloading = update.status.StartsWith("Descargando modpack ·", StringComparison.Ordinal) || update.status.StartsWith("Descargando mods ·", StringComparison.Ordinal);
        CancelDownloadButton.IsVisible = downloading && _packCancellation is not null;
        var value = LauncherPresentation.Progress(update.status, update.value);
        Progress.IsIndeterminate = value is null;
        if (value is double ratio) Progress.Value = ratio;
        SetStatus(LauncherPresentation.ProgressStatus(update.status), "");
    }
    private void CancelDownload_Click(object? sender, RoutedEventArgs e)
    {
        CancelDownloadButton.IsEnabled = false;
        _packCancellation?.Cancel();
        SetStatus("Cancelando descarga…", "");
    }
    private void SetStatus(string title, string detail, bool warning = false)
    {
        StatusText.Text = title; StatusDetail.Text = detail;
        StatusDot.Fill = Brush(warning ? "#D7B779" : "#C6A362");
        StatusFooter.IsVisible = _page != "settings" && (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(detail) || _isBusy);
    }
    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}


