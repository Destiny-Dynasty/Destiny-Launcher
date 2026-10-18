using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace DestinyLauncher;

internal sealed class RepairPrompt : StackPanel
{
    internal CheckBox RemoveThirdPartyMods { get; }
    internal event Action? Confirmed;
    internal event Action? Cancelled;
    internal RepairPrompt(string packName)
    {
        Margin = new Thickness(24, 22, 24, 24);
        Children.Add(new TextBlock { Text = $"¿Restaurar los archivos oficiales de {packName}?", Foreground = ProfileDialog.ResourceBrush("Ink"), TextWrapping = TextWrapping.Wrap });
        RemoveThirdPartyMods = new CheckBox { Content = "Borrar mods de terceros", IsChecked = false, Margin = new Thickness(0, 20, 0, 0),
            Style = (Style)Application.Current.FindResource("RepairCheckBox") };
        AutomationProperties.SetAutomationId(RemoveThirdPartyMods, "RemoveThirdPartyMods");
        AutomationProperties.SetName(RemoveThirdPartyMods, "Borrar mods de terceros");
        Children.Add(RemoveThirdPartyMods);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = ProfileDialog.DialogButton("Cancelar", false); cancel.IsDefault = true; cancel.Margin = new Thickness(0, 0, 10, 0);
        cancel.Click += (_, _) => Cancelled?.Invoke();
        var repair = ProfileDialog.DialogButton("Reparar", true);
        repair.Click += (_, _) => Confirmed?.Invoke();
        actions.Children.Add(cancel); actions.Children.Add(repair); Children.Add(actions);
    }
}

internal sealed class RepairDialog : Window
{
    internal RepairPrompt Prompt { get; }
    internal RepairDialog(string packName)
    {
        Prompt = new RepairPrompt(packName);
        Prompt.Confirmed += () => DialogResult = true;
        Prompt.Cancelled += Close;
        DialogChrome.Create(this, "Reparar modpack", Prompt, 470, 260);
    }
}
