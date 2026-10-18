using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace DestinyLauncher;

// A draft independent of the active profile and settings until Save is accepted.
internal sealed class AccountsEditor : UserControl
{
    private readonly ObservableCollection<string> _names;
    private readonly TextBox _input;
    private readonly ListBox _list;
    private readonly TextBlock _error;
    internal IReadOnlyList<string> AccountNames => _names.ToArray();
    internal event Action? SaveRequested;
    internal event Action? CancelRequested;

    internal AccountsEditor(IEnumerable<string> names, string active)
    {
        _names = new(names);
        var body = new Grid { Margin = new Thickness(24, 20, 24, 24) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            body.RowDefinitions.Add(new RowDefinition { Height = height });
        var entry = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        entry.ColumnDefinitions.Add(new ColumnDefinition());
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input = new TextBox { MaxLength = 16, MinWidth = 100, Height = 40, ToolTip = "Nombre de jugador", VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_input, "Nombre de jugador");
        AutomationProperties.SetAutomationId(_input, "AccountNameInput");
        var add = ProfileDialog.DialogButton("Añadir", false);
        add.Margin = new Thickness(10, 0, 0, 0);
        AutomationProperties.SetAutomationId(add, "AccountAddButton");
        add.Click += (_, _) => AddPending();
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddPending(); e.Handled = true; } };
        entry.Children.Add(_input); Grid.SetColumn(add, 1); entry.Children.Add(add);
        body.Children.Add(entry);
        _error = new TextBlock { Foreground = ProfileDialog.ResourceBrush("Error"), TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        _input.TextChanged += (_, _) => { _error.Text = ""; _error.Visibility = Visibility.Collapsed; };
        Grid.SetRow(_error, 1); body.Children.Add(_error);
        _list = new ListBox { ItemsSource = _names, Background = ProfileDialog.ResourceBrush("Canvas"),
            Foreground = ProfileDialog.ResourceBrush("Ink"), BorderBrush = ProfileDialog.ResourceBrush("Line"),
            BorderThickness = new Thickness(1), ItemContainerStyle = (Style)Application.Current.FindResource("AccountListItem"),
            SelectionMode = SelectionMode.Single, Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(_list, "Cuentas guardadas");
        AutomationProperties.SetAutomationId(_list, "AccountsList");
        _list.SelectedItem = _names.FirstOrDefault(n => n.Equals(active, StringComparison.OrdinalIgnoreCase));
        Grid.SetRow(_list, 2); body.Children.Add(_list);
        var actions = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var remove = ProfileDialog.DialogButton("Eliminar", false);
        remove.HorizontalAlignment = HorizontalAlignment.Left; remove.IsEnabled = _list.SelectedItem is string;
        AutomationProperties.SetAutomationId(remove, "AccountRemoveButton");
        _list.SelectionChanged += (_, _) => remove.IsEnabled = _list.SelectedItem is string;
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItem is not string selected) return;
            var index = _list.SelectedIndex; _names.Remove(selected);
            if (_names.Count > 0) _list.SelectedIndex = Math.Min(index, _names.Count - 1);
        };
        actions.Children.Add(remove);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = ProfileDialog.DialogButton("Cancelar", false); cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => CancelRequested?.Invoke();
        var save = ProfileDialog.DialogButton("Guardar", true);
        AutomationProperties.SetAutomationId(save, "AccountsSaveButton");
        save.Click += (_, _) => { if (string.IsNullOrWhiteSpace(_input.Text) || AddPending()) SaveRequested?.Invoke(); };
        right.Children.Add(cancel); right.Children.Add(save); Grid.SetColumn(right, 1); actions.Children.Add(right);
        Grid.SetRow(actions, 3); body.Children.Add(actions);
        Content = body;
    }

    private bool AddPending()
    {
        var name = _input.Text.Trim();
        var error = !Models.LauncherConfig.ValidPlayerName(name) ? "Usa de 3 a 16 letras, números o guiones bajos." :
            _names.Contains(name, StringComparer.OrdinalIgnoreCase) ? "Esta cuenta ya está guardada." : null;
        if (error is not null) { _error.Text = error; _error.Visibility = Visibility.Visible; return false; }
        _names.Add(name); _list.SelectedItem = name; _input.Clear(); return true;
    }
    internal void ShowSaveError() { _error.Text = "No se pudieron guardar las cuentas."; _error.Visibility = Visibility.Visible; }
}

internal sealed class AccountsDialog : Window
{
    internal AccountsEditor Editor { get; }
    internal AccountsDialog(IEnumerable<string> names, string active, Func<IEnumerable<string>, bool> save)
    {
        Editor = new AccountsEditor(names, active);
        Editor.SaveRequested += () => { if (save(Editor.AccountNames)) DialogResult = true; else Editor.ShowSaveError(); };
        Editor.CancelRequested += Close;
        DialogChrome.Create(this, "Cuentas", Editor, 520, 440);
    }
}
