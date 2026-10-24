using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DestinyLauncher.Services;

namespace DestinyLauncher;

internal sealed record AccountRow(string Name, string MicrosoftId = "")
{
    internal bool IsMicrosoft => !string.IsNullOrEmpty(MicrosoftId);
}

// Offline edits and removals remain a draft until Save. Microsoft credentials are
// only removed on acceptance; newly linked accounts are discarded on cancellation.
internal sealed class AccountsEditor : UserControl
{
    private readonly ObservableCollection<AccountRow> _accounts;
    private readonly ListBox _list;
    private readonly TextBlock _error;
    internal IReadOnlyList<AccountRow> Rows => _accounts.ToArray();
    internal IReadOnlyList<string> AccountNames => _accounts.Where(a => !a.IsMicrosoft).Select(a => a.Name).ToArray();
    internal event Action? AddRequested;
    internal event Action? SaveRequested;
    internal event Action? CancelRequested;
    internal AccountsEditor(IEnumerable<string> names, string active, PlayerHeadService? heads = null,
        IEnumerable<MicrosoftProfile>? microsoft = null, string? activeMicrosoftId = null)
    {
        if (heads is not null) SetValue(AccountIdentity.HeadServiceProperty, heads);
        _accounts = new(names.Select(name => new AccountRow(name)).Concat((microsoft ?? []).Select(p => new AccountRow(p.Username, p.Id))));
        var body = new Grid { Margin = new Thickness(24, 20, 24, 24) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            body.RowDefinitions.Add(new RowDefinition { Height = height });
        var add = ProfileDialog.DialogButton("Añadir cuenta", false); add.HorizontalAlignment = HorizontalAlignment.Right; add.Margin = new Thickness(0, 0, 0, 14);
        AutomationProperties.SetAutomationId(add, "AccountAddButton"); add.Click += (_, _) => AddRequested?.Invoke(); body.Children.Add(add);
        _error = new TextBlock { Foreground = ProfileDialog.ResourceBrush("Error"), TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite); Grid.SetRow(_error, 1); body.Children.Add(_error);
        _list = new ListBox { ItemsSource = _accounts, Background = ProfileDialog.ResourceBrush("Canvas"), Foreground = ProfileDialog.ResourceBrush("Ink"),
            BorderBrush = ProfileDialog.ResourceBrush("Line"), BorderThickness = new Thickness(1), ItemContainerStyle = (Style)Application.Current.FindResource("AccountListItem"),
            SelectionMode = SelectionMode.Single, Padding = new Thickness(4) };
        _list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(AccountIdentity)) };
        AutomationProperties.SetName(_list, "Cuentas guardadas"); AutomationProperties.SetAutomationId(_list, "AccountsList");
        _list.SelectedItem = _accounts.FirstOrDefault(a => !string.IsNullOrEmpty(activeMicrosoftId) ? a.MicrosoftId == activeMicrosoftId : !a.IsMicrosoft && a.Name.Equals(active, StringComparison.OrdinalIgnoreCase));
        Grid.SetRow(_list, 2); body.Children.Add(_list);
        var actions = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var remove = ProfileDialog.DialogButton("Eliminar", false); remove.HorizontalAlignment = HorizontalAlignment.Left; remove.IsEnabled = _list.SelectedItem is AccountRow;
        AutomationProperties.SetAutomationId(remove, "AccountRemoveButton");
        _list.SelectionChanged += (_, _) => { remove.IsEnabled = _list.SelectedItem is AccountRow; _error.Visibility = Visibility.Collapsed; };
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItem is not AccountRow selected) return;
            var index = _list.SelectedIndex; _accounts.Remove(selected);
            if (_accounts.Count > 0) _list.SelectedIndex = Math.Min(index, _accounts.Count - 1);
        };
        actions.Children.Add(remove);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = ProfileDialog.DialogButton("Cancelar", false); cancel.Margin = new Thickness(0, 0, 8, 0); cancel.Click += (_, _) => CancelRequested?.Invoke();
        var save = ProfileDialog.DialogButton("Guardar", true); AutomationProperties.SetAutomationId(save, "AccountsSaveButton"); save.Click += (_, _) => SaveRequested?.Invoke();
        right.Children.Add(cancel); right.Children.Add(save); Grid.SetColumn(right, 1); actions.Children.Add(right);
        Grid.SetRow(actions, 3); body.Children.Add(actions); Content = body;
    }
    internal bool AddLocal(string name)
    {
        name = name.Trim();
        var message = !Models.LauncherConfig.ValidPlayerName(name) ? "Usa de 3 a 16 letras, números o guiones bajos." :
            _accounts.Any(a => !a.IsMicrosoft && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "Esta cuenta ya está guardada." : null;
        if (message is not null) { _error.Text = message; _error.Visibility = Visibility.Visible; return false; }
        var row = new AccountRow(name); _accounts.Add(row); _list.SelectedItem = row; return true;
    }
    internal void AddMicrosoft(MicrosoftProfile profile)
    {
        var existing = _accounts.FirstOrDefault(a => a.IsMicrosoft && a.MicrosoftId == profile.Id);
        var row = new AccountRow(profile.Username, profile.Id);
        if (existing is null) _accounts.Add(row); else _accounts[_accounts.IndexOf(existing)] = row;
        _list.SelectedItem = row;
    }
    internal void ShowSaveError() { _error.Text = "No se pudieron guardar las cuentas."; _error.Visibility = Visibility.Visible; }
}

internal sealed class AccountsDialog : Window
{
    internal AccountsEditor Editor { get; }
    internal AccountsDialog(IEnumerable<string> names, string active, Func<IReadOnlyList<AccountRow>, bool> save, PlayerHeadService? heads = null,
        IReadOnlyList<MicrosoftProfile>? microsoft = null, string? activeMicrosoftId = null,
        Func<Window, MicrosoftProfile?>? signIn = null, Action<IEnumerable<string>>? discardLinked = null)
    {
        var originalIds = (microsoft ?? []).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var accepted = false;
        Editor = new AccountsEditor(names, active, heads, microsoft, activeMicrosoftId);
        Editor.AddRequested += () =>
        {
            var choice = new AddAccountDialog(Editor.AccountNames, signIn is not null) { Owner = this };
            if (choice.ShowDialog() != true) return;
            if (choice.MicrosoftRequested)
            {
                if (signIn?.Invoke(this) is not { } profile) return;
                if (!originalIds.Contains(profile.Id)) linked.Add(profile.Id);
                Editor.AddMicrosoft(profile);
            }
            else if (choice.OfflineName is { } name) Editor.AddLocal(name);
        };
        Editor.SaveRequested += () => { if (save(Editor.Rows)) { accepted = true; DialogResult = true; } else Editor.ShowSaveError(); };
        Editor.CancelRequested += Close;
        Closed += (_, _) => { if (!accepted && linked.Count > 0) discardLinked?.Invoke(linked); };
        DialogChrome.Create(this, "Cuentas", Editor, 550, 450);
    }
}
