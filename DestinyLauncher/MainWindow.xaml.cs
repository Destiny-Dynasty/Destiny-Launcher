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
    private readonly ZipPackInstaller _zipInstaller;
    private readonly GitHubPackUpdater _manifestUpdater;
    private readonly MinecraftService _minecraft;
    private readonly PlayerHeadService _playerHeadService;
    private readonly bool _previewMode;
    private readonly bool _loadRemoteSkinInPreview;
    private LauncherConfig _config;
    private PackUpdateCheck? _manifestCheck;
    private string? _manifestError;
    private bool _busy;
    private bool _updatingControls;
    private int _manifestRefreshGeneration;
    private bool _statusPulseStarted;
    private string? _playerHeadUsername;
    private CancellationTokenSource? _playerHeadCancellation;
    private Task? _playerHeadLoadTask;

    public MainWindow(bool previewMode = false, bool loadRemoteSkinInPreview = false)
    {
        InitializeComponent();
        _previewMode = previewMode;
        _loadRemoteSkinInPreview = loadRemoteSkinInPreview;
        _log = new AppLogger(_paths);
        _configService = new ConfigService(_paths);
        _zipInstaller = new ZipPackInstaller(_paths, _log);
        _manifestUpdater = new GitHubPackUpdater(_paths, _log);
        _minecraft = new MinecraftService(_paths, _log);
        _playerHeadService = new PlayerHeadService(_paths);
        _config = previewMode ? CreatePreviewConfig() : _configService.Load();

        WindowFrame.SizeChanged += (_, _) => UpdateFrameClip();
        StateChanged += (_, _) => UpdateWindowFrame();
        Loaded += async (_, _) => await RefreshManifestAsync();
        UpdateWindowFrame();
        Refresh();
    }

    private PackDefinition Pack => _config.SelectedEdition == PackEdition.Destiny ? _config.Destiny : _config.DestinyLite;
    internal Task PlayerHeadLoadTask => _playerHeadLoadTask ?? Task.CompletedTask;

    private static LauncherConfig CreatePreviewConfig() => new()
    {
        PlayerName = "Shady935",
        SelectedEdition = PackEdition.Destiny,
        RamGb = 8,
        Destiny = new PackDefinition { Name = "Destiny", Slug = "destiny", Version = "1.4.7" },
        DestinyLite = new PackDefinition { Name = "Destiny Lite", Slug = "destiny-lite", DownloadUrl = "", ManifestUrl = "", Version = "0.1.0" }
    };

    private void Refresh()
    {
        PlayerNameText.Text = string.IsNullOrWhiteSpace(_config.PlayerName) ? "Configurar perfil" : _config.PlayerName;
        PlayerInitial.Text = string.IsNullOrWhiteSpace(_config.PlayerName) ? "D" : _config.PlayerName[..1].ToUpperInvariant();
        RefreshPlayerHead();
        EditionText.Text = Pack.Name;
        VersionText.Text = $"Minecraft {_config.MinecraftVersion}  ·  Fabric {_config.FabricLoaderVersion}";
        HeroEditionText.Text = $"{Pack.Name.ToUpperInvariant()}  /  {_config.MinecraftVersion}";
        LatestText.Text = $"Versión {TargetVersion}";
        RamText.Text = $"{_config.RamGb} GB";

        _updatingControls = true;
        RamSlider.Value = Math.Clamp(_config.RamGb, 4, 12);
        _updatingControls = false;

        var installedVersion = _previewMode ? "1.4.6" : _configService.InstalledVersion(Pack.Slug);
        var hasInstall = !string.IsNullOrEmpty(installedVersion);
        var updateAvailable = hasInstall && NeedsUpdate(installedVersion);
        InstalledText.Text = hasInstall ? $"Instalada {installedVersion}" : "No instalado";
        PrimaryLabel.Text = !hasInstall ? "INSTALAR" : updateAvailable ? "ACTUALIZAR" : "JUGAR";
        StatusText.Text = _manifestError is not null ? "No se pudo comprobar la versión" :
            updateAvailable ? _manifestCheck?.HasChanges == true ? $"Actualización disponible · {_manifestCheck.ChangedFiles.Count} archivos" : "Actualización disponible" :
            hasInstall ? "Listo para jugar" : "Listo para instalar";
        PackInstalledVersionText.Text = hasInstall ? installedVersion : "—";
        PackLatestVersionText.Text = TargetVersion;
        PackStateText.Text = _manifestError is not null ? "NO VERIFICADO" : updateAvailable ? "ACTUALIZACIÓN" : hasInstall ? "AL DÍA" : "LISTO PARA INSTALAR";
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
        PackDetailText.Text = _manifestError is not null
            ? "La instalación local se conserva. Revisa la conexión o la dirección del manifiesto."
            : _manifestCheck is not null
                ? updateAvailable
                    ? _manifestCheck.ChangedFiles.Count > 0
                        ? $"{_manifestCheck.ChangedFiles.Count} archivos por actualizar · {_manifestCheck.TotalFiles} en el manifiesto."
                        : $"Nueva versión {TargetVersion}; todos los archivos ya están actualizados."
                    : $"Los {_manifestCheck.TotalFiles} archivos del modpack están verificados."
                : updateAvailable
                    ? $"La versión {TargetVersion} está lista para instalar desde el origen configurado."
                : string.IsNullOrWhiteSpace(Pack.ManifestUrl)
                    ? hasInstall ? "El estado se basa en la versión del ZIP configurada." : "Configura el ZIP o el manifiesto del modpack en Ajustes."
                    : "La verificación de archivos se realizará al conectar.";
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
        if (string.IsNullOrWhiteSpace(pack.ManifestUrl))
        {
            _manifestCheck = null;
            _manifestError = null;
            Refresh();
            return;
        }
        try
        {
            SetStatus("Comprobando archivos del modpack…");
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

        SetBusy(true, "Preparando el modpack…");
        var finalStatus = "Minecraft iniciado";
        try
        {
            var progress = new Progress<(string status, double value)>(SetProgress);
            var pack = Pack;
            if (!string.IsNullOrWhiteSpace(pack.ManifestUrl))
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
                        finalStatus = "Sin conexión a GitHub · iniciando la versión instalada";
                    }
                    catch (TaskCanceledException ex) when (!string.IsNullOrWhiteSpace(installedVersion))
                    {
                        _log.Info($"GitHub check timed out ({ex.Message}); launching installed {pack.Slug} {installedVersion}.");
                        finalStatus = "Tiempo de espera agotado · iniciando la versión instalada";
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
            else
            {
                var installed = _configService.InstalledVersion(pack.Slug);
                if (installed != pack.Version)
                {
                    await _zipInstaller.InstallAsync(pack, progress, CancellationToken.None);
                    _configService.SaveInstalled(pack.Slug, pack.Version);
                }
            }

            await _minecraft.PrepareAndLaunchAsync(_config, pack, progress, CancellationToken.None);
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
        HttpRequestException => "No se pudo conectar. Comprueba tu conexión y las URLs configuradas.",
        UnauthorizedAccessException => "Windows no permitió escribir los archivos del modpack.",
        InvalidDataException => $"El paquete descargado no es válido. {ex.Message}",
        IOException => $"No se pudo guardar o reemplazar un archivo. Comprueba el espacio disponible. {ex.Message}",
        _ => $"No se pudo completar la operación. Revisa el registro del launcher para ver los detalles. {ex.Message}"
    };

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        PrimaryButton.IsEnabled = !busy;
        DestinyButton.IsEnabled = !busy;
        LiteButton.IsEnabled = !busy;
        RamSlider.IsEnabled = !busy;
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

    private void RamSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls || _config is null || RamSlider is null) return;
        _config.RamGb = (int)Math.Round(e.NewValue);
        RamText.Text = $"{_config.RamGb} GB";
        if (!_previewMode) _configService.Save(_config);
    }

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

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new SettingsDialog(_config) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _config = dialog.Config;
            if (!_previewMode) _configService.Save(_config);
            _manifestCheck = null;
            _manifestError = null;
            Refresh();
            await RefreshManifestAsync();
        }
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
        body.Children.Add(new TextBlock { Text = "Entre 3 y 16 letras, números o guion bajo.", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(117, 109, 94)) });
        var save = DialogButton("GUARDAR PERFIL", true);
        save.Margin = new Thickness(0, 20, 0, 0);
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
        DialogChrome.Create(this, "Perfil del jugador", body, 420, 252);
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

