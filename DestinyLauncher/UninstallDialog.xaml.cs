using System.Windows;
using DestinyLauncher.Services;

namespace DestinyLauncher;

internal partial class UninstallDialog : Window
{
    private readonly InstallationDataService _data;
    private bool _removing;

    internal UninstallDialog(InstallationDataService data)
    {
        _data = data;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        VersionLabel.Text = "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "");
        Closing += (_, e) => e.Cancel = _removing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
        };
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Selection_Changed(object sender, RoutedEventArgs e)
    {
        // XAML can raise selection events before all named elements exist.
        if (Summary is null || GameDetails is null) return;
        ErrorMessage.Visibility = Visibility.Collapsed;
        GameDetails.Visibility = GamesOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (GamesOption.IsChecked == true)
        {
            try
            {
                var plan = _data.PlanGameRemoval();
                GameWarning.Text = plan.Count == 0 ? "No hay instalaciones registradas." : "También se borrarán mundos, capturas y mods.";
                GamePaths.Text = string.Join("\n", plan);
                GamePaths.Visibility = plan.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex)
            {
                GamesOption.IsChecked = false;
                ShowError(ex.Message);
            }
        }
        Summary.Text = (ProfilesOption.IsChecked == true, GamesOption.IsChecked == true) switch
        {
            (false, false) => "Tus juegos y perfiles se conservarán.",
            (true, false) => "Se conservarán las instalaciones del juego.",
            (false, true) => "Se conservarán tus perfiles y ajustes.",
            _ => "Se eliminarán el launcher y los datos seleccionados."
        };
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var profiles = ProfilesOption.IsChecked == true;
        var games = GamesOption.IsChecked == true;
        _removing = true;
        ProfilesOption.IsEnabled = GamesOption.IsEnabled = CancelButton.IsEnabled = RemoveButton.IsEnabled = CloseButton.IsEnabled = false;
        RemovalProgress.Visibility = Visibility.Visible;
        RemoveButton.Content = "Desinstalando…";
        ErrorMessage.Visibility = Visibility.Collapsed;
        try
        {
            await Task.Run(() => _data.RemoveSelected(profiles, games));
            _removing = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _removing = false;
            ShowError("No se pudieron eliminar los datos: " + ex.Message);
            ProfilesOption.IsEnabled = GamesOption.IsEnabled = CancelButton.IsEnabled = RemoveButton.IsEnabled = CloseButton.IsEnabled = true;
            RemovalProgress.Visibility = Visibility.Collapsed;
            RemoveButton.Content = "Desinstalar";
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.Visibility = Visibility.Visible;
    }
}
