using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher;

internal static class DialogChrome
{
    internal static void Create(Window owner, string title, UIElement body, double width, double height)
    {
        owner.Title = title;
        owner.Width = Math.Min(width, SystemParameters.WorkArea.Width - 24);
        owner.Height = Math.Min(height, SystemParameters.WorkArea.Height - 24);
        owner.WindowStyle = WindowStyle.None;
        owner.ResizeMode = ResizeMode.NoResize;
        owner.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        owner.Background = (Brush)Application.Current.FindResource("Canvas");
        owner.Foreground = (Brush)Application.Current.FindResource("Ink");
        owner.ShowInTaskbar = false;
        owner.UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(owner, new WindowChrome { CaptionHeight = 48, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        var frame = new Border { BorderBrush = (Brush)Application.Current.FindResource("GoldDim"), BorderThickness = new Thickness(1) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new Grid { Background = (Brush)Application.Current.FindResource("Panel") };
        bar.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Georgia"), FontSize = 18, Margin = new Thickness(24, 0, 55, 0), VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Style = (Style)Application.Current.FindResource("CloseControl"), Content = "×", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Cerrar" };
        AutomationProperties.SetName(close, "Cerrar diálogo");
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => owner.Close();
        bar.Children.Add(close);
        layout.Children.Add(bar);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        frame.Child = layout;
        owner.Content = frame;
        owner.Loaded += (_, _) => UiMotion.Enter(body as FrameworkElement ?? frame);
        owner.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { owner.Close(); e.Handled = true; } };
    }
}

public sealed class ProfileDialog : Window
{
    private readonly TextBox _nameBox;
    public string PlayerName => _nameBox.Text.Trim();
    public ProfileDialog(string current)
    {
        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        var nameLabel = new Label { Content = "Nombre de jugador", Padding = new Thickness(0, 0, 0, 8), Foreground = ResourceBrush("Ink"), FontSize = 12 };
        body.Children.Add(nameLabel);
        _nameBox = new TextBox { Text = current, MaxLength = 16, FontSize = 15, ToolTip = "De 3 a 16 letras, números o guiones bajos." };
        nameLabel.Target = _nameBox;
        AutomationProperties.SetName(_nameBox, "Nombre de jugador");
        AutomationProperties.SetAutomationId(_nameBox, "PlayerNameInput");
        body.Children.Add(_nameBox);
        var validation = new TextBlock { FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Polite);
        body.Children.Add(validation);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = DialogButton("Cancelar", false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 10, 0); cancel.Click += (_, _) => Close();
        var save = DialogButton("Guardar nombre", true); save.IsDefault = true;
        void Validate(bool editing)
        {
            var valid = Regex.IsMatch(PlayerName, "^[A-Za-z0-9_]{3,16}$");
            save.IsEnabled = valid;
            validation.Text = valid || !editing ? "" : "Usa de 3 a 16 letras, números o guiones bajos, sin espacios.";
            validation.Visibility = string.IsNullOrEmpty(validation.Text) ? Visibility.Collapsed : Visibility.Visible;
            validation.Foreground = ResourceBrush(valid || !editing ? "Muted" : "Error");
            _nameBox.BorderBrush = ResourceBrush(valid || !editing ? "Line" : "Error");
        }
        _nameBox.TextChanged += (_, _) => Validate(true);
        Validate(false);
        save.Click += (_, _) => { if (Regex.IsMatch(PlayerName, "^[A-Za-z0-9_]{3,16}$")) DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(save); body.Children.Add(actions);
        DialogChrome.Create(this, "Tu perfil", new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false }, 450, 280);
        Loaded += (_, _) => { _nameBox.Focus(); _nameBox.SelectAll(); };
    }
    internal static Brush ResourceBrush(string key) => (Brush)Application.Current.FindResource(key);
    internal static Button DialogButton(string label, bool primary) => new()
    {
        Style = (Style)Application.Current.FindResource(primary ? "GoldButton" : "QuietButton"),
        Content = label, MinWidth = 108, Height = 40, Padding = new Thickness(16, 8, 16, 8)
    };
}

internal static class LauncherDialog
{
    internal static bool Confirm(Window owner, string title, string message, string accept, string cancel) => Show(owner, title, message, accept, cancel);
    internal static void Inform(Window owner, string title, string message) => Show(owner, title, message, "Entendido", null);
    private static bool Show(Window owner, string title, string message, string accept, string? cancel)
    {
        var dialog = new Window { Owner = owner };
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = ProfileDialog.ResourceBrush("Muted"), FontSize = 13 });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        if (cancel is not null)
        {
            var back = ProfileDialog.DialogButton(cancel, false); back.Margin = new Thickness(0, 0, 10, 0); back.IsDefault = true; back.IsCancel = true; back.Click += (_, _) => dialog.Close(); actions.Children.Add(back);
        }
        var proceed = ProfileDialog.DialogButton(accept, true); proceed.IsDefault = cancel is null; proceed.Click += (_, _) => dialog.DialogResult = true; actions.Children.Add(proceed);
        body.Children.Add(actions);
        DialogChrome.Create(dialog, title, body, 470, 230);
        return dialog.ShowDialog() == true;
    }
}

