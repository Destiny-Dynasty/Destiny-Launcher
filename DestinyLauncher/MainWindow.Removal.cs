using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DestinyLauncher.Services;

namespace DestinyLauncher;

public partial class MainWindow
{
    private void ManageInstallations_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _checkingManifest) return;
        try { _installationData.RegisterConfigured(_config.InstallDirectory); }
        catch (Exception ex) { LauncherDialog.Inform(this, "No se pudieron comprobar las instalaciones", ex.Message); return; }
        var dialog = new Window { Owner = this };
        var body = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };
        body.Children.Add(new TextBlock { Text = "Elige un modpack para desinstalarlo. Cada edición se gestiona por separado.", Foreground = ProfileDialog.ResourceBrush("Muted"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        foreach (var (slug, name) in new[] { ("destiny", "Destiny"), ("destiny-lite", "Destiny Lite"), ("destiny-staff", "Destiny Staff") })
        {
            var path = _paths.Instance(slug);
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            text.Children.Add(new TextBlock { Text = Directory.Exists(path) ? path : "Sin instalar", Foreground = ProfileDialog.ResourceBrush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 10, Margin = new Thickness(0, 4, 8, 0), ToolTip = path });
            row.Children.Add(text);
            var remove = ProfileDialog.DialogButton("Desinstalar", false); remove.IsEnabled = Directory.Exists(path);
            Grid.SetColumn(remove, 1); row.Children.Add(remove);
            remove.Click += async (_, _) =>
            {
                var details = slug == "destiny"
                    ? $"Se quitarán los mods de Destiny y su configuración de perfil. Los mundos, capturas y datos compartidos permanecerán en:\n\n{_paths.GameDirectory}"
                    : $"Se quitarán los mods, datos de instalación y configuraciones de {name}. Los mundos y datos compartidos permanecerán en:\n\n{_paths.GameDirectory}";
                if (!LauncherDialog.Confirm(dialog, $"Desinstalar {name}", details, "Desinstalar", "Cancelar")) return;
                dialog.IsEnabled = false; remove.Content = "Eliminando…";
                try { await Task.Run(() => _installationData.RemoveInstance(path)); remove.IsEnabled = false; ((TextBlock)text.Children[1]).Text = "Sin instalar"; _editionChecks.Clear(); _manifestCheck = null; Refresh(); }
                catch (Exception ex) { dialog.IsEnabled = true; LauncherDialog.Inform(dialog, "No se pudo desinstalar", ex.Message); }
                finally { remove.Content = "Desinstalar"; dialog.IsEnabled = true; }
            };
            body.Children.Add(row);
        }
        var launcher = ProfileDialog.DialogButton("Desinstalar launcher", false);
        launcher.HorizontalAlignment = HorizontalAlignment.Left; launcher.Margin = new Thickness(0, 20, 0, 0);
        var uninstallLauncher = false;
        launcher.Click += (_, _) => { uninstallLauncher = true; dialog.Close(); };
        body.Children.Add(launcher);
        DialogChrome.Create(dialog, "Gestionar instalaciones", body, 600, 395);
        dialog.ShowDialog();
        if (!uninstallLauncher) return;
        if (_previewMode) { LauncherDialog.Inform(this, "Vista de prueba", "La desinstalación no está disponible en la vista de prueba."); return; }
        try
        {
            _ = FindInstalledUninstaller();
            var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No se encontró el ejecutable del launcher.")) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true };
            info.ArgumentList.Add("--uninstall-handoff"); info.ArgumentList.Add(Environment.ProcessId.ToString());
            using var handoff = Process.Start(info) ?? throw new InvalidOperationException("No se pudo iniciar el desinstalador.");
            Close();
        }
        catch (Exception ex) { LauncherDialog.Inform(this, "No se pudo abrir el desinstalador", ex.Message); }
    }

    internal static string FindInstalledUninstaller()
    {
        if (LauncherInstallation.Read(AppContext.BaseDirectory) is null) throw new InvalidOperationException("Esta copia no está instalada mediante el instalador de Windows.");
        var candidates = Directory.GetFiles(AppContext.BaseDirectory, "unins*.exe").Where(p => Regex.IsMatch(Path.GetFileName(p), @"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new InvalidOperationException("No se pudo identificar el desinstalador. Usa Aplicaciones instaladas de Windows.");
    }
}
