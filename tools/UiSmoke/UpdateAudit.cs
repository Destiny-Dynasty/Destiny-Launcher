using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using DestinyLauncher;
using DestinyLauncher.Services;

internal static partial class Program
{
    private static async Task AuditUpdateWindow()
    {
        var paths = new LauncherPaths(Path.Combine(_output, "data")); paths.Ensure();
        var handler = new UpdateAuditHandler();
        using var service = new LauncherUpdateService(paths, new HttpClient(handler), LauncherUpdateService.PublicKey(), new Version(1, 2, 0), "missing-fixture.exe");
        var release = new LauncherRelease(1, "1.2.1", "win-x64", "https://github.com/Destiny-Dynasty/Destiny-Launcher/releases/download/v1.2.1/DestinyLauncherSetup.exe", 1048576, new string('0', 64),
            ["Nueva ventana de actualización con el estilo de Destiny Dynasty.", "Novedades de cada versión y progreso de descarga más claro.", "El launcher permanece oculto mientras se actualiza."]);
        UpdateDialog Create() => new(service, release, Path.Combine(_output, "program"))
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        var dialog = Create(); dialog.Show(); await Idle();
        Assert(Get<Button>(dialog, "CancelButton").Content.ToString() == "Cancelar", "Update always offers Cancel instead of Later");
        Assert(Get<ItemsControl>(dialog, "ChangelogItems").Items.Count == 3, "Signed release notes appear as concise changelog entries");
        Assert(Get<TextBlock>(dialog, "ReleaseVersion").Text.Contains("1.2.1") && Get<TextBlock>(dialog, "CurrentVersion").Text == "v1.2.0", "Current and incoming versions are distinguished");
        Assert(dialog.ShowInTaskbar, "Standalone updater is reachable from the taskbar");
        Capture(dialog, "update-ready");
        Capture(dialog, "update-ready-125", 1.25);
        Capture(dialog, "update-ready-200", 2);
        foreach (var (width, height) in new[] { (760d, 490d), (680d, 400d), (600d, 360d) })
        {
            dialog.Width = width; dialog.Height = height; await Idle();
            foreach (var control in new[] { "UpdateButton", "CancelButton", "CloseButton" })
            {
                var button = Get<Button>(dialog, control);
                var bounds = button.TransformToAncestor(dialog).TransformBounds(new Rect(button.RenderSize));
                Assert(bounds.Left >= 0 && bounds.Right <= dialog.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= dialog.ActualHeight,
                    $"{control} fits at {width}x{height}");
            }
            Capture(dialog, $"update-{width}-{height}");
        }
        dialog.Width = 760; dialog.Height = 490;
        Get<Button>(dialog, "UpdateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Idle();
        Assert(Get<Button>(dialog, "UpdateButton").IsEnabled && Get<Button>(dialog, "UpdateButton").Content.ToString() == "Reintentar", "Network failure exposes a working retry action");
        Assert(Get<TextBlock>(dialog, "Status").Text.Contains("No se pudo descargar"), "Update errors are visible in the standalone window");
        Capture(dialog, "update-error");
        handler.Fail = false;
        Get<Button>(dialog, "UpdateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Get<ProgressBar>(dialog, "DownloadProgress").Value == 0 && DateTime.UtcNow < deadline) await Idle();
        Assert(Get<ProgressBar>(dialog, "DownloadProgress").Value > 0 && !Get<Button>(dialog, "UpdateButton").IsEnabled, "Download uses actual byte progress and prevents duplicate requests");
        Capture(dialog, "update-downloading");
        Get<Button>(dialog, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        while (dialog.IsVisible && DateTime.UtcNow < deadline) await Idle();
        Assert(!dialog.IsVisible && !dialog.InstallerStarted, "Cancel waits for cleanup and never starts an installer");
        Assert(!Directory.GetFiles(paths.Root, "*.part", SearchOption.AllDirectories).Any(), "Cancel removes partial update downloads before closing");
        var owner = new Window { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        owner.Show();
        var standalone = Create();
        bool hidden = false;
        standalone.Loaded += (_, _) =>
        {
            hidden = !owner.IsVisible;
            standalone.Dispatcher.BeginInvoke(() => Get<Button>(standalone, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        };
        standalone.ShowStandalone(owner);
        Assert(hidden && !owner.IsVisible, "Standalone update hides the launcher without bringing it back on Cancel");
        Assert(!standalone.InstallerStarted, "Cancel before downloading leaves the installation unchanged");
        owner.Close();
        var legacy = new UpdateDialog(service, release with { Changelog = null }, Path.Combine(_output, "program"));
        Assert(Get<StackPanel>(legacy, "ChangelogSection").Visibility == Visibility.Collapsed, "Older releases without notes do not show an empty changelog section");
        legacy.Close();
    }

    private sealed class UpdateAuditHandler : HttpMessageHandler
    {
        internal bool Fail = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Fail) throw new HttpRequestException("Controlled update test failure");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UpdateAuditStream()) });
        }
    }
    private sealed class UpdateAuditStream() : MemoryStream(new byte[1048576], writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(200, token); return await base.ReadAsync(buffer, token); }
    }
}
