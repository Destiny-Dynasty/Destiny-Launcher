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
    private readonly bool _previewMode;
    private readonly bool _loadRemoteSkinInPreview;
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

    public MainWindow(bool previewMode = false, bool loadRemoteSkinInPreview = false, bool startOnSettingsPage = false, string? dataRoot = null)
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
        var exists = File.Exists(_paths.ConfigFile);
        _config = previewMode ? new LauncherConfig { PlayerName = loadRemoteSkinInPreview ? "Shady935" : "", InstallDirectory = _paths.InstallDirectory } : _configService.Load();
        if (dataRoot is not null && !exists) _config.InstallDirectory = _paths.InstallDirectory;
        _paths.SetInstallDirectory(_config.InstallDirectory);
        InitializeComponent();
        ConfigureInitialWindowSize();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        WindowFrame.SizeChanged += (_, _) => UpdateFrameClip();
        StateChanged += (_, _) => UpdateWindowFrame();
        Hero.SizeChanged += (_, _) => ApplyResponsiveLayout();
        News.SetSlides(NewsFeedService.BundledSamples());
        Loaded += async (_, _) =>
        {
            ApplyResponsiveLayout();
            // Showing the window again after a game must not repeat startup requests.
            if (_initialLoadStarted) return;
            _initialLoadStarted = true;
            if (!_previewMode) _ = LoadNewsAsync();
            await RefreshManifestAsync();
        };
        TestModeText.Visibility = dataRoot is not null ? Visibility.Visible : Visibility.Collapsed;
        LauncherVersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        UpdateWindowFrame();
        Refresh();
        if (startOnSettingsPage) ShowSettingsPage();
    }

    private PackDefinition Pack => _config.SelectedEdition == PackEdition.Destiny ? _config.Destiny : _config.DestinyLite;
    internal Task PlayerHeadLoadTask => _playerHeadLoadTask ?? Task.CompletedTask;
    internal LauncherPaths DataPaths => _paths;
    internal Action<Uri> OpenExternalLink { get; set; } = uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    internal Action<string> OpenDirectory { get; set; } = path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

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
        var compactHero = Hero.ActualHeight < 340;
        HeroMark.Width = compactHero ? 190 : narrow ? 230 : 250;
        HeroMark.Height = compactHero ? 106 : 148;
        HeroMark.Margin = new Thickness(narrow ? 20 : 28, compactHero ? 8 : 22, 0, 0);
        News.Width = narrow || compactHero ? 300 : 350;
        News.Height = News.Width / 2.24;
        News.Margin = new Thickness(narrow ? 20 : 28, 0, 0, 22);
        LaunchPanel.Margin = new Thickness(narrow ? 20 : 28, 0, narrow ? 20 : 28, height < 560 ? 12 : 18);
        ProfilePanel.Width = narrow ? 214 : 234;
        ProfilePanel.Margin = new Thickness(0, 0, narrow ? 20 : 28, 22);
        InstallationView.Margin = new Thickness(narrow ? 20 : 28, 24, narrow ? 20 : 28, 20);
        LaunchLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : narrow ? 202 : 228);
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
        RefreshPlayerHead();
        EditionText.Text = Pack.Name;
        VersionText.Text = $"Minecraft {_config.MinecraftVersion} · Fabric";
        DestinyButton.IsChecked = _config.SelectedEdition == PackEdition.Destiny;
        LiteButton.IsChecked = _config.SelectedEdition == PackEdition.DestinyLite;
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
            return check.HasChanges || installed != check.Manifest.Version ? "Actualización disponible" : $"Instalada · {installed}";
        return $"Instalada · {installed}";
    }

    private async void Edition_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var edition = Enum.Parse<PackEdition>(((FrameworkElement)sender).Tag!.ToString()!);
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
    private async Task RefreshManifestAsync()
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
            var progress = new Progress<(string status, double value)>(p => { if (generation == _manifestRefreshGeneration && !_closed && !_busy && _checkingManifest) SetProgress(p); });
            var check = await Task.Run(() => _manifestUpdater.CheckAsync(pack, progress, token), token);
            if (generation != _manifestRefreshGeneration || _closed) return;
            _manifestCheck = check;
            _editionChecks[edition] = check;
            _config.MinecraftVersion = check.Manifest.MinecraftVersion;
            _config.FabricLoaderVersion = check.Manifest.FabricLoaderVersion;
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
            }
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _checkingManifest) return;
        if (string.IsNullOrEmpty(Installed(Pack)) && InstallationView.Visibility != Visibility.Visible)
        {
            ShowInstallationPage();
            return;
        }
        if (!Regex.IsMatch(_config.PlayerName ?? "", "^[A-Za-z0-9_]{3,16}$")) { Profile_Click(sender, e); return; }
        if (_previewMode) { SetNotice("Vista de prueba", "Las operaciones del juego están desactivadas en esta vista.", "Gold"); return; }
        ShowHomePage();
        SetBusy(true, "Comprobando archivos");
        string finalStatus = "Partida terminada";
        string finalDetail = "Minecraft se ha cerrado. Puedes volver a jugar cuando quieras.";
        try
        {
            var progress = new Progress<(string status, double value)>(SetProgress);
            var pack = Pack;
            var installedVersion = Installed(pack);
            var manifestCheck = _manifestCheck;
            if (manifestCheck is null)
            {
                try { manifestCheck = await Task.Run(() => _manifestUpdater.CheckAsync(pack, progress, _lifetime.Token)); }
                catch (HttpRequestException ex) when (!string.IsNullOrWhiteSpace(installedVersion) && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
                {
                    _log.Info($"GitHub unavailable; launching installed {pack.Slug} {installedVersion}.");
                }
                catch (TaskCanceledException) when (!string.IsNullOrWhiteSpace(installedVersion))
                {
                    _log.Info($"Manifest request timed out; launching installed {pack.Slug} {installedVersion}.");
                }
            }
            if (manifestCheck is not null)
            {
                _manifestCheck = manifestCheck;
                _manifestError = null;
                if (manifestCheck.HasChanges || installedVersion != manifestCheck.Manifest.Version)
                {
                    await _manifestUpdater.ApplyAsync(pack, manifestCheck, progress, _lifetime.Token);
                    _configService.SaveInstalled(pack.Slug, manifestCheck.Manifest.Version);
                    // A completed update must not keep the old 'changes pending' presentation.
                    _manifestCheck = new PackUpdateCheck(manifestCheck.Manifest, [], [], manifestCheck.PreservedOverrides);
                }
                _editionChecks[_config.SelectedEdition] = _manifestCheck;
                _config.MinecraftVersion = manifestCheck.Manifest.MinecraftVersion;
                _config.FabricLoaderVersion = manifestCheck.Manifest.FabricLoaderVersion;
                _configService.Save(_config);
            }
            else if (string.IsNullOrWhiteSpace(installedVersion))
                throw new InvalidOperationException("No hay una instalación local ni un manifiesto disponible.");
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
        try
        {
            _optionalActivity.Cancel();
            _playerHeadCancellation?.Cancel();
            News.SuspendPlayback(true);
            Progress.IsIndeterminate = false;
            Progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            Progress.Visibility = Visibility.Collapsed;
            PrimaryLabel.Text = "EN PARTIDA";
            SetNotice("Minecraft en ejecución", "El launcher permanece en segundo plano durante la partida.", "Success");
            if (!process.HasExited) Hide();
            await process.WaitForExitAsync(_lifetime.Token);
            var exitCode = process.ExitCode;
            _log.Info($"Minecraft exited: PID {process.Id}, code {exitCode}.");
            return exitCode;
        }
        finally
        {
            _gameRunning = false;
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
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "El modpack no está disponible en el servidor. Vuelve a comprobarlo más tarde.",
        HttpRequestException => "No se pudo conectar con el servidor. Revisa tu conexión y vuelve a intentarlo.",
        OperationCanceledException => "La conexión tardó demasiado. Revisa tu conexión y vuelve a intentarlo.",
        UnauthorizedAccessException => "No hay permiso para escribir en la carpeta. Elige otra ubicación en Ajustes.",
        InvalidDataException => "Los archivos recibidos no son válidos. Vuelve a intentarlo; si persiste, contacta con el equipo de Destiny.",
        Win32Exception => "Windows no pudo iniciar Minecraft. Comprueba que Java y los archivos de la instancia estén disponibles e inténtalo de nuevo.",
        IOException => "No se pudieron guardar los archivos. Comprueba el espacio disponible y que la carpeta no esté en uso.",
        _ => "Revisa la conexión y la carpeta de instalación en Ajustes antes de volver a intentarlo."
    };

    internal static bool AllowsLocalFallback(Exception ex) => ex is OperationCanceledException
        || ex is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500);

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        News.SuspendPlayback(busy);
        if (busy) _operationFailed = false;
        PrimaryButton.IsEnabled = !busy;
        InstallPrimaryButton.IsEnabled = !busy;
        DestinyButton.IsEnabled = LiteButton.IsEnabled = ProfileButton.IsEnabled = SettingsNav.IsEnabled = InstallationNav.IsEnabled = ManageInstallationButton.IsEnabled = !busy;
        ActionArrow.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        RetryCheckButton.Visibility = Visibility.Collapsed;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = true;
        OpenInstallationFolderButton.IsEnabled = !busy && Directory.Exists(_paths.Instance(Pack.Slug));
        if (busy)
        {
            PrimaryLabel.Text = "PREPARANDO…";
            SetNotice(status, "", "Gold");
        }
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
        var dialog = new ProfileDialog(_config.PlayerName) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var previousName = _config.PlayerName;
        _config.PlayerName = dialog.PlayerName;
        if (!_previewMode && !TrySaveConfig()) { _config.PlayerName = previousName; return; }
        Refresh();
        SetNotice("Perfil guardado", $"Jugarás como {_config.PlayerName}.", "Success", animate: true);
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
        var page = new SettingsPage(_config) { SaveRequested = SaveSettingsAsync };
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
        _lifetime.Cancel();
        _optionalActivity.Cancel();
        _optionalActivity.Dispose();
        _manifestCancellation?.Cancel();
        _manifestCancellation?.Dispose();
        _playerHeadCancellation?.Cancel();
        _playerHeadCancellation?.Dispose();
        _playerHeadService.Dispose();
        _newsFeed.Dispose();
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
    internal void EndAuditProgress() => SetBusy(false, "");
    internal void SetAuditFailure()
    {
        _operationFailed = true;
        Refresh();
        SetNotice("No se pudo completar la operación", GetFriendlyError(new HttpRequestException()), "Error");
    }
}
