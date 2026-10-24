using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DestinyLauncher;

internal sealed class AddAccountDialog : Window
{
    internal bool MicrosoftRequested { get; private set; }
    internal string? OfflineName { get; private set; }
    internal AddAccountDialog(IEnumerable<string> existingNames, bool allowMicrosoft)
    {
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var body = new Grid { Margin = new Thickness(24, 22, 24, 24) };
        body.RowDefinitions.Add(new RowDefinition()); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var choices = new Grid();
        choices.ColumnDefinitions.Add(new ColumnDefinition()); choices.ColumnDefinitions.Add(new ColumnDefinition());
        var offline = ProfileDialog.DialogButton("Offline", false);
        offline.Height = 86; offline.Margin = new Thickness(0, 0, allowMicrosoft ? 6 : 0, 0); offline.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAutomationId(offline, "ChooseOfflineAccount"); choices.Children.Add(offline);
        var microsoft = ProfileDialog.DialogButton("", false); microsoft.Content = MicrosoftBrand.Label("Microsoft", 14, 21);
        microsoft.Height = 86; microsoft.Margin = new Thickness(6, 0, 0, 0); microsoft.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(microsoft, "Microsoft"); AutomationProperties.SetAutomationId(microsoft, "ChooseMicrosoftAccount");
        microsoft.Visibility = allowMicrosoft ? Visibility.Visible : Visibility.Collapsed;
        if (!allowMicrosoft) Grid.SetColumnSpan(offline, 2);
        Grid.SetColumn(microsoft, 1); choices.Children.Add(microsoft); body.Children.Add(choices);
        var form = new StackPanel { Visibility = Visibility.Collapsed };
        form.Children.Add(new TextBlock { Text = "Nombre de jugador", FontSize = 12, Margin = new Thickness(0, 0, 0, 8), Foreground = ProfileDialog.ResourceBrush("Muted") });
        var input = new TextBox { MaxLength = 16, Height = 40, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(input, "Nombre de jugador"); AutomationProperties.SetAutomationId(input, "AccountNameInput"); form.Children.Add(input);
        var error = new TextBlock { Foreground = ProfileDialog.ResourceBrush("Error"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite); form.Children.Add(error); body.Children.Add(form);
        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = ProfileDialog.DialogButton("Volver", false); back.Visibility = Visibility.Collapsed; back.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(back);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = ProfileDialog.DialogButton("Cancelar", false); cancel.IsCancel = true; cancel.Click += (_, _) => Close(); actions.Children.Add(cancel);
        var add = ProfileDialog.DialogButton("Añadir", true); add.Visibility = Visibility.Collapsed; add.IsDefault = true; add.Margin = new Thickness(10, 0, 0, 0);
        AutomationProperties.SetAutomationId(add, "ConfirmOfflineAccount"); actions.Children.Add(add);
        back.Click += (_, _) => { form.Visibility = Visibility.Collapsed; choices.Visibility = Visibility.Visible; back.Visibility = add.Visibility = Visibility.Collapsed; };
        input.TextChanged += (_, _) => { error.Visibility = Visibility.Collapsed; input.BorderBrush = ProfileDialog.ResourceBrush("Line"); };
        add.Click += (_, _) =>
        {
            var name = input.Text.Trim();
            var message = !Models.LauncherConfig.ValidPlayerName(name) ? "Usa de 3 a 16 letras, números o guiones bajos." : names.Contains(name) ? "Esta cuenta ya está guardada." : null;
            if (message is not null) { error.Text = message; error.Visibility = Visibility.Visible; input.BorderBrush = ProfileDialog.ResourceBrush("Error"); input.Focus(); return; }
            OfflineName = name; DialogResult = true;
        };
        offline.Click += (_, _) => { choices.Visibility = Visibility.Collapsed; form.Visibility = Visibility.Visible; back.Visibility = add.Visibility = Visibility.Visible; input.Focus(); };
        microsoft.Click += (_, _) => { MicrosoftRequested = true; DialogResult = true; };
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 1); body.Children.Add(footer);
        DialogChrome.Create(this, "Añadir cuenta", body, 470, 295);
    }
}
