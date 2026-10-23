using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DestinyLauncher.Services;

namespace DestinyLauncher;

internal sealed class DiscordLinkDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    internal DiscordLinkDialog(DiscordLinkService service, Action<Uri> openBrowser)
    {
        var body = new StackPanel { Margin = new Thickness(24) };
        var status = new TextBlock { Text = "Conectando…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24), Foreground = ProfileDialog.ResourceBrush("Muted") };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        body.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = ProfileDialog.DialogButton("Cancelar", false); cancel.Click += (_, _) => Close(); actions.Children.Add(cancel);
        var retry = ProfileDialog.DialogButton("Reintentar", true); retry.Margin = new Thickness(10, 0, 0, 0); retry.Visibility = Visibility.Collapsed; actions.Children.Add(retry);
        body.Children.Add(actions); DialogChrome.Create(this, "Vincular Discord", body, 460, 240);
        async Task Link()
        {
            retry.Visibility = Visibility.Collapsed; status.Text = "Conectando…"; status.Foreground = ProfileDialog.ResourceBrush("Muted");
            try { await service.LinkAsync(openBrowser, new Progress<string>(text => status.Text = text), _cancel.Token); if (!_cancel.IsCancellationRequested) DialogResult = true; }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            catch (Exception error) { if (!_cancel.IsCancellationRequested) { status.Text = DiscordLinkService.FriendlyError(error); status.Foreground = ProfileDialog.ResourceBrush("Error"); retry.Visibility = Visibility.Visible; } }
        }
        Loaded += async (_, _) => await Link(); retry.Click += async (_, _) => await Link();
        Closed += (_, _) => _cancel.Cancel();
    }
}