public sealed class SettingsPage : UserControl
{
    private readonly LauncherConfig _source;
    private readonly TextBox _installDirectory;
    private readonly TextBox _ramValue;
    private readonly Slider _ramSlider;
    private readonly TextBox _javaArguments;
    private readonly string _installationFolderName;
    private readonly string _originalBaseDirectory;
    private bool _syncingRam;
    private bool _saved;
    public bool HasChanges => !_saved && (_installDirectory.Text != _originalBaseDirectory || _ramValue.Text != _source.RamGb.ToString()
        || _javaArguments.Text != _source.JavaArguments);
    public Action? BackRequested { get; set; }
    public Func<LauncherConfig, Task>? SaveRequested { get; set; }
    internal void MarkSaved() => _saved = true;

    public SettingsPage(LauncherConfig source)
    {
        _source = source;
        _installationFolderName = LauncherPaths.InstallationFolderName(source.InstallDirectory);
        _originalBaseDirectory = LauncherPaths.InstallationBaseDirectory(source.InstallDirectory);
        TextBlock? validation = null;
        var layout = new Grid { Margin = new Thickness(28, 24, 28, 20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        heading.Children.Add(new TextBlock { Text = "Ajustes", FontFamily = new FontFamily("Georgia"), FontSize = 28 });
        layout.Children.Add(heading);
        var content = new StackPanel();
        var installation = Card();
        var installBody = new StackPanel();
        installBody.Children.Add(Title("Carpeta de instalación"));
        var pathRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _installDirectory = new TextBox { Text = _originalBaseDirectory, ToolTip = _originalBaseDirectory };
        AutomationProperties.SetName(_installDirectory, "Carpeta de instalación");
        AutomationProperties.SetAutomationId(_installDirectory, "InstallDirectoryInput");
        _installDirectory.TextChanged += (_, _) => { _installDirectory.ToolTip = _installDirectory.Text; _installDirectory.BorderBrush = ProfileDialog.ResourceBrush("Line"); if (validation is not null) validation.Visibility = Visibility.Collapsed; };
        pathRow.Children.Add(_installDirectory);
        var folderName = new TextBlock
        {
            Text = "\\" + _installationFolderName + (source.SelectedEdition == PackEdition.DestinyLite ? "-Lite" : ""),
            FontSize = 12, Foreground = ProfileDialog.ResourceBrush("Muted"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        AutomationProperties.SetName(folderName, "Nombre fijo de la carpeta del juego");
        AutomationProperties.SetAutomationId(folderName, "FixedInstallationFolder");
        Grid.SetColumn(folderName, 1); pathRow.Children.Add(folderName);
        var browse = ProfileDialog.DialogButton("", false); browse.MinWidth = 42; browse.Width = 42; browse.Padding = new Thickness(0); browse.Margin = new Thickness(10, 0, 0, 0); browse.ToolTip = "Elegir carpeta base";
        browse.Content = new System.Windows.Shapes.Path { Data = IconGlyphs.Folder, Width = 19, Height = 19, Stretch = Stretch.Uniform, Fill = ProfileDialog.ResourceBrush("Gold") };
        AutomationProperties.SetName(browse, "Elegir carpeta base de instalación");
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta base de instalación", InitialDirectory = Directory.Exists(_installDirectory.Text) ? _installDirectory.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Multiselect = false };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) _installDirectory.Text = picker.FolderName;
        };
        Grid.SetColumn(browse, 2); pathRow.Children.Add(browse); installBody.Children.Add(pathRow);
        installation.Child = installBody; content.Children.Add(installation);
        var memory = Card(); memory.Margin = new Thickness(0, 14, 0, 0);
        var memoryBody = new StackPanel(); memoryBody.Children.Add(Title("Memoria del juego"));
        var ramRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        ramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _ramSlider = new Slider { Style = (Style)Application.Current.FindResource("RamSlider"), Minimum = 2, Maximum = 16, SmallChange = 1, LargeChange = 2, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Clamp(source.RamGb, 2, 16), Margin = new Thickness(0, 0, 24, 0), VerticalAlignment = VerticalAlignment.Top };
        _ramSlider.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(Slider.Value)) { Source = _ramSlider, StringFormat = "{0:0} GB" });
        AutomationProperties.SetName(_ramSlider, "Memoria del juego en GB");
        _ramSlider.ValueChanged += (_, e) => { if (_syncingRam || _ramValue is null) return; _syncingRam = true; _ramValue.Text = ((int)Math.Round(e.NewValue)).ToString(); _syncingRam = false; };
        ramRow.Children.Add(_ramSlider);
        _ramValue = new TextBox { Text = source.RamGb.ToString(), Width = 60, MaxLength = 4, TextAlignment = TextAlignment.Center, FontSize = 16, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 5, 2, 5) };
        AutomationProperties.SetName(_ramValue, "Memoria en GB");
        AutomationProperties.SetAutomationId(_ramValue, "RamInput");
        _ramValue.TextChanged += (_, _) => { _ramValue.BorderBrush = ProfileDialog.ResourceBrush("Line"); if (validation is not null) validation.Visibility = Visibility.Collapsed; if (_syncingRam || !int.TryParse(_ramValue.Text, out var value)) return; _syncingRam = true; _ramSlider.Value = Math.Clamp(value, 2, 16); _syncingRam = false; };
        var valueBadge = new Border { Height = 40, VerticalAlignment = VerticalAlignment.Top, Background = ProfileDialog.ResourceBrush("Canvas"), BorderBrush = ProfileDialog.ResourceBrush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        valueBadge.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(TextBox.BorderBrush)) { Source = _ramValue });
        var valueContents = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 12, 0) };
        valueContents.Children.Add(_ramValue);
        valueContents.Children.Add(new TextBlock { Text = "GB", Foreground = ProfileDialog.ResourceBrush("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        valueBadge.Child = valueContents;
        Grid.SetColumn(valueBadge, 1); ramRow.Children.Add(valueBadge);
        memoryBody.Children.Add(ramRow); memory.Child = memoryBody; content.Children.Add(memory);
        var java = Card(); java.Margin = new Thickness(0, 14, 0, 0);
        var javaBody = new StackPanel();
        var javaHeading = new Grid();
        javaHeading.ColumnDefinitions.Add(new ColumnDefinition());
        javaHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var javaTitle = Title("Argumentos de Java"); javaTitle.VerticalAlignment = VerticalAlignment.Center;
        javaHeading.Children.Add(javaTitle);
        var resetJava = ProfileDialog.DialogButton("Restablecer", false);
        resetJava.Height = 30; resetJava.MinHeight = 30; resetJava.MinWidth = 0; resetJava.Padding = new Thickness(12, 4, 12, 4); resetJava.FontSize = 11;
        AutomationProperties.SetAutomationId(resetJava, "ResetJavaArguments");
        AutomationProperties.SetName(resetJava, "Restablecer argumentos de Java");
        Grid.SetColumn(resetJava, 1); javaHeading.Children.Add(resetJava);
        javaBody.Children.Add(javaHeading);
        _javaArguments = new TextBox { Text = source.JavaArguments, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 74, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0), MaxLength = 8192 };
        AutomationProperties.SetName(_javaArguments, "Argumentos de Java");
        AutomationProperties.SetAutomationId(_javaArguments, "JavaArgumentsInput");
        _javaArguments.TextChanged += (_, _) => { _javaArguments.BorderBrush = ProfileDialog.ResourceBrush("Line"); if (validation is not null) validation.Visibility = Visibility.Collapsed; };
        resetJava.Click += (_, _) => _javaArguments.Text = LauncherConfig.DefaultJavaArguments;
        javaBody.Children.Add(_javaArguments); java.Child = javaBody; content.Children.Add(java);
        var scroller = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 7, 0) };
        Grid.SetRow(scroller, 1); layout.Children.Add(scroller);
        var footer = new Grid { Margin = new Thickness(0, 16, 7, 0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        validation = Help("", new Thickness(0, 0, 0, 10)); validation.Foreground = ProfileDialog.ResourceBrush("Error"); validation.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Polite); footer.Children.Add(validation);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var back = ProfileDialog.DialogButton("Volver", false); back.Margin = new Thickness(0, 0, 10, 0); back.Click += (_, _) => BackRequested?.Invoke(); actions.Children.Add(back);
        var save = ProfileDialog.DialogButton("Guardar ajustes", true); actions.Children.Add(save);
        save.Click += async (_, _) =>
        {
            void Error(string message, TextBox input) { validation.Text = message; validation.Visibility = Visibility.Visible; input.BorderBrush = ProfileDialog.ResourceBrush("Error"); input.Focus(); input.SelectAll(); }
            string path;
            try
            {
                path = Environment.ExpandEnvironmentVariables(_installDirectory.Text.Trim());
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) { Error("Elige una ruta completa, por ejemplo C:\\Juegos\\Destiny.", _installDirectory); return; }
                path = Path.Combine(Path.GetFullPath(path), _installationFolderName);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { Error("La ruta contiene caracteres no válidos. Elige otra carpeta.", _installDirectory); return; }
            if (!int.TryParse(_ramValue.Text, out var ram) || ram is < 1 or > 1024) { Error("Introduce un número entero entre 1 y 1024 GB.", _ramValue); return; }
            try { JavaArguments.Parse(_javaArguments.Text); }
            catch (FormatException ex) { Error(ex.Message, _javaArguments); return; }
            var updated = new LauncherConfig { PlayerName = source.PlayerName, SelectedEdition = source.SelectedEdition, InstallDirectory = path,
                RamGb = ram, JavaArguments = _javaArguments.Text, MinecraftVersion = source.MinecraftVersion, FabricLoaderVersion = source.FabricLoaderVersion, Destiny = source.Destiny, DestinyLite = source.DestinyLite };
            save.IsEnabled = false;
            try { if (SaveRequested is not null) await SaveRequested(updated); }
            catch (Exception ex) { Error(MainWindow.GetFriendlyError(ex), _installDirectory); }
            finally { save.IsEnabled = true; }
        };
        Grid.SetRow(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); layout.Children.Add(footer); Content = layout;
    }
    private static TextBlock Title(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold };
    private static TextBlock Help(string text, Thickness margin) => new() { Text = text, FontSize = 12, Foreground = ProfileDialog.ResourceBrush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = margin };
    private static Border Card() => new() { Background = ProfileDialog.ResourceBrush("Panel"), BorderBrush = ProfileDialog.ResourceBrush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(20, 17, 20, 18) };
}
