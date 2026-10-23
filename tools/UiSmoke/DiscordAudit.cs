using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using DestinyLauncher;
using DestinyLauncher.Services;
using DestinyLauncher.Models;

internal static partial class Program
{
    private static async Task AuditDiscord()
    {
        var window = new MainWindow(previewMode: true, dataRoot: Path.Combine(_output, "data"))
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        window.Show(); await Idle();
        Assert(Get<RadioButton>(window, "StaffButton").Visibility == Visibility.Collapsed, "Staff is absent before Discord authorization");
        var handler = new DiscordAuditHandler();
        var service = new DiscordLinkService(window.DataPaths, new HttpClient(handler), new Uri("http://127.0.0.1/"));
        await service.LinkAsync(_ => {}, null, CancellationToken.None);
        typeof(MainWindow).GetField("_discord", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, service);
        window.SetAuditState("", playerName: "Destiny_Test");
        Assert(Get<RadioButton>(window, "StaffButton").Visibility == Visibility.Visible, "Authorized Discord session reveals the Staff selector");
        Get<Button>(window, "InstallationNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(); Capture(window, "staff-installation");
        Assert(Get<RadioButton>(window, "StaffButton").IsVisible, "Staff selector is actually visible inside the Installation page");
        handler.Staff = false; await service.RefreshAsync(CancellationToken.None); window.SetAuditState("", playerName: "Destiny_Test");
        Assert(Get<RadioButton>(window, "StaffButton").Visibility == Visibility.Collapsed, "Removing Discord role hides Staff again");
        var offline = new DiscordLinkService(window.DataPaths, new HttpClient(new DiscordOfflineHandler()));
        var dialog = new DiscordLinkDialog(offline, _ => throw new Exception("Offline flow must not open browser"))
            { Owner = window, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        dialog.Show(); await Task.Delay(100); await Idle();
        Assert(Descendants<TextBlock>((DependencyObject)dialog.Content).Any(b => b.Text.Contains("No se pudo contactar")), "Offline link failure is visible in the dialog");
        Assert(Descendants<Button>((DependencyObject)dialog.Content).Any(b => b.Content?.ToString() == "Reintentar" && b.Visibility == Visibility.Visible), "Offline linking offers Retry and can be canceled");
        Capture(dialog, "discord-offline"); dialog.Close(); window.Close();
    }
    private sealed class DiscordOfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new HttpRequestException("Offline fixture");
    }
    private sealed class DiscordAuditHandler : HttpMessageHandler
    {
        internal bool Staff = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object body = request.RequestUri!.AbsolutePath.EndsWith("/link") ? new { id = new string('a', 64) }
                : new { username = "Destiny Staff", staff = Staff, token = new string('b', 64) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }
    }
}
