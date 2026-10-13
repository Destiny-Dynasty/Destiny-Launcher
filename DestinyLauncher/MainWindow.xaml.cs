using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherPaths _paths = new();
    private readonly ConfigService _configService;
    private readonly AppLogger _log;
    private readonly GitHubPackUpdater _manifestUpdater;
    private readonly MinecraftService _minecraft;
    private readonly PlayerHeadService _playerHeadService;
    private readonly bool _previewMode;
    private readonly bool _loadRemoteSkinInPreview;
    private LauncherConfig _config;
    private PackUpdateCheck? _manifestCheck;
    private string? _manifestError;
    private bool _busy;
    private int _manifestRefreshGeneration;
    private bool _statusPulseStarted;
    private string? _playerHeadUsername;
    private CancellationTokenSource? _playerHeadCancellation;
    private Task? _playerHeadLoadTask;

    public MainWindow(bool previewMode = false, bool loadRemoteSkinInPreview = false, bool startOnSettingsPage = false)
    {
        InitializeComponent();
        _previewMode = previewMode;
        _loadRemoteSkinInPreview = loadRemoteSkinInPreview;
        _log = new AppLogger(_paths);
        _configService = new ConfigService(_paths);
        _manifestUpdater = new GitHubPackUpdater(_paths, _log);
        _minecraft = new MinecraftService(_paths, _log);
        _playerHeadService = new PlayerHeadService(_paths);
        _config = previewMode ? CreatePreviewConfig() : _configService.Load();
        _paths.SetInstallDirectory(_config.InstallDirectory);

        WindowFrame.SizeChanged += (_, _) => UpdateFrameClip();
        StateChanged += (_, _) => UpdateWindowFrame();
        Loaded += async (_, _) => await RefreshManifestAsync();
        UpdateWindowFrame();
        Refresh();
        if (startOnSettingsPage) ShowSettingsPage();
    }

    private PackDefinition Pack => _config.SelectedEdition == PackEdition.Destiny ? _config.Destiny : _config.DestinyLite;
    internal Task PlayerHeadLoadTask => _playerHeadLoadTask ?? Task.CompletedTask;

    private static LauncherConfig CreatePreviewConfig() => new()
    {
        PlayerName = "Shady935",
        SelectedEdition = PackEdition.Destiny,
        RamGb = 8,
        Destiny = new PackDefinition { Name = "Destiny", Slug = "destiny", Version = "1.4.7" },
        DestinyLite = new PackDefinition { Name = "Destiny Lite", Slug = "destiny-lite", Version = "0.1.0" }
    };

    private void Refresh()
    {
        PlayerNameText.Text = string.IsNullOrWhiteSpace(_config.PlayerName) ? "Añadir nombre" : _config.PlayerName;
        PlayerInitial.Text = string.IsNullOrWhiteSpace(_config.PlayerName) ? "D" : _config.PlayerName[..1].ToUpperInvariant();
        RefreshPlayerHead();
        EditionText.Text = Pack.Name;
        VersionText.Text = $"Minecraft {_config.MinecraftVersion}  ·  Fabric";

        var installedVersion = _previewMode ? "1.4.6" : _configService.InstalledVersion(Pack.Slug);
        var hasInstall = !string.IsNullOrEmpty(installedVersion);
        var updateAvailable = hasInstall && NeedsUpdate(installedVersion);
        PrimaryLabel.Text = !hasInstall ? "INSTALAR" : updateAvailable ? "ACTUALIZAR" : "JUGAR";
        StatusText.Text = _manifestError is not null ? "No disponible" : "Listo";
        PackInstalledVersionText.Text = hasInstall ? installedVersion : "—";
        PackLatestVersionText.Text = _manifestError is null ? TargetVersion : "—";
        PackStateIndicator.ToolTip = _manifestError is not null ? "No disponible" : updateAvailable ? "Actualización disponible" : hasInstall ? "Actualizado" : "Sin instalar";
        PackStateDot.Fill = new SolidColorBrush(_manifestError is not null
            ? Color.FromRgb(177, 126, 76)
            : updateAvailable ? Color.FromRgb(222, 184, 98) : Color.FromRgb(185, 156, 83));
        if (!_statusPulseStarted)
        {
            var pulse = new DoubleAnimation(.58, 1, TimeSpan.FromSeconds(1.45))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            PackStateDot.BeginAnimation(UIElement.OpacityProperty, pulse);
            _statusPulseStarted = true;
        }
        ApplyEditionStyle();
    }

    private string TargetVersion => _manifestCheck?.Manifest.Version ?? Pack.Version;

    private bool NeedsUpdate(string installedVersion) =>
        installedVersion != TargetVersion || _manifestCheck?.HasChanges == true;

    private void ApplyEditionStyle()
    {
        var selected = new SolidColorBrush(Color.FromArgb(0x38, 196, 154, 75));
        var selectedText = new SolidColorBrush(Color.FromRgb(248, 224, 167));
        var idle = new SolidColorBrush(Color.FromArgb(0x20, 27, 25, 20));
        var idleText = new SolidColorBrush(Color.FromRgb(170, 161, 143));
        DestinyButton.Background = _config.SelectedEdition == PackEdition.Destiny ? selected : idle;
        DestinyButton.Foreground = _config.SelectedEdition == PackEdition.Destiny ? selectedText : idleText;
        LiteButton.Background = _config.SelectedEdition == PackEdition.DestinyLite ? selected : idle;
        LiteButton.Foreground = _config.SelectedEdition == PackEdition.DestinyLite ? selectedText : idleText;
    }

    private async void Edition_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _config.SelectedEdition = (PackEdition)Enum.Parse(typeof(PackEdition), ((FrameworkElement)sender).Tag!.ToString()!);
        _manifestCheck = null;
        _manifestError = null;
        if (!_previewMode) _configService.Save(_config);
        Refresh();
        await RefreshManifestAsync();
    }

    private async Task RefreshManifestAsync()
    {
        var generation = Interlocked.Increment(ref _manifestRefreshGeneration);
        var pack = Pack;
        if (_previewMode) return;
        try
        {
            SetStatus("Comprobando…");
            var progress = new Progress<(string status, double value)>(SetProgress);
            var check = await _manifestUpdater.CheckAsync(pack, progress, CancellationToken.None);
            if (generation != Volatile.Read(ref _manifestRefreshGeneration)) return;
            _manifestCheck = check;
            _manifestError = null;
            _config.MinecraftVersion = _manifestCheck.Manifest.MinecraftVersion;
            _config.FabricLoaderVersion = _manifestCheck.Manifest.FabricLoaderVersion;
            _configService.Save(_config);
            _log.Info($"GitHub manifest ready for {pack.Slug}: {_manifestCheck.Manifest.Version}");
        }
        catch (Exception ex)
        {
            if (generation != Volatile.Read(ref _manifestRefreshGeneration)) return;
            _manifestCheck = null;
            _manifestError = ex.Message;
            _log.Error("Could not check GitHub pack manifest", ex);
        }
        Refresh();
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_config.PlayerName))
        {
            Profile_Click(sender, e);
            return;
        }

        SetBusy(true, "Iniciando…");
        var finalStatus = "Minecraft iniciado";
        try
        {
            var progress = new Progress<(string status, double value)>(SetProgress);
            var pack = Pack;
            {
                var installedVersion = _configService.InstalledVersion(pack.Slug);
                var manifestCheck = _manifestCheck;
                if (manifestCheck is null)
                {
                    try
                    {
                        manifestCheck = await _manifestUpdater.CheckAsync(pack, progress, CancellationToken.None);
                    }
                    catch (HttpRequestException ex) when (!string.IsNullOrWhiteSpace(installedVersion) && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
                    {
                        _log.Info($"GitHub unavailable; launching installed {pack.Slug} {installedVersion}.");
                        finalStatus = "Sin conexión · usando instalación local";
                    }
                    catch (TaskCanceledException ex) when (!string.IsNullOrWhiteSpace(installedVersion))
                    {
                        _log.Info($"GitHub check timed out ({ex.Message}); launching installed {pack.Slug} {installedVersion}.");
                        finalStatus = "Tiempo agotado · usando instalación local";
                    }
                }

                if (manifestCheck is not null)
                {
                    _manifestCheck = manifestCheck;
                    if (manifestCheck.HasChanges || installedVersion != manifestCheck.Manifest.Version)
                    {
                        await _manifestUpdater.ApplyAsync(pack, manifestCheck, progress, CancellationToken.None);
                        _configService.SaveInstalled(pack.Slug, manifestCheck.Manifest.Version);
                    }
                    _config.MinecraftVersion = manifestCheck.Manifest.MinecraftVersion;
                    _config.FabricLoaderVersion = manifestCheck.Manifest.FabricLoaderVersion;
                    _configService.Save(_config);
                }
                else if (string.IsNullOrWhiteSpace(installedVersion))
                {
                    throw new InvalidOperationException("No hay una instalación local y no se pudo consultar el manifiesto de GitHub.");
                }
            }

            await _minecraft.PrepareAndLaunchAsync(_config, pack, progress, CancellationToken.None);
            _configService.Save(_config);
        }
        catch (Exception ex)
        {
            finalStatus = GetFriendlyError(ex);
            _log.Error("Launcher operation failed", ex);
            MessageBox.Show(finalStatus, "Destiny Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, string.Empty);
            Refresh();
            StatusText.Text = finalStatus;
        }
    }

    private static string GetFriendlyError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "Modpack no disponible.",
        HttpRequestException => "Sin conexión.",
        UnauthorizedAccessException => "Windows bloqueó la carpeta de instalación.",
        InvalidDataException => "El paquete no es válido.",
        IOException => "No se pudieron guardar los archivos.",
        _ => "No se pudo completar la operación."
    };

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        PrimaryButton.IsEnabled = !busy;
        DestinyButton.IsEnabled = !busy;
        LiteButton.IsEnabled = !busy;
        ProfileButton.IsEnabled = !busy;
        ActionArrow.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (busy) PrimaryLabel.Text = "PREPARANDO...";
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) Progress.Value = 0;
        if (!string.IsNullOrEmpty(status)) StatusText.Text = status;
    }

    private void SetProgress((string status, double value) update)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = update.status;
            Progress.Value = Math.Clamp(update.value, 0, 1);
            if (_busy)
            {
                PrimaryLabel.Text = update.status.StartsWith("Descargando", StringComparison.OrdinalIgnoreCase) ? "DESCARGANDO..." :
                    update.status.StartsWith("Aplicando", StringComparison.OrdinalIgnoreCase) || update.status.StartsWith("Extrayendo", StringComparison.OrdinalIgnoreCase) ? "INSTALANDO..." :
                    "PREPARANDO...";
            }
        });
    }

    private void SetStatus(string status) => Dispatcher.Invoke(() => StatusText.Text = status);

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new ProfileDialog(_config.PlayerName) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _config.PlayerName = dialog.PlayerName;
            if (!_previewMode) _configService.Save(_config);
            Refresh();
        }
    }

    private void RefreshPlayerHead()
    {
        if ((_previewMode && !_loadRemoteSkinInPreview) || PlayerHead is null) return;
        var username = _config.PlayerName?.Trim();
        if (string.Equals(username, _playerHeadUsername, StringComparison.Ordinal)) return;

        _playerHeadUsername = username;
        _playerHeadCancellation?.Cancel();
        _playerHeadCancellation?.Dispose();
        _playerHeadCancellation = null;
        PlayerHead.Source = null;
        PlayerHead.Visibility = Visibility.Collapsed;
        PlayerInitial.Visibility = Visibility.Visible;

        if (string.IsNullOrWhiteSpace(username) || !Regex.IsMatch(username, "^[A-Za-z0-9_]{1,16}$")) return;
        var cancellation = new CancellationTokenSource();
        _playerHeadCancellation = cancellation;
        _playerHeadLoadTask = LoadPlayerHeadAsync(username, cancellation.Token);
    }

    private async Task LoadPlayerHeadAsync(string username, CancellationToken cancellationToken)
    {
        try
        {
            var image = await _playerHeadService.GetHeadAsync(username, cancellationToken);
            if (image is null || cancellationToken.IsCancellationRequested || !string.Equals(username, _config.PlayerName, StringComparison.Ordinal)) return;
            PlayerHead.Source = image;
            PlayerHead.Visibility = Visibility.Visible;
            PlayerInitial.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Info($"Could not load MCHeads skin head for {username}: {ex.Message}");
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowHomePage();

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

    private void ShowHomePage()
    {
        HomeView.Visibility = Visibility.Visible;
        SettingsPageHost.Visibility = Visibility.Collapsed;
        SettingsPageHost.Content = null;
        SetNavigationState(settingsSelected: false);
    }

    private void ShowSettingsPage()
    {
        if (_busy) return;

        var page = new SettingsPage(_config)
        {
            SaveRequested = SaveSettingsAsync
        };
        page.BackRequested += ShowHomePage;
        HomeView.Visibility = Visibility.Collapsed;
        SettingsPageHost.Content = page;
        SettingsPageHost.Visibility = Visibility.Visible;
        SetNavigationState(settingsSelected: true);
    }

    private void SetNavigationState(bool settingsSelected)
    {
        var activeBackground = new SolidColorBrush(Color.FromRgb(33, 28, 17));
        var activeText = new SolidColorBrush(Color.FromRgb(241, 209, 138));
        var inactiveText = new SolidColorBrush(Color.FromRgb(156, 149, 136));
        var activeIcon = new SolidColorBrush(Color.FromRgb(227, 191, 106));
        var inactiveIcon = new SolidColorBrush(Color.FromRgb(168, 155, 126));

        HomeNav.Tag = settingsSelected ? "Unselected" : "Selected";
        HomeNav.Background = settingsSelected ? Brushes.Transparent : activeBackground;
        HomeNav.Foreground = settingsSelected ? inactiveText : activeText;
        HomeNavIcon.Stroke = settingsSelected ? inactiveIcon : activeIcon;
        SettingsNav.Tag = settingsSelected ? "Selected" : "Unselected";
        SettingsNav.Background = settingsSelected ? activeBackground : Brushes.Transparent;
        SettingsNav.Foreground = settingsSelected ? activeText : inactiveText;
        SettingsNavIcon.Stroke = settingsSelected ? activeIcon : inactiveIcon;
    }

    private async Task SaveSettingsAsync(LauncherConfig config)
    {
        _config = config;
        _paths.SetInstallDirectory(_config.InstallDirectory);
        if (!_previewMode) _configService.Save(_config);
        _manifestCheck = null;
        _manifestError = null;
        ShowHomePage();
        Refresh();
        await RefreshManifestAsync();
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        if (IsWindowControl(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private static bool IsWindowControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleWindowState();
    private void ToggleWindowState() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateWindowFrame()
    {
        if (WindowFrame is null) return;
        var maximized = WindowState == WindowState.Maximized;
        WindowFrame.Margin = maximized ? new Thickness(0) : new Thickness(8);
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(20);
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        FrameOrnaments.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        WindowStateIcon.Data = maximized ? IconGlyphs.Restore : IconGlyphs.Maximize;
        UpdateFrameClip();
    }

    private void UpdateFrameClip()
    {
        if (WindowFrame is null) return;
        var radius = WindowState == WindowState.Maximized ? 0 : 20;
        WindowFrame.Clip = new RectangleGeometry(new Rect(0, 0, WindowFrame.ActualWidth, WindowFrame.ActualHeight), radius, radius);
    }

    protected override void OnClosed(EventArgs e)
    {
        _playerHeadCancellation?.Cancel();
        _playerHeadCancellation?.Dispose();
        _playerHeadService.Dispose();
        base.OnClosed(e);
    }
}

internal static class DialogChrome
{
    public static Border Create(Window owner, string title, UIElement body, double width, double height)
    {
        var frame = new Border { Background = new SolidColorBrush(Color.FromRgb(15, 14, 11)), BorderBrush = new SolidColorBrush(Color.FromRgb(66, 56, 37)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), ClipToBounds = true };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new Grid { Background = new SolidColorBrush(Color.FromRgb(20, 18, 14)) };
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) owner.DragMove(); };
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(238, 228, 204)) };
        var close = new Button { Content = "×", Width = 40, Height = 34, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(190, 177, 147)), BorderThickness = new Thickness(0), FontSize = 19, Cursor = Cursors.Hand };
        close.Click += (_, _) => owner.Close();
        bar.Children.Add(heading);
        bar.Children.Add(close);
        Grid.SetRow(body, 1);
        layout.Children.Add(bar);
        layout.Children.Add(body);
        frame.Child = layout;
        owner.Width = width;
        owner.Height = height;
        owner.WindowStyle = WindowStyle.None;
        owner.AllowsTransparency = true;
        owner.Background = Brushes.Transparent;
        owner.ResizeMode = ResizeMode.NoResize;
        owner.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        owner.Content = frame;
        return frame;
    }
}

