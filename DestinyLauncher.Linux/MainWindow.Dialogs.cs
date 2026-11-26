using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher.Linux;

public sealed partial class MainWindow
{
    private async Task<bool> ConfirmDiscardSettingsAsync()
    {
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        body.Children.Add(new TextBlock { Text = "Tienes cambios en los ajustes. Puedes seguir editándolos o descartarlos.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B3AC9E") });
        var dialog = CreateDialog("Cambios sin guardar", body, 470, 230);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 24, 0, 0) };
        var accepted = false;
        var back = DialogButton("Seguir editando"); back.Click += (_, _) => dialog.Close();
        var discard = DialogButton("Descartar cambios", true); discard.Click += (_, _) => { accepted = true; dialog.Close(); };
        actions.Children.Add(back); actions.Children.Add(discard); body.Children.Add(actions);
        await ShowOwnedDialogAsync(dialog); return accepted;
    }
    private async Task ShowProfileDialogAsync()
    {
        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        body.Children.Add(new TextBlock { Text = "Nombre de jugador", FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Text = _launcher.Settings.PlayerName, MaxLength = 16, FontSize = 15 };
        body.Children.Add(input);
        var validation = new TextBlock { FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Foreground = Brush("#E0A396"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        body.Children.Add(validation);
        var dialog = CreateDialog("Tu perfil", body, 450, 350);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = DialogButton("Cancelar"); cancel.Click += (_, _) => dialog.Close();
        var save = DialogButton("Guardar nombre", true);
        save.IsEnabled = LauncherConfig.ValidPlayerName(input.Text ?? "");
        var originalName = input.Text ?? "";
        var edited = false;
        input.TextChanged += (_, _) =>
        {
            if (!edited && input.Text == originalName) return;
            edited = true;
            var valid = LauncherConfig.ValidPlayerName((input.Text ?? "").Trim());
            save.IsEnabled = valid; validation.IsVisible = !valid;
            validation.Text = "Usa de 3 a 16 letras, números o guiones bajos, sin espacios.";
            input.BorderBrush = Brush(valid ? "#35332C" : "#E0A396");
        };
        save.Click += (_, _) =>
        {
            var name = (input.Text ?? "").Trim();
            if (!LauncherConfig.ValidPlayerName(name)) return;
            _launcher.Settings.PlayerName = name; _launcher.SaveSettings(); RefreshProfile(); dialog.Close();
        };
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter && save.IsEnabled) { save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; } };
        actions.Children.Add(cancel); actions.Children.Add(save); body.Children.Add(actions);
        var microsoft = DialogButton(string.IsNullOrEmpty(_launcher.Settings.MicrosoftAccountId) ? "Iniciar sesión con Microsoft" : "Cerrar sesión de Microsoft");
        microsoft.Margin = new Thickness(0, 18, 0, 0); microsoft.HorizontalAlignment = HorizontalAlignment.Stretch;
        microsoft.Click += async (_, _) => { dialog.Close(); await SignInMicrosoftAsync(); };
        body.Children.Add(microsoft);
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        await ShowOwnedDialogAsync(dialog);
    }
    private async Task SignInMicrosoftAsync(bool addAnother = false)
    {
        if (_isBusy) return;
        SetBusy(true, "Preparando la cuenta Microsoft…");
        try
        {
            if (!addAnother && !string.IsNullOrWhiteSpace(_launcher.Settings.MicrosoftAccountId)) await Task.Run(_launcher.SignOutMicrosoft);
            else
            {
                var browser = new LinuxSystemBrowserWebUI();
                var progress = new Progress<string>(message => SetStatus(message, ""));
                await Task.Run(() => _launcher.SignInMicrosoftAsync(browser, browser.RedirectUri, progress, _uiLifetime.Token));
            }
            RefreshProfile(); SetStatus("", "");
        }
        catch (Exception ex) { SetStatus(MicrosoftAccountService.FriendlyError(ex), "", true); }
        finally { SetBusy(false); }
    }
    private void GameOptions_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Width = 300, Placement = PlacementMode.Top, HorizontalOffset = -252 };
        void Add(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled && !_isBusy };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("Ajustes del juego", () => ShowPage("settings"));
        Add("Crear acceso directo en el escritorio", CreateDesktopShortcut);
        Add("Ver capturas", () => { var path = Path.Combine(_launcher.Paths.GameDirectory, "screenshots"); Directory.CreateDirectory(path); OpenPath(path); }, Directory.Exists(_launcher.Paths.GameDirectory));
        Add("Buscar actualizaciones", () => _ = RefreshCatalogAsync());
        menu.Items.Add(new Separator { Margin = new Thickness(8, 4) });
        Add("Reparar modpack", () => _ = ShowRepairAsync(), Directory.Exists(InstancePath()));
        var profileConfig = _launcher.Paths.ProfileConfigDirectory(_launcher.Settings.SelectedEdition switch
        { PackEdition.DestinyLite => "destiny-lite", PackEdition.DestinyStaff => "destiny-staff", _ => "destiny" });
        Add("Abrir configuración de este perfil", () => OpenPath(profileConfig), Directory.Exists(profileConfig));
        GameOptionsButton.ContextMenu = menu; menu.Open(GameOptionsButton);
    }
    private async Task ShowRepairAsync()
    {
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        body.Children.Add(new TextBlock { Text = $"¿Restaurar los archivos oficiales de {EditionText.Text}?", TextWrapping = TextWrapping.Wrap });
        var remove = new CheckBox { Content = "Borrar mods de terceros", IsChecked = false, Margin = new Thickness(0, 20, 0, 0) }; remove.Classes.Add("repair"); body.Children.Add(remove);
        var dialog = CreateDialog("Reparar modpack", body, 470, 260);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = DialogButton("Cancelar"); cancel.Click += (_, _) => dialog.Close();
        var repair = DialogButton("Reparar", true);
        repair.Click += async (_, _) =>
        {
            dialog.Close(); SetBusy(true, "Comprobando archivos");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_uiLifetime.Token);
            _packCancellation = cancellation; CancelDownloadButton.IsEnabled = true;
            try { await _launcher.RepairPackAsync(_launcher.Settings.SelectedEdition, remove.IsChecked == true, new Progress<(string status, double value)>(SetDownloadProgress), cancellation.Token); SetStatus("Modpack reparado", ""); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !_uiLifetime.IsCancellationRequested) { SetStatus("Descarga cancelada", "Puedes volver a intentarlo cuando quieras."); }
            catch (Exception ex) { SetStatus("No se pudo reparar el modpack", ex.Message, true); }
            finally { _packCancellation = null; if (!_uiLifetime.IsCancellationRequested) { SetBusy(false); UpdateVersions(); } }
        };
        actions.Children.Add(cancel); actions.Children.Add(repair); body.Children.Add(actions);
        await ShowOwnedDialogAsync(dialog);
    }
    private void CreateDesktopShortcut()
    {
        if (!OperatingSystem.IsLinux()) { SetStatus("Acceso directo de Linux", "Esta opción crea un archivo .desktop al ejecutar el launcher en Linux."); return; }
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
            Directory.CreateDirectory(desktop);
            var executable = Environment.ProcessPath ?? throw new IOException("No se encontró el ejecutable.");
            var edition = _launcher.Settings.SelectedEdition;
            var name = edition switch { PackEdition.DestinyLite => "Destiny Lite", PackEdition.DestinyStaff => "Destiny Staff", _ => "Destiny" };
            var file = Path.Combine(desktop, "Destiny Launcher - " + name + ".desktop");
            static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";
            var arguments = " --edition " + edition + (App.DataRoot is null ? "" : " --data-root " + Quote(_launcher.Paths.Root));
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "destiny-emblem.png");
            File.WriteAllText(file, $"[Desktop Entry]\nVersion=1.0\nType=Application\nName=Destiny Launcher - {name}\nExec={Quote(executable)}{arguments}\nIcon={icon}\nTerminal=false\nCategories=Game;\n");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            SetStatus("Acceso directo creado", "");
        }
        catch (Exception ex) { SetStatus("No se pudo crear el acceso directo", ex.Message, true); }
    }
    private async void InstallationOptions_Click(object? sender, RoutedEventArgs e)
    {
        if (_discord.HasCredentialMarker)
        {
            await RefreshDiscordAsync();
            await InformAsync("Más opciones", _launcher.StaffAllowed ? "Tu cuenta tiene acceso a Destiny Staff." : "Tu cuenta de Discord no tiene acceso a más modpacks.");
            return;
        }
        if (await ConfirmAsync("Más opciones", "Inicia sesión con Discord para acceder a más opciones.", "Vincular Discord", "Cancelar")) await ManageDiscordAsync();
    }
    private async void DiscordSettings_Click(object? sender, RoutedEventArgs e)
    {
        await ManageDiscordAsync();
    }
    private void RefreshDiscordStatus()
    {
        DiscordStatusText.Text = _discord.Access is { } access ? access.Username + (access.Staff ? " · Staff" : "") : _discord.HasCredentialMarker ? "Sin verificar" : "Sin vincular";
        DiscordSettingsButton.Content = _discord.HasCredentialMarker ? "Desvincular" : "Vincular Discord";
    }
    private async Task RefreshDiscordAsync()
    {
        try
        {
            var access = _discord.HasCredentialMarker ? await Task.Run(() => _discord.RefreshAsync(_uiLifetime.Token)) : null;
            if (_uiLifetime.IsCancellationRequested) return;
            _launcher.SetStaffAccess(access?.Staff == true);
            if (_requestedStaff && access?.Staff == true && !_discordChecked) _launcher.SelectEdition(PackEdition.DestinyStaff);
        }
        catch (OperationCanceledException) when (_uiLifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_uiLifetime.IsCancellationRequested) { _launcher.SetStaffAccess(false); SetStatus("Discord no disponible", LinuxDiscordLinkService.FriendlyError(ex), true); } }
        finally { if (!_uiLifetime.IsCancellationRequested) { _discordChecked = true; RefreshDiscordStatus(); UpdateVersions(); UpdateEditionLayout(); } }
    }
    private async Task ManageDiscordAsync()
    {
        if (_isBusy) return;
        if (_discord.HasCredentialMarker)
        {
            if (!await ConfirmAsync("Desvincular Discord", "¿Desvincular esta cuenta de Discord?", "Desvincular", "Cancelar")) return;
            try { await _discord.UnlinkAsync(_uiLifetime.Token); }
            catch (OperationCanceledException) when (_uiLifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { await InformAsync("Discord", LinuxDiscordLinkService.FriendlyError(ex)); return; }
        }
        else
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_uiLifetime.Token);
            var body = new StackPanel { Margin = new Thickness(24) };
            var status = new TextBlock { Text = "Conectando…", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B3AC9E"), Margin = new Thickness(0, 0, 0, 24) };
            body.Children.Add(status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
            var dialog = CreateDialog("Vincular Discord", body, 460, 240);
            var cancel = DialogButton("Cancelar"); cancel.Click += (_, _) => dialog.Close();
            var retry = DialogButton("Reintentar", true); retry.IsVisible = false;
            actions.Children.Add(cancel); actions.Children.Add(retry); body.Children.Add(actions);
            async Task Link()
            {
                retry.IsVisible = false; status.Text = "Conectando…"; status.Foreground = Brush("#B3AC9E");
                var progress = new Progress<string>(message => status.Text = message);
                try { await Task.Run(() => _discord.LinkAsync(uri => OpenPath(uri.AbsoluteUri), progress, cancellation.Token)); if (!cancellation.IsCancellationRequested) dialog.Close(); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex) { if (!cancellation.IsCancellationRequested) { status.Text = LinuxDiscordLinkService.FriendlyError(ex); status.Foreground = Brush("#E0A396"); retry.IsVisible = true; } }
            }
            dialog.Opened += async (_, _) => await Link();
            retry.Click += async (_, _) => await Link();
            dialog.Closed += (_, _) => cancellation.Cancel();
            await ShowOwnedDialogAsync(dialog);
        }
        await RefreshDiscordAsync();
        await RefreshCatalogAsync();
    }
    private async Task<bool> ConfirmAsync(string title, string message, string acceptText, string cancelText, Window? owner = null)
    {
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B3AC9E") });
        var dialog = CreateDialog(title, body, 470, 230);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 24, 0, 0) };
        var accepted = false;
        var cancel = DialogButton(cancelText); cancel.Click += (_, _) => dialog.Close();
        var accept = DialogButton(acceptText, true); accept.Click += (_, _) => { accepted = true; dialog.Close(); };
        actions.Children.Add(cancel); actions.Children.Add(accept); body.Children.Add(actions);
        await ShowOwnedDialogAsync(dialog, owner); return accepted;
    }
    private async Task InformAsync(string title, string message, Window? owner = null)
    {
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 24), Spacing = 24 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B3AC9E") });
        var dialog = CreateDialog(title, body, 470, 230);
        var accept = DialogButton("Entendido", true); accept.HorizontalAlignment = HorizontalAlignment.Right; accept.Click += (_, _) => dialog.Close();
        body.Children.Add(accept); await ShowOwnedDialogAsync(dialog, owner);
    }
    private static Button DialogButton(string text, bool primary = false)
    {
        var button = new Button { Content = text, MinWidth = 108, Height = 40, Padding = new Thickness(16, 8) };
        button.Classes.Add(primary ? "gold" : "dialog"); return button;
    }
    private Window CreateDialog(string title, Control body, double width, double height)
    {
        var dialog = new Window { Title = title, Width = Math.Ceiling(width * RenderScaling) / RenderScaling, Height = Math.Ceiling(height * RenderScaling) / RenderScaling, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent], Foreground = Brush("#F2EBDD"), FontFamily = new FontFamily("Segoe UI, Noto Sans, Liberation Sans, DejaVu Sans") };
        var layout = new Grid { RowDefinitions = new RowDefinitions("48,*") };
        var header = new Grid { Background = Brushes.Transparent };
        header.PointerPressed += (_, e) => { if (e.GetCurrentPoint(dialog).Properties.IsLeftButtonPressed) dialog.BeginMoveDrag(e); };
        header.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Georgia, Liberation Serif, DejaVu Serif"), FontSize = 18, Margin = new Thickness(24, 0, 55, 0), VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M0 0L14 14M14 0L0 14"), Width = 14, Height = 14, Stroke = Brush("#B3AC9E"), StrokeThickness = 1.4 },
            Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        close.Classes.Add("chrome"); close.Classes.Add("close"); close.Click += (_, _) => dialog.Close(); header.Children.Add(close);
        header.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(24, 0), Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative), GradientStops = [new GradientStop(Colors.Transparent, 0), new GradientStop(Color.Parse("#64C6A362"), 1)] } });
        layout.Children.Add(header); Grid.SetRow(body, 1); layout.Children.Add(body);
        var surface = new Grid();
        surface.Children.Add(new Image { Source = LoadBitmap("experiment/obsidian-gold-v1/obsidian-texture.png"), Stretch = Stretch.UniformToFill, Opacity = .075, IsHitTestVisible = false });
        dialog.Closed += (_, _) =>
        {
            var grain = (Image)surface.Children[0];
            var texture = grain.Source as IDisposable;
            grain.Source = null;
            texture?.Dispose();
        };
        surface.Children.Add(layout);
        surface.SizeChanged += (_, _) => surface.Clip = new RectangleGeometry(new Rect(surface.Bounds.Size), 13, 13);
        dialog.Content = new Border { CornerRadius = new CornerRadius(14), BorderBrush = Brush("#77603B"), BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = [new GradientStop(Color.Parse("#27241D"), 0), new GradientStop(Color.Parse("#111210"), 1)] }, Child = surface };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };
        dialog.Opened += (_, _) => UiMotion.Enter(body);
        _activeDialog = dialog;
        return dialog;
    }
    private async Task ShowOwnedDialogAsync(Window dialog, Window? owner = null)
    {
        var surface = owner?.Content as Control ?? WindowFrame;
        var opacity = surface.Opacity;
        surface.Opacity = opacity * .75; _video?.Pause(); _newsTimer.Stop();
        try { await dialog.ShowDialog(owner ?? this); }
        finally { surface.Opacity = opacity; _activeDialog = owner; if (IsActive && _page == "home" && !_isBusy) _video?.Play(); UpdateNewsPlayback(); }
    }
}



