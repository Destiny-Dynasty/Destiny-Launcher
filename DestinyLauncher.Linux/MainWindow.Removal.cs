using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace DestinyLauncher.Linux;

public sealed partial class MainWindow
{
    private async void ManageInstallations_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _checkingCatalog) return;
        try { _launcher.RegisterExistingInstallations(); }
        catch (Exception ex) { await InformAsync("No se pudieron comprobar las instalaciones", ex.Message); return; }
        var body = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };
        body.Children.Add(new TextBlock { Text = "Elige un modpack para desinstalarlo. Cada edición se gestiona por separado.", Foreground = Brush("#B3AC9E"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var dialog = CreateDialog("Gestionar instalaciones", body, 600, 395);
        foreach (var (slug, name) in new[] { ("destiny", "Destiny"), ("destiny-lite", "Destiny Lite"), ("destiny-staff", "Destiny Staff") })
        {
            var path = _launcher.Paths.Instance(slug);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 14 });
            var detail = new TextBlock { Text = Directory.Exists(path) ? path : "Sin instalar", Foreground = Brush("#B3AC9E"), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 10, Margin = new Thickness(0, 4, 8, 0) };
            ToolTip.SetTip(detail, path); text.Children.Add(detail); row.Children.Add(text);
            var remove = DialogButton("Desinstalar"); remove.IsEnabled = Directory.Exists(path); Grid.SetColumn(remove, 1); row.Children.Add(remove);
            remove.Click += async (_, _) =>
            {
                var details = slug == "destiny"
                    ? $"Se quitarán los mods de Destiny y su configuración de perfil. Los mundos, capturas y datos compartidos permanecerán en:\n\n{_launcher.Paths.GameDirectory}"
                    : $"Se quitarán los mods, datos de instalación y configuraciones de {name}. Los mundos y datos compartidos permanecerán en:\n\n{_launcher.Paths.GameDirectory}";
                if (!await ConfirmAsync($"Desinstalar {name}", details, "Desinstalar", "Cancelar", dialog)) return;
                dialog.IsEnabled = false; remove.Content = "Eliminando…";
                try { await Task.Run(() => _launcher.RemoveInstance(slug)); remove.IsEnabled = false; detail.Text = "Sin instalar"; UpdateVersions(); }
                catch (Exception ex) { dialog.IsEnabled = true; await InformAsync("No se pudo desinstalar", ex.Message, dialog); }
                finally { remove.Content = "Desinstalar"; dialog.IsEnabled = true; }
            };
            body.Children.Add(row);
        }
        var uninstall = DialogButton("Desinstalar launcher"); uninstall.HorizontalAlignment = HorizontalAlignment.Left; uninstall.Margin = new Thickness(0, 20, 0, 0);
        var requested = false;
        uninstall.Click += (_, _) => { requested = true; dialog.Close(); };
        body.Children.Add(uninstall);
        await ShowOwnedDialogAsync(dialog);
        if (!requested) return;
        if (!OperatingSystem.IsLinux() || !AppContext.BaseDirectory.TrimEnd('/').Equals("/opt/destiny-launcher", StringComparison.Ordinal))
        { await InformAsync("Desinstalar launcher", "Esta opción está disponible cuando el launcher se instala con el paquete Debian."); return; }
        if (!await ConfirmAsync("Desinstalar launcher", "Se eliminará Destiny Launcher. Tus perfiles, ajustes y modpacks se conservarán. El sistema solicitará tu contraseña.", "Desinstalar", "Cancelar")) return;
        _removingLauncher = true;
        SetBusy(true, "Desinstalando launcher…");
        var packageRemoved = false;
        try
        {
            var info = new ProcessStartInfo("pkexec") { UseShellExecute = false };
            foreach (var arg in new[] { "/usr/bin/dpkg", "--remove", "destiny-launcher" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("No se pudo iniciar el gestor de paquetes.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("No se completó la desinstalación o se canceló la autorización.");
            packageRemoved = true;
            await InformAsync("Hasta pronto", "Destiny Launcher se ha desinstalado. Gracias por formar parte de nuestra aventura. Tus perfiles y modpacks se han conservado.");
            _removingLauncher = false;
            Close();
        }
        catch (Exception ex)
        {
            _removingLauncher = false;
            if (packageRemoved) { Close(); return; }
            SetBusy(false); await InformAsync("No se pudo desinstalar", ex.Message);
        }
    }
}
