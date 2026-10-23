using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using DestinyLauncher.Services;

namespace DestinyLauncher;

internal sealed class UpdateDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    internal bool InstallerStarted { get; private set; }
    internal UpdateDialog(LauncherUpdateService service, LauncherRelease release, string installDirectory)
    {
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        body.Children.Add(new TextBlock { Text = "Destiny Launcher " + release.Version, FontSize = 23, FontFamily = new System.Windows.Media.FontFamily("Georgia"), Margin = new Thickness(0, 0, 0, 18) });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ProfileDialog.ResourceBrush("Muted"), Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(status, "LauncherUpdateStatus");
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(progress, "LauncherUpdateProgress");
        body.Children.Add(status); body.Children.Add(progress);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var later = ProfileDialog.DialogButton("Más tarde", false); later.Margin = new Thickness(0, 0, 10, 0); later.Click += (_, _) => Close();
        var update = ProfileDialog.DialogButton("Actualizar", true); AutomationProperties.SetAutomationId(update, "InstallLauncherUpdate");
        update.Click += async (_, _) =>
        {
            update.IsEnabled = false; later.Content = "Cancelar";
            status.Visibility = progress.Visibility = Visibility.Visible; status.Text = "Descargando actualización…";
            status.Foreground = ProfileDialog.ResourceBrush("Muted"); progress.Value = 0;
            try
            {
                var installer = await service.DownloadAsync(release, new Progress<double>(value =>
                {
                    progress.Value = value;
                    status.Text = value < 1 ? $"Descargando actualización · {value:P0}" : "Verificando actualización…";
                }), _cancel.Token);
                _cancel.Token.ThrowIfCancellationRequested();
                status.Text = "Reiniciando launcher…";
                using var process = Process.Start(LauncherUpdateService.InstallCommand(installer, installDirectory))
                    ?? throw new IOException("No se pudo iniciar el instalador.");
                InstallerStarted = true; DialogResult = true;
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            catch (Exception ex)
            {
                status.Text = ex is InvalidDataException ? ex.Message : "No se pudo actualizar. Puedes volver a intentarlo.";
                status.Foreground = ProfileDialog.ResourceBrush("Error"); update.IsEnabled = true; later.Content = "Más tarde";
            }
        };
        actions.Children.Add(later); actions.Children.Add(update); body.Children.Add(actions);
        DialogChrome.Create(this, "Actualización disponible", body, 490, 280);
        Closed += (_, _) => _cancel.Cancel();
    }
}
