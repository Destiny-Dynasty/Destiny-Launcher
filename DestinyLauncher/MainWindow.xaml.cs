using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherPaths _paths;
    private readonly ConfigService _configService;
    private readonly AppLogger _log;
    private readonly GitHubPackUpdater _manifestUpdater;
    private readonly MinecraftService _minecraft;
    private readonly PlayerHeadService _playerHeadService;
    private readonly NewsFeedService _newsFeed;
    private readonly DiscordLinkService _discord;
    private readonly bool _previewMode;
    private readonly bool _loadRemoteSkinInPreview;
    private readonly BackgroundVideoController _backgroundVideo;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _optionalActivity = new();
    private CancellationTokenSource? _manifestCancellation;
    private readonly Dictionary<PackEdition, PackUpdateCheck> _editionChecks = [];
    private LauncherConfig _config;
    private PackUpdateCheck? _manifestCheck;
    private string? _manifestError;
    private bool _localFallbackAllowed = true;
    private bool _checkingManifest;
    private bool _busy;
    private bool _operationFailed;
    private int _manifestRefreshGeneration;
    private bool _closed;
    private bool _initialLoadStarted;
    private bool _gameRunning;
    private string? _playerHeadUsername;
    private CancellationTokenSource? _playerHeadCancellation;
    private Task? _playerHeadLoadTask;
    private readonly InstallationDataService _installationData;
    private readonly LauncherUpdateService? _launcherUpdates;
    private LauncherRelease? _pendingLauncherUpdate;
    private bool _updateDialogOpen;
    private bool _updateInstalling;
    private readonly bool _requestedStaff;

    public MainWindow(bool previewMode = false, bool loadRemoteSkinInPreview = false, bool startOnSettingsPage = false, string? dataRoot = null, PackEdition? initialEdition = null)
    {
        _previewMode = previewMode;
        _loadRemoteSkinInPreview = loadRemoteSkinInPreview;
        // Even offscreen previews must never create caches or logs in the real user profile.
        _paths = new LauncherPaths(dataRoot ?? (previewMode ? Path.Combine(Path.GetTempPath(), "DestinyLauncher-previews", Guid.NewGuid().ToString("N")) : null));
        _log = new AppLogger(_paths);
        _configService = new ConfigService(_paths);
        _manifestUpdater = new GitHubPackUpdater(_paths, _log);
        _minecraft = new MinecraftService(_paths, _log);
        _playerHeadService = new PlayerHeadService(_paths);
        _newsFeed = new NewsFeedService(_paths);
        _discord = new DiscordLinkService(_paths);
        var exists = File.Exists(_paths.ConfigFile);
        _config = previewMode ? new LauncherConfig { PlayerName = loadRemoteSkinInPreview ? "Shady935" : "", InstallDirectory = _paths.InstallDirectory } : _configService.Load();
        if (dataRoot is not null && !exists) _config.InstallDirectory = _paths.InstallDirectory;
        if (initialEdition is not null) _config.SelectedEdition = initialEdition.Value;
        _requestedStaff = _config.SelectedEdition == PackEdition.DestinyStaff;
        if (_requestedStaff) _config.SelectedEdition = PackEdition.Destiny;
        CreateDesktopShortcut = edition => GameShortcutService.Create(
            previewMode ? Path.Combine(_paths.Root, "desktop-preview") : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.ProcessPath ?? throw new InvalidOperationException("No se encontró el ejecutable del launcher."), edition, dataRoot);
        _paths.SetInstallDirectory(_config.InstallDirectory);
        _installationData = new InstallationDataService(_paths);
        if (!previewMode)
        {
            try { _installationData.RegisterConfigured(_config.InstallDirectory); }
            catch (Exception ex) { _log.Error("Instance registration was skipped", ex); }
        }
        var installation = LauncherInstallation.Read(AppContext.BaseDirectory);
        if (!previewMode && installation is { Experimental: false } && dataRoot?.Equals(installation.DataRoot, StringComparison.OrdinalIgnoreCase) != false)
            _launcherUpdates = new LauncherUpdateService(_paths);
        InitializeComponent();
        _backgroundVideo = new BackgroundVideoController(HeroVideo, this, _paths,
            () => !_busy && !_gameRunning && !_closed,
            ex => _log.Error("Animated background unavailable; keeping the still image", ex), !previewMode);
        ConfigureInitialWindowSize();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        WindowFrame.SizeChanged += (_, _) => UpdateFrameClip();
        StateChanged += (_, _) => UpdateWindowFrame();
        Hero.SizeChanged += (_, _) => ApplyResponsiveLayout();
        News.SetSlides(NewsFeedService.BundledNews());
        Loaded += async (_, _) =>
        {
            ApplyResponsiveLayout();
            // Showing the window again after a game must not repeat startup requests.
            if (_initialLoadStarted) return;
            _initialLoadStarted = true;
            if (!_previewMode) _ = LoadNewsAsync();
            if (_launcherUpdates is not null) _ = CheckLauncherUpdateAsync();
            if (!_previewMode && _discord.IsLinked && await RefreshDiscordAsync() && _requestedStaff) _config.SelectedEdition = PackEdition.DestinyStaff;
            await RefreshManifestAsync();
        };
        TestModeText.Visibility = dataRoot is not null ? Visibility.Visible : Visibility.Collapsed;
        LauncherVersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        UpdateWindowFrame();
        Refresh();
        if (startOnSettingsPage) ShowSettingsPage();
    }

    private PackDefinition Pack => _config.SelectedEdition switch { PackEdition.DestinyLite => _config.DestinyLite, PackEdition.DestinyStaff => _config.DestinyStaff, _ => _config.Destiny };
    internal Task PlayerHeadLoadTask => _playerHeadLoadTask ?? Task.CompletedTask;
    internal LauncherPaths DataPaths => _paths;
    internal Action<Uri> OpenExternalLink { get; set; } = uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    internal Action<string> OpenDirectory { get; set; } = path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    internal Func<PackEdition, string> CreateDesktopShortcut { get; set; }
    internal Func<Process, CancellationToken, Task<bool>>? GameWindowReady { get; set; }
    internal BackgroundVideoController BackgroundVideo => _backgroundVideo;

    private async Task CheckLauncherUpdateAsync()
    {
        try
        {
            _pendingLauncherUpdate = await _launcherUpdates!.CheckAsync(_optionalActivity.Token);
            ShowPendingLauncherUpdate();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) _log.Info("Launcher update check skipped: " + ex.Message); }
    }
    private void ShowPendingLauncherUpdate()
    {
        if (_pendingLauncherUpdate is null || _closed || _busy || _gameRunning || _updateDialogOpen
            || SettingsPageHost.Content is SettingsPage { HasChanges: true }) return;
        var release = _pendingLauncherUpdate; _pendingLauncherUpdate = null; _updateDialogOpen = true;
        try
        {
            var dialog = new UpdateDialog(_launcherUpdates!, release, AppContext.BaseDirectory);
            dialog.ShowStandalone(this);
            _updateInstalling = dialog.InstallerStarted;
            Close(); // Cancel/X exits too; the update window is now the sole foreground UI.
        }
        finally { _updateDialogOpen = false; }
    }
    private void RegisterCurrentInstances()
    {
        try { _installationData.RegisterConfigured(_config.InstallDirectory); }
        catch (Exception ex) { _log.Error("Could not register instance ownership", ex); }
    }

    private void ConfigureInitialWindowSize()
    {
        var work = SystemParameters.WorkArea;
        // Choose once at startup, honoring display scaling and small working areas.
        // Normal sessions stay locked; isolated previews can still exercise compact layouts.
        var width = Math.Min(1000, work.Width * .94);
        var height = Math.Min(620, work.Height * .94);
        MinWidth = _previewMode ? Math.Min(760, width) : width;
        MinHeight = _previewMode ? Math.Min(500, height) : height;
        MaxWidth = _previewMode ? double.PositiveInfinity : width;
        MaxHeight = _previewMode ? double.PositiveInfinity : height;
        Width = width;
        Height = height;
    }

    private void ApplyResponsiveLayout()
    {
        if (Hero is null) return;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var narrow = width < 860;
        var stacked = width < 740;
        SidebarColumn.Width = new GridLength(narrow ? 70 : 80);
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var compactHero = Hero.ActualHeight < 380;
        HeroMark.Width = compactHero ? 190 : narrow ? 230 : 250;
        HeroMark.Height = compactHero ? 106 : 178;
        HeroMark.Margin = new Thickness(narrow ? 20 : 28, compactHero ? 8 : 22, 0, 0);
        News.Width = narrow || compactHero ? 300 : 350;
        News.Height = News.Width / 2.24;
        News.Margin = new Thickness(narrow ? 20 : 28, 0, 0, 22);
        LaunchPanel.Margin = new Thickness(narrow ? 20 : 28, 0, narrow ? 20 : 28, height < 560 ? 12 : 18);
        ProfilePanel.Width = narrow ? 214 : 234;
        ProfilePanel.Margin = new Thickness(0, 0, narrow ? 20 : 28, 22);
        InstallationView.Margin = new Thickness(narrow ? 20 : 28, 24, narrow ? 20 : 28, 20);
        LaunchLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : narrow ? 264 : 290);
        Grid.SetRow(ActionPanel, stacked ? 1 : 0);
        Grid.SetColumn(ActionPanel, stacked ? 0 : 1);
        ActionPanel.Margin = stacked ? new Thickness(0, 18, 0, 0) : new Thickness(0);
    }

    private string Installed(PackDefinition pack) => _configService.InstalledVersion(pack.Slug);
    private async Task LoadNewsAsync()
    {
        var token = _optionalActivity.Token;
        try
        {
            var cached = await _newsFeed.ReadCacheAsync(token);
            if (!_closed && !token.IsCancellationRequested && cached is not null) News.SetSlides(cached);
            var fresh = await _newsFeed.RefreshAsync(token);
            if (!_closed && !token.IsCancellationRequested) { News.SetSlides(fresh); _log.Info($"Novedades disponibles: {fresh.Count}"); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Optional editorial content must never replace installation status or block Play.
            if (!_closed) _log.Info($"Novedades sin conexión; se conserva el contenido disponible ({ex.GetType().Name}).");
        }
    }
    private void Refresh()
    {
        if (_closed) return;
        PlayerNameText.Text = string.IsNullOrWhiteSpace(_config.PlayerName) ? "Añadir nombre" : _config.PlayerName;
        PlayerNameText.ToolTip = string.IsNullOrWhiteSpace(_config.PlayerName) ? "Elige tu nombre para jugar" : _config.PlayerName;
        PlayerInitial.Text = string.IsNullOrWhiteSpace(_config.PlayerName) ? "D" : _config.PlayerName[..1].ToUpperInvariant();
        RefreshAccountMenu();
        RefreshGameMenu();
        RefreshPlayerHead();
        EditionText.Text = Pack.Name;
        VersionText.Text = $"Minecraft {_config.MinecraftVersion} · Fabric";
        DestinyButton.IsChecked = _config.SelectedEdition == PackEdition.Destiny;
        LiteButton.IsChecked = _config.SelectedEdition == PackEdition.DestinyLite;
        StaffButton.Visibility = _discord.Access?.Staff == true ? Visibility.Visible : Visibility.Collapsed;
        StaffButton.IsChecked = _config.SelectedEdition == PackEdition.DestinyStaff;
        StaffState.Text = EditionStatus(PackEdition.DestinyStaff, _config.DestinyStaff);
        DestinyState.Text = EditionStatus(PackEdition.Destiny, _config.Destiny);
        LiteState.Text = EditionStatus(PackEdition.DestinyLite, _config.DestinyLite);
        DestinyState.Foreground = Brush(_config.SelectedEdition == PackEdition.Destiny ? "GoldBright" : "Muted");
        LiteState.Foreground = Brush(_config.SelectedEdition == PackEdition.DestinyLite ? "GoldBright" : "Muted");
        var installed = Installed(Pack);
        InstallationPathText.Text = _paths.Instance(Pack.Slug);
        InstallationPathText.ToolTip = InstallationPathText.Text;
        OpenInstallationFolderButton.IsEnabled = !_busy && Directory.Exists(InstallationPathText.Text);
        OpenInstallationFolderButton.ToolTip = OpenInstallationFolderButton.IsEnabled ? "Abrir carpeta del juego" : "Carpeta aún no creada";
        var presentation = LauncherPresentation.Describe(installed, _manifestCheck, _checkingManifest, _manifestError, _operationFailed, _localFallbackAllowed);
        PackInstalledVersionText.Text = string.IsNullOrEmpty(installed) ? "No instalado" : installed;
        PackInstalledVersionText.ToolTip = PackInstalledVersionText.Text;
        PackLatestVersionText.Text = _checkingManifest ? "Consultando…" : _manifestCheck?.Manifest.Version ?? "No disponible";
        PackLatestVersionText.ToolTip = PackLatestVersionText.Text;
        PackStateText.Text = presentation.State;
        PackStateText.Foreground = Brush(presentation.Tone);
        if (!_busy)
        {
            PrimaryLabel.Text = presentation.Action;
            PrimaryButton.IsEnabled = !_checkingManifest;
            InstallPrimaryButton.IsEnabled = !_checkingManifest;
            AutomationProperties.SetName(PrimaryButton, $"{presentation.Action} {Pack.Name}");
            AutomationProperties.SetName(InstallPrimaryButton, $"{presentation.Action} {Pack.Name}");
            SetNotice(presentation.Status, presentation.Detail, presentation.Tone);
        }
        RetryCheckButton.Visibility = !_busy && !_checkingManifest && _manifestError is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private string EditionStatus(PackEdition edition, PackDefinition pack)
    {
        var installed = Installed(pack);
        if (edition == _config.SelectedEdition && _checkingManifest) return "Comprobando…";
        if (string.IsNullOrEmpty(installed)) return "Sin instalar";
        if (_editionChecks.TryGetValue(edition, out var check))
            return installed != check.Manifest.Version ? "Actualización disponible" : $"Instalada · {installed}";
        return $"Instalada · {installed}";
    }

    private async void Edition_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var edition = Enum.Parse<PackEdition>(((FrameworkElement)sender).Tag!.ToString()!);
        if (edition == PackEdition.DestinyStaff && !await RefreshDiscordAsync()) return;
        if (edition == _config.SelectedEdition) return;
        var previousEdition = _config.SelectedEdition;
        _config.SelectedEdition = edition;
        _manifestCheck = null;
        _manifestError = null;
        _operationFailed = false;
        if (!_previewMode && !TrySaveConfig())
        {
            _config.SelectedEdition = previousEdition;
            DestinyButton.IsChecked = previousEdition == PackEdition.Destiny;
            LiteButton.IsChecked = previousEdition == PackEdition.DestinyLite;
            return;
        }
        Refresh();
        await RefreshManifestAsync();
    }

    private async void RetryCheck_Click(object sender, RoutedEventArgs e) => await RefreshManifestAsync();
    private async Task RefreshManifestAsync(bool manual = false)
    {
        if (_previewMode || _busy || _closed) return;
        _manifestCancellation?.Cancel();
        _manifestCancellation?.Dispose();
        _manifestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _manifestCancellation.CancelAfter(TimeSpan.FromSeconds(45));
        var token = _manifestCancellation.Token;
        var generation = ++_manifestRefreshGeneration;
        var edition = _config.SelectedEdition;
        var pack = Pack;
        _checkingManifest = true;
        _manifestError = null;
        _operationFailed = false;
        Refresh();
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        try
        {
            var manifest = await _manifestUpdater.ReadManifestAsync(pack, token);
            var check = new PackUpdateCheck(manifest, [], [], []);
            if (generation != _manifestRefreshGeneration || _closed) return;
            _manifestCheck = check;
            _editionChecks[edition] = check;
            _config.MinecraftVersion = check.Manifest.MinecraftVersion;
            _config.FabricLoaderVersion = check.Manifest.FabricLoaderVersion;
            pack.JavaVersion = check.Manifest.JavaVersion;
            _log.Info($"Manifest ready for {pack.Slug}: {check.Manifest.Version}");
        }
        catch (OperationCanceledException) when (_closed || generation != _manifestRefreshGeneration) { return; }
        catch (Exception ex)
        {
            if (generation != _manifestRefreshGeneration || _closed) return;
            _manifestCheck = null;
            _editionChecks.Remove(edition);
            _manifestError = GetFriendlyError(ex);
            _localFallbackAllowed = AllowsLocalFallback(ex);
            _log.Error("Could not check pack manifest", ex);
        }
        finally
        {
            if (generation == _manifestRefreshGeneration && !_closed)
            {
                _checkingManifest = false;
                Progress.Visibility = Visibility.Collapsed;
                Refresh();
                if (manual && _manifestCheck is not null && _manifestError is null)
                    SetNotice(string.IsNullOrEmpty(Installed(pack)) ? $"Versión disponible · {_manifestCheck.Manifest.Version}"
                        : Installed(pack) == _manifestCheck.Manifest.Version ? "No hay actualizaciones" : "Actualización disponible", "", "Gold");
            }
        }
    }

    private void GameOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _checkingManifest) return;
        RefreshGameMenu(); GameOptionsButton.ContextMenu.IsOpen = true;
    }

    private void RefreshGameMenu()
    {
        GameOptionsButton.IsEnabled = !_busy && !_checkingManifest;
        var menu = new ContextMenu { Style = (Style)FindResource("AccountMenu"), Width = 300, HorizontalOffset = GameOptionsButton.ActualWidth - 300,
            PlacementTarget = GameOptionsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        void Add(string label, RoutedEventHandler action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, Style = (Style)FindResource("GameMenuItem"), IsEnabled = enabled && !_busy };
            item.Click += action; menu.Items.Add(item);
        }
        Add("Ajustes del juego", (_, _) => ShowSettingsPage());
        Add("Crear acceso directo en el escritorio", (_, _) =>
        {
            try { CreateDesktopShortcut(_config.SelectedEdition); SetNotice("Acceso directo creado", "", "Success"); }
            catch (Exception ex) { _log.Error("Could not create desktop shortcut", ex); SetNotice("No se pudo crear el acceso directo", GetFriendlyError(ex), "Error"); }
        });
        Add("Ver capturas", (_, _) => OpenScreenshots(), Directory.Exists(_paths.Instance(Pack.Slug)));
        Add("Buscar actualizaciones", async (_, _) => await RefreshManifestAsync(manual: true), !_checkingManifest);
        menu.Items.Add(new Separator { Background = Brush("Line"), Margin = new Thickness(8, 4, 8, 4) });
        Add("Reparar modpack", Repair_Click, !_checkingManifest && Directory.Exists(_paths.Instance(Pack.Slug)));
        GameOptionsButton.ContextMenu = menu;
    }

    private void OpenScreenshots()
    {
        if (_busy || !Directory.Exists(_paths.Instance(Pack.Slug))) return;
        try
        {
            var screenshots = Path.Combine(_paths.Instance(Pack.Slug), "screenshots");
            Directory.CreateDirectory(screenshots); OpenDirectory(screenshots);
        }
        catch (Exception ex) { SetNotice("No se pudieron abrir las capturas", GetFriendlyError(ex), "Error"); }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _checkingManifest || !Directory.Exists(_paths.Instance(Pack.Slug))) return;
        var dialog = new RepairDialog(Pack.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (_previewMode) { SetNotice("Vista de prueba", "Las operaciones del juego están desactivadas en esta vista.", "Gold"); return; }
        SetBusy(true, "Comprobando archivos");
        var success = false;
        string? error = null;
        try
        {
            var pack = Pack;
            var initialInstall = string.IsNullOrEmpty(Installed(pack));
            var manifest = await _manifestUpdater.ReadManifestAsync(pack, _lifetime.Token);
            await _manifestUpdater.RepairAsync(pack, manifest, dialog.Prompt.RemoveThirdPartyMods.IsChecked == true,
                new Progress<(string status, double value)>(SetProgress), _lifetime.Token);
            if (initialInstall) await MinecraftService.InitializeNewInstanceAsync(_paths.Instance(pack.Slug), _lifetime.Token);
            _configService.SaveInstalled(pack.Slug, manifest.Version, manifest.JavaVersion);
            RegisterCurrentInstances();
            _config.MinecraftVersion = manifest.MinecraftVersion; _config.FabricLoaderVersion = manifest.FabricLoaderVersion; pack.JavaVersion = manifest.JavaVersion;
            _manifestCheck = new PackUpdateCheck(manifest, [], [], []);
            _editionChecks[_config.SelectedEdition] = _manifestCheck; _manifestError = null;
            _configService.Save(_config); success = true;
        }
        catch (Exception ex) { error = ex is InvalidDataException ? ex.Message : GetFriendlyError(ex); _log.Error("Pack repair failed", ex); }
        finally
        {
            if (!_closed)
            {
                SetBusy(false, ""); _operationFailed = !success; Refresh();
                SetNotice(success ? "Modpack reparado" : "No se pudo reparar el modpack", error ?? "", success ? "Success" : "Error", animate: true);
            }
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _checkingManifest) return;
        if (_config.SelectedEdition == PackEdition.DestinyStaff && !await RefreshDiscordAsync()) return;
        if (string.IsNullOrEmpty(Installed(Pack)) && InstallationView.Visibility != Visibility.Visible)
        {
            ShowInstallationPage();
            return;
        }
        if (!Regex.IsMatch(_config.PlayerName ?? "", "^[A-Za-z0-9_]{3,16}$")) { Profile_Click(sender, e); return; }
        if (_previewMode) { SetNotice("Vista de prueba", "Las operaciones del juego están desactivadas en esta vista.", "Gold"); return; }
        ShowHomePage();
        SetBusy(true, "Consultando versión");
        string finalStatus = "Partida terminada";
        string finalDetail = "Minecraft se ha cerrado. Puedes volver a jugar cuando quieras.";
        try
        {
            var progress = new Progress<(string status, double value)>(SetProgress);
            var pack = Pack;
            var installedVersion = Installed(pack);
            PackUpdateCheck? manifestCheck = null;
            using var versionRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            versionRequest.CancelAfter(TimeSpan.FromSeconds(15));
            try { manifestCheck = new PackUpdateCheck(await _manifestUpdater.ReadManifestAsync(pack, versionRequest.Token), [], [], []); }
            catch (HttpRequestException ex) when (!string.IsNullOrWhiteSpace(installedVersion) && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
            {
                _log.Info($"GitHub unavailable; launching installed {pack.Slug} {installedVersion}.");
            }
            catch (TaskCanceledException) when (!string.IsNullOrWhiteSpace(installedVersion) && !_lifetime.IsCancellationRequested)
            {
                _log.Info($"Manifest request timed out; launching installed {pack.Slug} {installedVersion}.");
            }
            if (manifestCheck is not null)
            {
                _manifestCheck = manifestCheck;
                _manifestError = null;
                manifestCheck = await _manifestUpdater.PrepareForPlayAsync(pack, manifestCheck.Manifest, installedVersion, progress, _lifetime.Token);
                if (installedVersion != manifestCheck.Manifest.Version || manifestCheck.HasChanges)
                {
                    await _manifestUpdater.ApplyAsync(pack, manifestCheck, progress, _lifetime.Token);
                    if (string.IsNullOrWhiteSpace(installedVersion))
                        await MinecraftService.InitializeNewInstanceAsync(_paths.Instance(pack.Slug), _lifetime.Token);
                    _configService.SaveInstalled(pack.Slug, manifestCheck.Manifest.Version, manifestCheck.Manifest.JavaVersion);
                    RegisterCurrentInstances();
                    // A completed update must not keep the old 'changes pending' presentation.
                    _manifestCheck = new PackUpdateCheck(manifestCheck.Manifest, [], [], manifestCheck.PreservedOverrides);
                }
                _editionChecks[_config.SelectedEdition] = _manifestCheck;
                _config.MinecraftVersion = manifestCheck.Manifest.MinecraftVersion;
                _config.FabricLoaderVersion = manifestCheck.Manifest.FabricLoaderVersion;
                pack.JavaVersion = manifestCheck.Manifest.JavaVersion;
                _configService.SaveInstalled(pack.Slug, manifestCheck.Manifest.Version, pack.JavaVersion);
                _configService.Save(_config);
            }
            else if (string.IsNullOrWhiteSpace(installedVersion))
                throw new InvalidOperationException("No hay una instalación local ni un manifiesto disponible.");
            if (manifestCheck is null) pack.JavaVersion = _configService.InstalledJavaVersion(pack.Slug);
            using var session = await _minecraft.PrepareAndLaunchAsync(_config, pack, progress, _lifetime.Token);
            // A settings write failure after a successful start must never abandon the game
            // handle or leave the launcher visible for the rest of the session.
            TrySaveConfig();
            var exitCode = await FollowGameProcessAsync(session.Process);
            if (exitCode != 0)
            {
                _operationFailed = true;
                finalStatus = "Minecraft se cerró con un error";
                var errors = await session.ReadErrorsAsync();
                finalDetail = $"Código de salida: {exitCode}. Puedes volver a intentarlo.";
                if (!string.IsNullOrWhiteSpace(errors))
                {
                    var errorFile = Path.Combine(_paths.Logs, $"minecraft-startup-{DateTime.Now:yyyyMMdd-HHmmss}-{session.Process.Id}.log");
                    await File.WriteAllTextAsync(errorFile, errors, _lifetime.Token);
                    _log.Error($"Minecraft stderr saved to {errorFile}");
                    finalDetail += " Se guardó el detalle de Java en los registros del launcher.";
                }
            }
        }
        catch (Exception ex)
        {
            _operationFailed = true;
            finalStatus = "No se pudo completar la operación";
            finalDetail = GetFriendlyError(ex);
            _log.Error("Launcher operation failed", ex);
        }
        finally
        {
            if (!_closed)
            {
                SetBusy(false, "");
                Refresh();
                SetNotice(finalStatus, finalDetail, _operationFailed ? "Error" : "Success", animate: true);
            }
        }
    }

    // The caller owns/disposes the process. Waiting uses the OS exit event, not a timer,
    // a blocked worker thread, or the lifetime of an unrelated Java process.
    internal async Task<int> FollowGameProcessAsync(Process process)
    {
        if (_gameRunning) throw new InvalidOperationException("Ya hay una partida en curso.");
        _ = process.Id; // Reject an unstarted process before hiding any UI.
        _gameRunning = true;
        _backgroundVideo.Update();
        try
        {
            _optionalActivity.Cancel();
            _playerHeadCancellation?.Cancel();
            News.SuspendPlayback(true);
            PrimaryLabel.Text = "INICIANDO…";
            SetNotice("Iniciando Minecraft", "", "Gold");
            Progress.IsIndeterminate = true;
            Progress.Visibility = Visibility.Visible;
            var clock = Stopwatch.StartNew();
            var ready = GameWindowReady is not null ? await GameWindowReady(process, _lifetime.Token)
                : _previewMode || await GameWindowService.WaitForWindowAsync(process, _config.WindowMode, _lifetime.Token);
            _log.Info($"Minecraft window readiness: PID {process.Id}, ready={ready}, elapsed={clock.Elapsed.TotalSeconds:F2}s.");
            Progress.IsIndeterminate = false;
            Progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            Progress.Visibility = Visibility.Collapsed;
            PrimaryLabel.Text = "EN PARTIDA";
            SetNotice("Minecraft en ejecución", "El launcher permanece en segundo plano durante la partida.", "Success");
            if (!process.HasExited && ready) Hide();
            await process.WaitForExitAsync(_lifetime.Token);
            var exitCode = process.ExitCode;
            _log.Info($"Minecraft exited: PID {process.Id}, code {exitCode}.");
            return exitCode;
        }
        finally
        {
            _gameRunning = false;
            _backgroundVideo.Update();
            if (!_closed)
            {
                _optionalActivity.Dispose();
                _optionalActivity = new CancellationTokenSource();
                if (PlayerHead.Source is null) _playerHeadUsername = null;
                // Also restore a launcher that was minimized while Minecraft was preparing.
                WindowState = WindowState.Normal;
                Show();
                Activate();
            }
        }
    }

    internal static string GetFriendlyError(Exception ex) => ex switch
    {
        JavaRuntimeException => ex.Message,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "El modpack no está disponible en el servidor. Vuelve a comprobarlo más tarde.",
        HttpRequestException => "No se pudo conectar con el servidor. Revisa tu conexión y vuelve a intentarlo.",
        OperationCanceledException => "La conexión tardó demasiado. Revisa tu conexión y vuelve a intentarlo.",
        UnauthorizedAccessException => "No hay permiso para escribir en la carpeta. Elige otra ubicación en Ajustes.",
        InvalidDataException => "Los archivos recibidos no son válidos. Vuelve a intentarlo; si persiste, contacta con el equipo de Destiny.",
        Win32Exception => "Windows no pudo iniciar Minecraft. Comprueba que Java y los archivos de la instancia estén disponibles e inténtalo de nuevo.",
        FormatException => "Revisa los argumentos de Java en Ajustes.",
        PackRecoveryException => ex.Message,
        IOException => "No se pudieron guardar los archivos. Comprueba el espacio disponible y que la carpeta no esté en uso.",
        _ => "Revisa la conexión y la carpeta de instalación en Ajustes antes de volver a intentarlo."
    };

    internal static bool AllowsLocalFallback(Exception ex) => ex is OperationCanceledException
        || ex is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500);

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        News.SuspendPlayback(busy);
        _backgroundVideo.Update();
        if (busy) _operationFailed = false;
        PrimaryButton.IsEnabled = !busy;
        InstallPrimaryButton.IsEnabled = !busy;
        DestinyButton.IsEnabled = LiteButton.IsEnabled = StaffButton.IsEnabled = ProfileButton.IsEnabled = SettingsNav.IsEnabled = InstallationNav.IsEnabled = ManageInstallationButton.IsEnabled = !busy;
        ActionArrow.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        RetryCheckButton.Visibility = Visibility.Collapsed;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = true;
        OpenInstallationFolderButton.IsEnabled = !busy && Directory.Exists(_paths.Instance(Pack.Slug));
        GameOptionsButton.IsEnabled = !busy && !_checkingManifest;
        if (busy)
        {
            PrimaryLabel.Text = "PREPARANDO…";
            SetNotice(status, "", "Gold");
        }
        else if (_pendingLauncherUpdate is not null) Dispatcher.BeginInvoke(ShowPendingLauncherUpdate);
    }

    private void SetProgress((string status, double value) update)
    {
        if (_closed || _gameRunning || (!_busy && !_checkingManifest)) return;
        SetNotice(LauncherPresentation.ProgressStatus(update.status), "", "Gold");
        // Backend values combine different phases. Only show determinate progress when a real
        // file count or Minecraft byte ratio is available; never present a fabricated overall %.
        var progress = LauncherPresentation.Progress(update.status, update.value);
        Progress.IsIndeterminate = progress is null;
        if (progress is double value) UiMotion.UpdateProgress(Progress, value);
        if (_busy) PrimaryLabel.Text = LauncherPresentation.ProgressAction(update.status);
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
    private void SetNotice(string title, string detail, string tone, bool animate = false)
    {
        StatusText.Text = title;
        StatusDetail.Text = detail;
        StatusDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        StatusFooter.Visibility = SettingsPageHost.Visibility != Visibility.Visible && (_busy || _checkingManifest || tone is "Error" or "Warning")
            ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Fill = Brush(tone);
        if (animate) UiMotion.Enter(StatusText);
    }

    private bool TrySaveConfig()
    {
        try { _configService.Save(_config); return true; }
        catch (Exception ex) { _log.Error("Could not save settings", ex); SetNotice("No se pudieron guardar los cambios", GetFriendlyError(ex), "Error"); return false; }
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_config.MultiAccountEnabled)
        {
            RefreshAccountMenu();
            ProfileButton.ContextMenu.IsOpen = true;
            return;
        }
        var dialog = new ProfileDialog(_config.PlayerName) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var previousName = _config.PlayerName;
        var previousAccounts = _config.Accounts.ToList();
        _config.Accounts.RemoveAll(n => n.Equals(previousName, StringComparison.OrdinalIgnoreCase));
        _config.PlayerName = dialog.PlayerName;
        _config.NormalizeAccounts();
        if (!_previewMode && !TrySaveConfig()) { _config.PlayerName = previousName; _config.Accounts = previousAccounts; return; }
        Refresh();
    }

    private void RefreshAccountMenu()
    {
        ProfileActionGlyph.Data = _config.MultiAccountEnabled ? Geometry.Parse("M3 6l5 5 5-5") : IconGlyphs.Edit;
        ProfileButton.ToolTip = _config.MultiAccountEnabled ? "Cambiar cuenta" : "Editar nombre de jugador";
        System.Windows.Automation.AutomationProperties.SetName(ProfileButton, (string)ProfileButton.ToolTip);
        if (!_config.MultiAccountEnabled) { ProfileButton.ContextMenu = null; return; }
        var menu = new ContextMenu { Style = (Style)FindResource("AccountMenu"), PlacementTarget = ProfileButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var name in _config.Accounts)
        {
            var identity = new AccountIdentity { DataContext = name };
            if (!_previewMode || _loadRemoteSkinInPreview) identity.SetValue(AccountIdentity.HeadServiceProperty, _playerHeadService);
            var item = new MenuItem { Header = identity, Style = (Style)FindResource("AccountMenuItem"),
                IsCheckable = true, IsChecked = name.Equals(_config.PlayerName, StringComparison.OrdinalIgnoreCase) };
            System.Windows.Automation.AutomationProperties.SetName(item, name);
            item.Click += (_, _) => SetActiveAccount(name);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator { Background = Brush("Line"), Margin = new Thickness(8, 4, 8, 4) });
        var manage = new MenuItem { Header = "Gestionar cuentas", Style = (Style)FindResource("AccountMenuItem") };
        manage.Click += (_, _) =>
        {
            if (_busy) return;
            var dialog = new AccountsDialog(_config.Accounts, _config.PlayerName, SaveAccounts,
                !_previewMode || _loadRemoteSkinInPreview ? _playerHeadService : null) { Owner = this };
            dialog.ShowDialog();
        };
        menu.Items.Add(manage);
        ProfileButton.ContextMenu = menu;
    }

    internal bool SetActiveAccount(string name)
    {
        if (_busy || !_config.MultiAccountEnabled || !_config.Accounts.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        var previous = _config.PlayerName;
        _config.PlayerName = _config.Accounts.First(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!_previewMode && !TrySaveConfig()) { _config.PlayerName = previous; return false; }
        Refresh(); return true;
    }

    internal bool SaveAccounts(IEnumerable<string> names)
    {
        if (_busy || !_config.MultiAccountEnabled) return false;
        var previousNames = _config.Accounts; var previousActive = _config.PlayerName;
        _config.Accounts = names.Where(LauncherConfig.ValidPlayerName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!_config.Accounts.Contains(_config.PlayerName, StringComparer.OrdinalIgnoreCase)) _config.PlayerName = _config.Accounts.FirstOrDefault() ?? "";
        if (!_previewMode && !TrySaveConfig()) { _config.Accounts = previousNames; _config.PlayerName = previousActive; return false; }
        Refresh(); return true;
    }

    private void RefreshPlayerHead()
    {
        if (_previewMode && !_loadRemoteSkinInPreview) return;
        var username = _config.PlayerName?.Trim();
        if (string.Equals(username, _playerHeadUsername, StringComparison.Ordinal)) return;
        _playerHeadUsername = username;
        _playerHeadCancellation?.Cancel();
        _playerHeadCancellation?.Dispose();
        _playerHeadCancellation = null;
        PlayerHead.Source = null;
        PlayerHead.Visibility = Visibility.Collapsed;
        PlayerInitial.Visibility = Visibility.Visible;
        if (!Regex.IsMatch(username ?? "", "^[A-Za-z0-9_]{3,16}$")) return;
        _playerHeadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _optionalActivity.Token);
        _playerHeadLoadTask = LoadPlayerHeadAsync(username!, _playerHeadCancellation.Token);
    }

    private async Task LoadPlayerHeadAsync(string username, CancellationToken token)
    {
        try
        {
            var cached = await _playerHeadService.GetCachedHeadAsync(username, token);
            if (cached is not null && !token.IsCancellationRequested && !_closed && string.Equals(username, _config.PlayerName, StringComparison.Ordinal))
            { PlayerHead.Source = cached; PlayerHead.Visibility = Visibility.Visible; PlayerInitial.Visibility = Visibility.Collapsed; }
            var image = await _playerHeadService.GetHeadAsync(username, token);
            if (image is null || token.IsCancellationRequested || _closed || !string.Equals(username, _config.PlayerName, StringComparison.Ordinal)) return;
            PlayerHead.Source = image;
            PlayerHead.Visibility = Visibility.Visible;
            PlayerInitial.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Info($"Could not load player head: {ex.Message}"); }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowHomePage();
    private void Installation_Click(object sender, RoutedEventArgs e) => ShowInstallationPage();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();
    private void OpenInstallationFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var folder = _paths.Instance(Pack.Slug);
        if (!Directory.Exists(folder)) { Refresh(); return; }
        try { OpenDirectory(folder); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { SetNotice("No se pudo abrir la carpeta", "Comprueba que la carpeta exista y tengas acceso a ella.", "Error"); }
    }
    private void SocialLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string address } || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        try { OpenExternalLink(uri); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            LauncherDialog.Inform(this, "No se pudo abrir el enlace", "Windows no pudo abrir el navegador predeterminado. Vuelve a intentarlo más tarde.");
        }
    }
    private void ShowHomePage()
    {
        if (SettingsPageHost.Content is SettingsPage { HasChanges: true } && !DiscardSettings()) return;
        HomeView.Visibility = Visibility.Visible;
        InstallationView.Visibility = Visibility.Collapsed;
        SettingsPageHost.Visibility = Visibility.Collapsed;
        SettingsPageHost.Content = null;
        StatusFooter.Visibility = Visibility.Visible;
        SetNavigationState("home");
        Refresh();
        UiMotion.Enter(HomeView);
    }
    private void ShowInstallationPage()
    {
        if (_busy || InstallationView.Visibility == Visibility.Visible) return;
        if (SettingsPageHost.Content is SettingsPage { HasChanges: true } && !DiscardSettings()) return;
        HomeView.Visibility = Visibility.Collapsed;
        SettingsPageHost.Visibility = Visibility.Collapsed;
        SettingsPageHost.Content = null;
        InstallationView.Visibility = Visibility.Visible;
        StatusFooter.Visibility = Visibility.Visible;
        SetNavigationState("installation");
        Refresh();
        UiMotion.Enter(InstallationView);
    }
    private void ShowSettingsPage()
    {
        if (_busy || SettingsPageHost.Visibility == Visibility.Visible) return;
        var page = new SettingsPage(_config, ManageDiscordAsync, () => DiscordStatus, () => _discord.IsLinked) { SaveRequested = SaveSettingsAsync };
        page.BackRequested += ShowHomePage;
        HomeView.Visibility = Visibility.Collapsed;
        InstallationView.Visibility = Visibility.Collapsed;
        SettingsPageHost.Content = page;
        SettingsPageHost.Visibility = Visibility.Visible;
        StatusFooter.Visibility = Visibility.Collapsed;
        SetNavigationState("settings");
        UiMotion.Enter(page);
    }
    private void SetNavigationState(string page)
    {
        foreach (var (button, name) in new[] { (HomeNav, "home"), (InstallationNav, "installation"), (SettingsNav, "settings") })
        {
            button.Tag = page == name ? "Selected" : "";
            AutomationProperties.SetItemStatus(button, page == name ? "Página actual" : "");
        }
    }
    private bool DiscardSettings() => LauncherDialog.Confirm(this, "Cambios sin guardar", "Tienes cambios en los ajustes. Puedes seguir editándolos o descartarlos.", "Descartar cambios", "Seguir editando");
    private async Task SaveSettingsAsync(LauncherConfig config)
    {
        RegisterCurrentInstances();
        // Validate and persist before updating the active configuration.
        var installPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(config.InstallDirectory.Trim()));
        if (!_previewMode) _configService.Save(config);
        _config = config;
        _paths.SetInstallDirectory(installPath);
        _manifestCheck = null;
        _manifestError = null;
        if (SettingsPageHost.Content is SettingsPage page) page.MarkSaved();
        ShowHomePage();
        Refresh();
        await RefreshManifestAsync();
        if (_manifestError is null) SetNotice("Ajustes guardados", "Se aplicarán a la próxima partida.", "Success", animate: true);
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowWorkArea.Attach(this);
    }
    private void UpdateWindowFrame()
    {
        if (WindowFrame is null) return;
        if (WindowState == WindowState.Maximized) { WindowState = WindowState.Normal; return; }
        UpdateFrameClip();
    }
    private void UpdateFrameClip()
    {
        if (WindowFrame.ActualWidth <= 0 || WindowFrame.ActualHeight <= 0) return;
        WindowFrame.Clip = new RectangleGeometry(new Rect(0, 0, WindowFrame.ActualWidth, WindowFrame.ActualHeight), 18, 18);
        OutlineTop.Fill = OutlineBottom.Fill = CreateOutlineFade(WindowFrame.ActualWidth, horizontal: true);
        OutlineLeft.Fill = OutlineRight.Fill = CreateOutlineFade(WindowFrame.ActualHeight, horizontal: false);
    }
    private static Brush CreateOutlineFade(double length, bool horizontal)
    {
        // Keep the curved corner clear, then ease the contour into its full tone over 24 DIPs.
        var clear = Color.FromArgb(0, 0x51, 0x43, 0x2C);
        var soft = Color.FromArgb(0x60, 0x51, 0x43, 0x2C);
        var ink = Color.FromRgb(0x51, 0x43, 0x2C);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1),
            GradientStops = [new(clear, 0), new(clear, 18 / length), new(soft, 30 / length),
                new(ink, 42 / length), new(ink, 1 - 42 / length), new(soft, 1 - 30 / length),
                new(clear, 1 - 18 / length), new(clear, 1)]
        };
        brush.Freeze();
        return brush;
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_updateInstalling) { base.OnClosing(e); return; }
        if (_busy)
        {
            e.Cancel = true;
            LauncherDialog.Inform(this, "Operación en curso", "Espera a que termine la instalación o el inicio del juego antes de cerrar el launcher.");
        }
        else if (SettingsPageHost.Content is SettingsPage { HasChanges: true } && !DiscardSettings()) e.Cancel = true;
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _backgroundVideo.Dispose();
        _lifetime.Cancel();
        _optionalActivity.Cancel();
        _optionalActivity.Dispose();
        _manifestCancellation?.Cancel();
        _manifestCancellation?.Dispose();
        _playerHeadCancellation?.Cancel();
        _playerHeadCancellation?.Dispose();
        _playerHeadService.Dispose();
        _newsFeed.Dispose();
        _launcherUpdates?.Dispose();
        base.OnClosed(e);
    }

    // Used by the isolated WPF smoke harness, never selected during normal startup.
    internal void SetAuditState(string installed, PackUpdateCheck? check = null, bool checking = false, string? error = null, string playerName = "Player_Destiny16", PackEdition edition = PackEdition.Destiny)
    {
        if (!_previewMode) throw new InvalidOperationException("Audit states require an isolated preview.");
        _config.PlayerName = playerName;
        _config.SelectedEdition = edition;
        if (!string.IsNullOrEmpty(installed)) _configService.SaveInstalled(Pack.Slug, installed);
        else { var marker = Path.Combine(_paths.Instance(Pack.Slug), "destiny-installed.json"); if (File.Exists(marker)) File.Delete(marker); }
        _manifestCheck = check;
        _manifestError = error;
        _localFallbackAllowed = true;
        _operationFailed = false;
        _checkingManifest = checking;
        Progress.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = true;
        if (check is not null) _editionChecks[edition] = check;
        Refresh();
    }
    internal void SetAuditProgress(string status, double value) { SetBusy(true, status); SetProgress((status, value)); }
    internal void SetAuditAccounts(bool enabled, IEnumerable<string> names, string active)
    {
        if (!_previewMode) throw new InvalidOperationException("Audit states require an isolated preview.");
        _config.MultiAccountEnabled = enabled; _config.Accounts = names.ToList(); _config.PlayerName = active;
        _config.NormalizeAccounts(); Refresh();
    }
    internal void EndAuditProgress() => SetBusy(false, "");
    internal void SetAuditFailure()
    {
        _operationFailed = true;
        Refresh();
        SetNotice("No se pudo completar la operación", GetFriendlyError(new HttpRequestException()), "Error");
    }
}