public sealed class ProfileDialog : Window
{
    private readonly TextBox _nameBox;
    public string PlayerName => _nameBox.Text.Trim();

    public ProfileDialog(string current)
    {
        var body = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        body.Children.Add(new TextBlock { Text = "Nombre de jugador", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(183, 171, 143)) });
        _nameBox = new TextBox { Text = current, MaxLength = 16, Margin = new Thickness(0, 9, 0, 7) };
        body.Children.Add(_nameBox);
        var save = DialogButton("GUARDAR", true);
        save.Margin = new Thickness(0, 16, 0, 0);
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.Click += (_, _) =>
        {
            if (!Regex.IsMatch(PlayerName, "^[A-Za-z0-9_]{3,16}$"))
            {
                MessageBox.Show(this, "Usa entre 3 y 16 caracteres: letras, números o _.", "Perfil", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        body.Children.Add(save);
        DialogChrome.Create(this, "Perfil", body, 420, 252);
        Loaded += (_, _) => { _nameBox.Focus(); _nameBox.SelectAll(); };
    }

    internal static Button DialogButton(string label, bool primary)
    {
        return new Button
        {
            Style = (Style)Application.Current.FindResource("DialogAction"),
            Content = label,
            MinWidth = 142,
            Height = 38,
            Padding = new Thickness(14, 0, 14, 0),
            Background = primary ? new SolidColorBrush(Color.FromRgb(204, 166, 86)) : new SolidColorBrush(Color.FromRgb(31, 29, 23)),
            Foreground = primary ? new SolidColorBrush(Color.FromRgb(28, 23, 14)) : new SolidColorBrush(Color.FromRgb(211, 198, 167)),
            BorderBrush = primary ? new SolidColorBrush(Color.FromRgb(232, 204, 133)) : new SolidColorBrush(Color.FromRgb(67, 57, 38)),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.Bold,
            FontSize = 10,
            Cursor = Cursors.Hand
        };
    }
}

public sealed class SettingsPage : UserControl
{
    private readonly LauncherConfig _source;
    private readonly TextBox _installDirectory;
    private readonly TextBox _ramValue;
    private readonly Slider _ramSlider;
    private bool _syncingRam;

    public Action? BackRequested { get; set; }
    public Func<LauncherConfig, Task>? SaveRequested { get; set; }

    public SettingsPage(LauncherConfig source)
    {
        _source = source;
        var layout = new Grid { Margin = new Thickness(30, 26, 30, 28) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "AJUSTES",
            FontFamily = new FontFamily("Georgia"),
            FontSize = 27,
            VerticalAlignment = VerticalAlignment.Center
        });
        var back = ProfileDialog.DialogButton("VOLVER", false);
        back.MinWidth = 112;
        back.Height = 36;
        back.Click += (_, _) => BackRequested?.Invoke();
        Grid.SetColumn(back, 1);
        header.Children.Add(back);
        layout.Children.Add(header);

        var cards = new Grid { VerticalAlignment = VerticalAlignment.Top };
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var installCard = CreateSettingsCard();
        var installContent = new StackPanel();
        installContent.Children.Add(SectionTitle("INSTALACIÓN"));
        var pathRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _installDirectory = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(source.InstallDirectory) ? LauncherPaths.DefaultInstallDirectory : source.InstallDirectory,
            MinHeight = 40,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "El modpack Lite se guarda junto a esta carpeta, con el sufijo -Lite."
        };
        pathRow.Children.Add(_installDirectory);
        var browse = ProfileDialog.DialogButton("", false);
        browse.MinWidth = 46;
        browse.Width = 46;
        browse.Height = 40;
        browse.Padding = new Thickness(0);
        browse.Margin = new Thickness(9, 0, 0, 0);
        browse.Content = new System.Windows.Shapes.Path
        {
            Data = IconGlyphs.Folder,
            Width = 16,
            Height = 16,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Stroke = new SolidColorBrush(Color.FromRgb(201, 173, 112)),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        browse.ToolTip = "Elegir carpeta";
        Grid.SetColumn(browse, 1);
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Elige la carpeta de instalación de Destiny", InitialDirectory = Directory.Exists(_installDirectory.Text) ? _installDirectory.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Multiselect = false };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) _installDirectory.Text = picker.FolderName;
        };
        pathRow.Children.Add(browse);
        installContent.Children.Add(pathRow);
        installCard.Child = installContent;
        cards.Children.Add(installCard);

        var memoryCard = CreateSettingsCard();
        memoryCard.Margin = new Thickness(14, 0, 0, 0);
        var memoryContent = new StackPanel();
        memoryContent.Children.Add(SectionTitle("MEMORIA"));
        var ramRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        ramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _ramSlider = new Slider
        {
            Minimum = 2,
            Maximum = 16,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = Math.Clamp(source.RamGb, 2, 16),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            ToolTip = "Arrastra para elegir hasta 16 GB"
        };
        _ramSlider.ValueChanged += RamSlider_ValueChanged;
        ramRow.Children.Add(_ramSlider);
        _ramValue = new TextBox
        {
            Text = source.RamGb.ToString(),
            Width = 68,
            Height = 38,
            MaxLength = 7,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 239, 229)),
            Padding = new Thickness(5, 0, 5, 0),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = "Valor en GB"
        };
        _ramValue.TextChanged += RamValue_TextChanged;
        Grid.SetColumn(_ramValue, 1);
        ramRow.Children.Add(_ramValue);
        var ramUnit = new TextBlock { Text = "GB", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(178, 167, 142)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(ramUnit, 2);
        ramRow.Children.Add(ramUnit);
        memoryContent.Children.Add(ramRow);
        memoryCard.Child = memoryContent;
        Grid.SetColumn(memoryCard, 1);
        cards.Children.Add(memoryCard);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(cards);

        var save = ProfileDialog.DialogButton("GUARDAR", true);
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.MinWidth = 156;
        save.Height = 42;
        save.Margin = new Thickness(0, 20, 0, 0);
        save.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_installDirectory.Text) || !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(_installDirectory.Text.Trim())))
            {
                MessageBox.Show(Window.GetWindow(this), "Elige una carpeta válida.", "Instalación", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!int.TryParse(_ramValue.Text, out var ramGb) || ramGb is < 1 or > 1024)
            {
                MessageBox.Show(Window.GetWindow(this), "Introduce un valor entre 1 y 1024 GB.", "Memoria", MessageBoxButton.OK, MessageBoxImage.Information);
                _ramValue.Focus();
                _ramValue.SelectAll();
                return;
            }
            _source.InstallDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_installDirectory.Text.Trim()));
            _source.RamGb = ramGb;
            if (SaveRequested is not null) await SaveRequested(_source);
        };
        content.Children.Add(save);
        Grid.SetRow(content, 1);
        layout.Children.Add(content);
        Content = layout;
    }

    private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingRam || _ramValue is null) return;
        _syncingRam = true;
        _ramValue.Text = ((int)Math.Round(e.NewValue)).ToString();
        _syncingRam = false;
    }

    private void RamValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingRam || _ramSlider is null || !int.TryParse(_ramValue.Text, out var value) || value < 1) return;
        _syncingRam = true;
        _ramSlider.Value = Math.Clamp(value, _ramSlider.Minimum, _ramSlider.Maximum);
        _syncingRam = false;
    }

    private static Border CreateSettingsCard() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(17, 16, 13)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(77, 62, 35)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(11),
        Padding = new Thickness(20, 18, 20, 20)
    };

    private static TextBlock SectionTitle(string title) => new()
    {
        Text = title,
        FontFamily = new FontFamily("Georgia"),
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromRgb(214, 180, 96)),
        Margin = new Thickness(0, 4, 0, 9)
    };

}
