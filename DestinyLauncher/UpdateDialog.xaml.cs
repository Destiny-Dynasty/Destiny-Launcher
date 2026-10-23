using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using DestinyLauncher.Services;

namespace DestinyLauncher;

internal partial class UpdateDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly LauncherUpdateService _service;
    private readonly LauncherRelease _release;
    private readonly string _installDirectory;
    private bool _downloading, _closeRequested, _closed;
    internal bool InstallerStarted { get; private set; }

    internal UpdateDialog(LauncherUpdateService service, LauncherRelease release, string installDirectory)
    {
        _service = service; _release = release; _installDirectory = installDirectory;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        CurrentVersion.Text = "v" + service.CurrentVersion.ToString(3);
        ReleaseVersion.Text = "Versión " + release.Version;
        DownloadSize.Text = $"{release.Size / 1048576d:F1} MB";
        ChangelogItems.ItemsSource = release.Changelog ?? [];
        ChangelogSection.Visibility = release.Changelog is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => UpdateButton.Focus();
        Closed += (_, _) => { _closed = true; _cancel.Cancel(); };
    }

    internal bool? ShowStandalone(Window launcher)
    {
        Owner = launcher;
        launcher.Hide();
        try { return ShowDialog(); }
        catch { if (launcher.IsLoaded) launcher.Show(); throw; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_downloading && !InstallerStarted)
        {
            e.Cancel = true;
            _closeRequested = true;
            CancelButton.IsEnabled = CloseButton.IsEnabled = false;
            Status.Text = "Cancelando descarga…";
            _cancel.Cancel();
        }
        base.OnClosing(e);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        _downloading = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Descargando…";
        ProgressSection.Visibility = Visibility.Visible;
        Status.Text = "Descargando actualización";
        Status.Foreground = ProfileDialog.ResourceBrush("Muted");
        DownloadProgress.Value = 0; Percentage.Text = "0 %";
        try
        {
            var installer = await _service.DownloadAsync(_release, new Progress<double>(value =>
            {
                if (_closed || _closeRequested) return;
                DownloadProgress.Value = value;
                Percentage.Text = $"{value:P0}";
                Status.Text = value < 1 ? "Descargando actualización" : "Verificando actualización…";
            }), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            Status.Text = "Reiniciando launcher…";
            using var process = Process.Start(LauncherUpdateService.InstallCommand(installer, _installDirectory))
                ?? throw new IOException("No se pudo iniciar el instalador.");
            InstallerStarted = true;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_closed || _closeRequested) return;
            Status.Text = ex is InvalidDataException ? ex.Message : "No se pudo descargar la actualización. Vuelve a intentarlo.";
            Status.Foreground = ProfileDialog.ResourceBrush("Error");
            Percentage.Text = "";
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Reintentar";
        }
        finally
        {
            _downloading = false;
            if (_closeRequested && !_closed) Close();
        }
    }
}
