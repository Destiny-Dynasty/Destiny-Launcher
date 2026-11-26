using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher.Linux;

public sealed partial class MainWindow
{
    private sealed record AccountRow(string Name, string MicrosoftId = "")
    {
        public bool IsMicrosoft => !string.IsNullOrEmpty(MicrosoftId);
    }
    private CancellationTokenSource? _headRequest;
    private string? _headUsername;

    private static Control MicrosoftSymbol(double size)
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, 21, 21)) });
        foreach (var (x, y, color) in new[] { (1, 1, "#F25022"), (11, 1, "#7FBA00"), (1, 11, "#00A4EF"), (11, 11, "#FFB900") })
            drawing.Children.Add(new GeometryDrawing { Brush = Brush(color), Geometry = new RectangleGeometry(new Rect(x, y, 9, 9)) });
        return new Image { Source = new DrawingImage(drawing), Width = size, Height = size };
    }
    private static Control TypeLabel(bool microsoft, double iconSize = 13)
    {
        if (!microsoft) return new TextBlock { Text = "Offline", FontSize = 10, Foreground = Brush("#B3AC9E"), VerticalAlignment = VerticalAlignment.Center };
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 6 };
        label.Children.Add(MicrosoftSymbol(iconSize));
        label.Children.Add(new TextBlock { Text = "Microsoft", FontSize = 10, Foreground = Brush("#B3AC9E"), VerticalAlignment = VerticalAlignment.Center });
        return label;
    }
    private void RefreshProfile()
    {
        var settings = _launcher.Settings;
        var valid = LauncherConfig.ValidPlayerName(settings.PlayerName);
        ProfileLabel.Text = valid ? settings.PlayerName : "Añadir nombre";
        PlayerInitial.Text = valid ? settings.PlayerName[..1].ToUpperInvariant() : "D";
        AccountType.Children.Clear();
        AccountType.IsVisible = valid;
        AccountType.Children.Add(TypeLabel(!string.IsNullOrEmpty(settings.MicrosoftAccountId), 12));
        var menu = settings.MultiAccountEnabled || !string.IsNullOrEmpty(settings.MicrosoftAccountId);
        ProfileActionGlyph.Data = menu ? Geometry.Parse("M3 6l5 5 5-5") : LinuxIconGlyphs.Edit;
        ToolTip.SetTip(ProfileButton, menu ? "Cambiar cuenta" : "Editar tu nombre de jugador");
        if (_headUsername == settings.PlayerName) return;
        _headUsername = settings.PlayerName;
        _headRequest?.Cancel(); _headRequest?.Dispose();
        (PlayerHead.Source as IDisposable)?.Dispose(); PlayerHead.Source = null; PlayerHead.IsVisible = false; PlayerInitial.IsVisible = true;
        if (!valid || App.IsVisualAudit) return;
        _headRequest = CancellationTokenSource.CreateLinkedTokenSource(_uiLifetime.Token);
        _ = LoadHeadAsync(PlayerHead, PlayerInitial, settings.PlayerName, _headRequest.Token);
    }
    private async Task LoadHeadAsync(Image head, TextBlock initial, string name, CancellationToken token)
    {
        try
        {
            void Display(Bitmap? bitmap)
            {
                if (token.IsCancellationRequested) { bitmap?.Dispose(); return; }
                if (bitmap is null) return;
                var previous = head.Source as IDisposable;
                head.Source = bitmap; head.IsVisible = true; initial.IsVisible = false;
                RenderOptions.SetBitmapInterpolationMode(head, BitmapInterpolationMode.None);
                previous?.Dispose();
            }
            Display(await _heads.GetCachedHeadAsync(name, token));
            Display(await _heads.GetHeadAsync(name, token));
        }
        catch (OperationCanceledException) { }
        catch { /* Verified cached portraits or initials remain usable while offline. */ }
    }
    private Control AccountIdentity(AccountRow row)
    {
        var identity = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*,Auto") };
        var layers = new Grid();
        var initial = new TextBlock { Text = row.Name.Length > 0 ? row.Name[..1].ToUpperInvariant() : "D", FontSize = 14, Foreground = Brush("#E2C58B"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var head = new Image { Stretch = Stretch.Uniform, IsVisible = false };
        layers.Children.Add(initial); layers.Children.Add(head);
        identity.Children.Add(new Border { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#22211D"), BorderBrush = Brush("#35332C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(3), Child = layers });
        var name = new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1); identity.Children.Add(name);
        var type = TypeLabel(row.IsMicrosoft); type.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(type, 2); identity.Children.Add(type);
        CancellationTokenSource? request = null;
        identity.AttachedToVisualTree += (_, _) =>
        {
            if (App.IsVisualAudit) return;
            request = CancellationTokenSource.CreateLinkedTokenSource(_uiLifetime.Token);
            _ = LoadHeadAsync(head, initial, row.Name, request.Token);
        };
        identity.DetachedFromVisualTree += (_, _) => { request?.Cancel(); request?.Dispose(); request = null; (head.Source as IDisposable)?.Dispose(); head.Source = null; };
        return identity;
    }
    private async Task<IReadOnlyList<LauncherAccountProfile>> MicrosoftProfilesAsync()
    {
        try { return await Task.Run(_launcher.MicrosoftProfiles); }
        catch { return []; }
    }
    private async void Profile_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var settings = _launcher.Settings;
        if (!settings.MultiAccountEnabled && string.IsNullOrEmpty(settings.MicrosoftAccountId)) { await ShowProfileDialogAsync(); return; }
        var menu = new ContextMenu { MinWidth = ProfileButton.Bounds.Width, Placement = PlacementMode.Bottom };
        menu.Classes.Add("accounts");
        void Add(AccountRow row)
        {
            var item = new MenuItem { Header = AccountIdentity(row), IsChecked = row.IsMicrosoft ? row.MicrosoftId == settings.MicrosoftAccountId : string.IsNullOrEmpty(settings.MicrosoftAccountId) && row.Name.Equals(settings.PlayerName, StringComparison.OrdinalIgnoreCase) };
            item.Classes.Add("account");
            item.Click += (_, _) => { settings.PlayerName = row.Name; settings.MicrosoftAccountId = row.MicrosoftId; _launcher.SaveSettings(); RefreshProfile(); };
            menu.Items.Add(item);
        }
        foreach (var name in settings.Accounts) Add(new AccountRow(name));
        foreach (var profile in await MicrosoftProfilesAsync()) Add(new AccountRow(profile.Username, profile.Id));
        if (!settings.MultiAccountEnabled)
        {
            var add = new MenuItem { Header = "Añadir cuenta de Microsoft" }; add.Classes.Add("account");
            add.Click += async (_, _) => await SignInMicrosoftAsync(addAnother: true); menu.Items.Add(add);
        }
        if (!string.IsNullOrEmpty(settings.MicrosoftAccountId))
        {
            var signOut = new MenuItem { Header = "Cerrar sesión de Microsoft" }; signOut.Classes.Add("account");
            signOut.Click += async (_, _) => await SignInMicrosoftAsync(); menu.Items.Add(signOut);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Gestionar cuentas" }; manage.Classes.Add("account");
        manage.Click += async (_, _) => { if (settings.MultiAccountEnabled) await ShowAccountsAsync(); else await ShowProfileDialogAsync(); };
        menu.Items.Add(manage); ProfileButton.ContextMenu = menu; menu.Open(ProfileButton);
    }

    private async Task ShowAccountsAsync()
    {
        var settings = _launcher.Settings;
        var originalMicrosoft = await MicrosoftProfilesAsync();
        var originalIds = originalMicrosoft.Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var draft = settings.Accounts.Select(name => new AccountRow(name)).Concat(originalMicrosoft.Select(profile => new AccountRow(profile.Username, profile.Id))).ToList();
        var layout = new Grid { Margin = new Thickness(24, 20, 24, 24), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var add = DialogButton("Añadir cuenta"); add.HorizontalAlignment = HorizontalAlignment.Right; add.Margin = new Thickness(0, 0, 0, 14); layout.Children.Add(add);
        var error = new TextBlock { Foreground = Brush("#E0A396"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), IsVisible = false }; Grid.SetRow(error, 1); layout.Children.Add(error);
        var list = new ListBox { ItemsSource = draft.ToArray(), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4), ItemTemplate = new FuncDataTemplate<AccountRow>((row, _) => row is null ? null : AccountIdentity(row)) };
        list.Classes.Add("accounts");
        list.SelectedItem = draft.FirstOrDefault(row => !string.IsNullOrEmpty(settings.MicrosoftAccountId) ? row.MicrosoftId == settings.MicrosoftAccountId : !row.IsMicrosoft && row.Name.Equals(settings.PlayerName, StringComparison.OrdinalIgnoreCase));
        var frame = new Border { CornerRadius = new CornerRadius(10), Background = Brush("#780D0E0C"), BorderBrush = Brush("#35332C"), BorderThickness = new Thickness(1), Padding = new Thickness(5), Child = list }; Grid.SetRow(frame, 2); layout.Children.Add(frame);
        var dialog = CreateDialog("Cuentas", layout, 550, 450);
        add.Click += async (_, _) =>
        {
            var choice = await ShowAddAccountAsync(draft.Where(row => !row.IsMicrosoft).Select(row => row.Name), dialog);
            if (choice is null) return;
            if (choice.IsMicrosoft)
            {
                var browser = new LinuxSystemBrowserWebUI();
                try
                {
                    add.IsEnabled = false;
                    var profile = await _launcher.SignInMicrosoftAsync(browser, browser.RedirectUri, cancellationToken: _uiLifetime.Token, makeActive: false);
                    if (!originalIds.Contains(profile.Id)) linked.Add(profile.Id);
                    choice = new AccountRow(profile.Username, profile.Id);
                    draft.RemoveAll(row => row.MicrosoftId == profile.Id);
                }
                catch (Exception ex) { error.Text = MicrosoftAccountService.FriendlyError(ex); error.IsVisible = true; return; }
                finally { add.IsEnabled = true; }
            }
            draft.Add(choice); list.ItemsSource = draft.ToArray(); list.SelectedItem = choice;
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 20, 0, 0) };
        var remove = DialogButton("Eliminar"); remove.HorizontalAlignment = HorizontalAlignment.Left; remove.IsEnabled = list.SelectedItem is AccountRow;
        list.SelectionChanged += (_, _) => { remove.IsEnabled = list.SelectedItem is AccountRow; error.IsVisible = false; };
        remove.Click += (_, _) => { if (list.SelectedItem is AccountRow row) { var index = list.SelectedIndex; draft.Remove(row); list.ItemsSource = draft.ToArray(); if (draft.Count > 0) list.SelectedIndex = Math.Min(index, draft.Count - 1); } };
        footer.Children.Add(remove);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var cancel = DialogButton("Cancelar"); cancel.Click += (_, _) => dialog.Close();
        var accepted = false;
        var save = DialogButton("Guardar", true);
        save.Click += async (_, _) =>
        {
            try
            {
                var removed = originalIds.Concat(linked).Except(draft.Where(row => row.IsMicrosoft).Select(row => row.MicrosoftId), StringComparer.OrdinalIgnoreCase).ToArray();
                if (removed.Length > 0) await Task.Run(() => _launcher.ForgetMicrosoft(removed));
                settings.Accounts = draft.Where(row => !row.IsMicrosoft).Select(row => row.Name).ToList();
                if (!draft.Any(row => row.IsMicrosoft ? row.MicrosoftId == settings.MicrosoftAccountId : string.IsNullOrEmpty(settings.MicrosoftAccountId) && row.Name == settings.PlayerName))
                {
                    var active = draft.FirstOrDefault(); settings.PlayerName = active?.Name ?? ""; settings.MicrosoftAccountId = active?.MicrosoftId ?? "";
                }
                _launcher.SaveSettings(); accepted = true; RefreshProfile(); dialog.Close();
            }
            catch { error.Text = "No se pudieron guardar las cuentas."; error.IsVisible = true; }
        };
        actions.Children.Add(cancel); actions.Children.Add(save); Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); layout.Children.Add(footer);
        await ShowOwnedDialogAsync(dialog);
        if (!accepted && linked.Count > 0) try { await Task.Run(() => _launcher.ForgetMicrosoft(linked)); } catch { SetStatus("No se pudieron descartar las cuentas nuevas", "Comprueba que el llavero esté desbloqueado.", true); }
    }
    private async Task<AccountRow?> ShowAddAccountAsync(IEnumerable<string>? existingNames = null, Window? owner = null)
    {
        var names = (existingNames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AccountRow? result = null;
        var body = new Grid { Margin = new Thickness(24, 22, 24, 24), RowDefinitions = new RowDefinitions("*,Auto") };
        var dialog = CreateDialog("Añadir cuenta", body, 490, 310);
        var choices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var offline = DialogButton(""); offline.Classes.Add("provider"); offline.Height = 132; offline.Margin = new Thickness(0, 0, 6, 0); offline.VerticalAlignment = VerticalAlignment.Top;
        var identity = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        identity.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M8,0 A5,5 0 1 1 7.99,0 M0,25 C0,13 16,13 16,25"), Width = 24, Height = 30, Stretch = Stretch.Uniform, Stroke = Brush("#E2C58B"), StrokeThickness = 1.5, Margin = new Thickness(0, 0, 0, 12) });
        identity.Children.Add(new TextBlock { Text = "Offline", FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }); offline.Content = identity;
        var microsoft = DialogButton(""); microsoft.Classes.Add("provider"); microsoft.Height = 132; microsoft.Margin = new Thickness(6, 0, 0, 0); microsoft.VerticalAlignment = VerticalAlignment.Top;
        var brand = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center }; var symbol = MicrosoftSymbol(30); symbol.Margin = new Thickness(0, 0, 0, 12); brand.Children.Add(symbol); brand.Children.Add(new TextBlock { Text = "Microsoft", FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }); microsoft.Content = brand;
        Grid.SetColumn(microsoft, 1); choices.Children.Add(offline); choices.Children.Add(microsoft); body.Children.Add(choices);
        var form = new StackPanel { IsVisible = false };
        form.Children.Add(new TextBlock { Text = "Nombre de jugador", FontSize = 12, Foreground = Brush("#B3AC9E"), Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { MaxLength = 16, Height = 40 }; form.Children.Add(input);
        var error = new TextBlock { FontSize = 12, Foreground = Brush("#E0A396"), TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) }; form.Children.Add(error); body.Children.Add(form);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 20, 0, 0) };
        var back = DialogButton("Volver"); back.IsVisible = false; back.HorizontalAlignment = HorizontalAlignment.Left; footer.Children.Add(back);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var cancel = DialogButton("Cancelar"); cancel.Click += (_, _) => dialog.Close();
        var add = DialogButton("Añadir", true); add.IsVisible = false;
        void Accept()
        {
            var name = (input.Text ?? "").Trim();
            var message = !LauncherConfig.ValidPlayerName(name) ? "Usa de 3 a 16 letras, números o guiones bajos." : names.Contains(name) ? "Esta cuenta ya está guardada." : null;
            if (message is not null) { error.Text = message; error.IsVisible = true; input.BorderBrush = Brush("#E0A396"); input.Focus(); return; }
            result = new AccountRow(name); dialog.Close();
        }
        input.TextChanged += (_, _) => { error.IsVisible = false; input.BorderBrush = Brush("#35332C"); };
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
        add.Click += (_, _) => Accept();
        back.Click += (_, _) => { form.IsVisible = false; choices.IsVisible = true; back.IsVisible = add.IsVisible = false; };
        offline.Click += (_, _) => { choices.IsVisible = false; form.IsVisible = back.IsVisible = add.IsVisible = true; input.Focus(); };
        microsoft.Click += (_, _) => { result = new AccountRow("", "microsoft"); dialog.Close(); };
        actions.Children.Add(cancel); actions.Children.Add(add); Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 1); body.Children.Add(footer);
        await ShowOwnedDialogAsync(dialog, owner); return result;
    }
}