public sealed class SettingsDialog : Window
{
    private readonly LauncherConfig _source;
    private readonly TextBox _destinyZip;
    private readonly TextBox _destinyManifest;
    private readonly TextBox _liteZip;
    private readonly TextBox _liteManifest;
    private readonly TextBox _server;

    public LauncherConfig Config { get; private set; }

    public SettingsDialog(LauncherConfig source)
    {
        _source = source;
        Config = source;
        var stack = new StackPanel { Margin = new Thickness(23, 18, 23, 18) };
        stack.Children.Add(SectionTitle("DESTINY"));
        _destinyZip = AddField(stack, "ZIP del paquete", source.Destiny.DownloadUrl);
        _destinyManifest = AddField(stack, "Manifiesto HTTPS de GitHub (opcional)", source.Destiny.ManifestUrl, "Raw GitHub: manifests/destiny.json");
        stack.Children.Add(SectionTitle("DESTINY LITE"));
        _liteZip = AddField(stack, "ZIP del paquete", source.DestinyLite.DownloadUrl);
        _liteManifest = AddField(stack, "Manifiesto HTTPS de GitHub (opcional)", source.DestinyLite.ManifestUrl, "Raw GitHub: manifests/destiny-lite.json");
        stack.Children.Add(SectionTitle("CONEXIÓN"));
        _server = AddField(stack, "Servidor de Destiny", source.ServerAddress);
        stack.Children.Add(new TextBlock { Text = "Los manifiestos permiten descargar solo los mods que cambiaron.", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(142, 131, 106)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        var save = ProfileDialog.DialogButton("GUARDAR CAMBIOS", true);
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.Margin = new Thickness(0, 18, 0, 0);
        save.Click += (_, _) =>
        {
            _source.Destiny.DownloadUrl = _destinyZip.Text.Trim();
            _source.Destiny.ManifestUrl = _destinyManifest.Text.Trim();
            _source.DestinyLite.DownloadUrl = _liteZip.Text.Trim();
            _source.DestinyLite.ManifestUrl = _liteManifest.Text.Trim();
            _source.ServerAddress = _server.Text.Trim();
            Config = _source;
            DialogResult = true;
        };
        stack.Children.Add(save);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stack };
        DialogChrome.Create(this, "Ajustes del launcher", scroll, 620, 740);
    }

    private static TextBlock SectionTitle(string title) => new()
    {
        Text = title,
        FontFamily = new FontFamily("Georgia"),
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromRgb(214, 180, 96)),
        Margin = new Thickness(0, 4, 0, 9)
    };

    private static TextBox AddField(Panel parent, string label, string value, string? hint = null)
    {
        parent.Children.Add(new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(178, 167, 142)), Margin = new Thickness(0, 5, 0, 4) });
        var field = new TextBox { Text = value, MinHeight = 36, Margin = new Thickness(0, 0, 0, hint is null ? 10 : 3) };
        parent.Children.Add(field);
        if (hint is not null) parent.Children.Add(new TextBlock { Text = hint, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(117, 109, 94)), Margin = new Thickness(0, 0, 0, 8) });
        return field;
    }
}
