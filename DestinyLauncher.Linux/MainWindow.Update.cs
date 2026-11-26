using System.Diagnostics;
using System.Runtime.InteropServices;
using DestinyLauncher.Services;

namespace DestinyLauncher.Linux;

public sealed partial class MainWindow
{
    private async Task CheckLauncherUpdateAsync()
    {
        if (App.IsLocalTestBuild) return;
        if (!OperatingSystem.IsLinux() || AppContext.BaseDirectory.TrimEnd('/') != "/opt/destiny-launcher") return;
        var platform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => null
        };
        if (platform is null) return;
        try
        {
            using var updater = new LauncherUpdateService(_launcher.Paths, new HttpClient(), LauncherUpdateService.PublicKey(),
                typeof(MainWindow).Assembly.GetName().Version!, Environment.ProcessPath!, platform);
            var release = await updater.CheckAsync(_uiLifetime.Token);
            if (release is null || _uiLifetime.IsCancellationRequested) return;
            // Avoid interrupting a game, settings edit or another dialog. The next launch retries.
            if (_isBusy || _activeDialog is not null || _page == "settings") return;
            if (!await ConfirmAsync("Actualización disponible", $"Hay una nueva versión de Destiny Launcher ({release.Version}). Se descargará el paquete verificado y se abrirá el instalador del sistema.", "Descargar", "Ahora no")) return;
            SetBusy(true, "Descargando actualización del launcher…");
            try
            {
                var path = await updater.DownloadAsync(release, new Progress<double>(), _uiLifetime.Token);
                var info = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                info.ArgumentList.Add(path);
                using var process = Process.Start(info) ?? throw new InvalidOperationException("No se pudo abrir el instalador del sistema.");
                await process.WaitForExitAsync(_uiLifetime.Token);
                if (process.ExitCode != 0) throw new InvalidOperationException("El sistema no pudo abrir el paquete. Puedes encontrarlo en " + path);
                SetStatus("Actualización descargada", "Termina la instalación en el gestor de aplicaciones y vuelve a abrir el launcher.");
            }
            finally { SetBusy(false); }
        }
        catch (OperationCanceledException) when (_uiLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_uiLifetime.IsCancellationRequested)
                SetStatus("No se pudo comprobar o descargar la actualización", ex.Message, true);
        }
    }
}
