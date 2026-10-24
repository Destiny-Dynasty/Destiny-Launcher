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
        Get<Button>(window, "InstallationNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
        var closeOptions = window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.IsVisible && w.Owner == window);
            Assert(dialog.Title == "Más opciones" && Descendants<TextBlock>(dialog).Any(t => t.Text.Contains("Inicia sesión con Discord")), "Installation explains Discord linking in a modal");
            Assert(Descendants<Button>(dialog).Any(b => Equals(b.Content, "Vincular Discord")), "More options exposes the existing Discord link flow");
            await Task.Delay(350);
            Capture(dialog, "installation-more-options");
            Descendants<Button>(dialog).Single(b => Equals(b.Content, "Cancelar")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        ((System.Windows.Documents.Hyperlink)window.FindName("MoreInstallationOptions")).RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        await closeOptions;
        void CheckCards(int count, string size)
        {
            var panel = Get<Grid>(window, "EditionGrid");
            var cards = new[] { "DestinyButton", "LiteButton", "StaffButton" }.Select(name => Get<RadioButton>(window, name)).Where(card => card.IsVisible).ToArray();
            Assert(cards.Length == count && cards.All(card => card.ActualHeight >= 90 && card.ActualWidth >= 150), $"{size}: authorized variants form {count} readable illustrated cards");
            // Star columns may differ by one physical pixel when the available
            // width is not divisible by the card count and layout rounding is on.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(panel);
            Assert(cards.All(card => Math.Abs(card.ActualWidth - cards[0].ActualWidth) <= 1 / dpi.DpiScaleX + .01
                && Math.Abs(card.ActualHeight - cards[0].ActualHeight) <= 1 / dpi.DpiScaleY + .01),
                $"{size}: cards retain equal dimensions within one device pixel ({string.Join(", ", cards.Select(card => $"{card.ActualWidth:F2}x{card.ActualHeight:F2}"))})");
            var viewport = Get<Grid>(window, "InstallationContent");
            var bounds = panel.TransformToAncestor(viewport).TransformBounds(new Rect(panel.RenderSize));
            Assert(Math.Abs(bounds.Left + bounds.Width / 2 - viewport.ActualWidth / 2) < 1 && bounds.Left >= -.5 && bounds.Right <= viewport.ActualWidth + .5,
                $"{size}: visible cards fit the viewport and are centered with no reserved Staff gap");
            Assert(cards.All(card => card.Background is System.Windows.Media.ImageBrush { ImageSource: System.Windows.Media.Imaging.BitmapSource { PixelWidth: > 0 } }), $"{size}: each card uses a decoded bundled illustration");
            var details = Get<Border>(window, "InstallationDetails");
            var detailBounds = details.TransformToAncestor(viewport).TransformBounds(new Rect(details.RenderSize));
            Assert(bounds.Top >= -.5 && bounds.Bottom <= detailBounds.Top + .5 && detailBounds.Bottom <= viewport.ActualHeight + .5,
                $"{size}: cards and details fit vertically without overlap or scrolling");
            Assert(cards.All(card => { var b = card.TransformToAncestor(viewport).TransformBounds(new Rect(card.RenderSize)); return b.Top >= -.5 && b.Bottom <= detailBounds.Top + .5; }),
                $"{size}: individual cards remain fully inside their allocated row");
            Assert(!Descendants<ScrollViewer>(Get<Grid>(window, "InstallationView")).Any(), $"{size}: Installation has no scroll container");
        }
        CheckCards(2, "1000x620, no Staff"); Capture(window, "installation-two-cards");
        Get<RadioButton>(window, "LiteButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
        var handler = new DiscordAuditHandler();
        var service = new DiscordLinkService(window.DataPaths, new HttpClient(handler), new Uri("http://127.0.0.1/"));
        await service.LinkAsync(_ => {}, null, CancellationToken.None);
        typeof(MainWindow).GetField("_discord", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, service);
        window.SetAuditState("", playerName: "Destiny_Test", edition: PackEdition.DestinyLite);
        Assert(Get<RadioButton>(window, "StaffButton").Visibility == Visibility.Visible, "Authorized Discord session reveals the Staff selector");
        Get<Button>(window, "InstallationNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(); Capture(window, "staff-installation");
        Assert(Get<RadioButton>(window, "StaffButton").IsVisible, "Staff selector is actually visible inside the Installation page");
        Assert(Get<RadioButton>(window, "LiteButton").IsChecked == true && Get<RadioButton>(window, "StaffButton").IsChecked == false, "Granting Staff preserves the selected modpack");
        CheckCards(3, "1000x620, Staff");
        foreach (var (width, height) in new[] { (820d, 560d), (680d, 480d) })
        {
            window.Width = width; window.Height = height; await Idle(); CheckCards(3, $"{width}x{height}, Staff");
            var action = Get<Button>(window, "InstallPrimaryButton");
            var actionBounds = action.TransformToAncestor(window).TransformBounds(new Rect(action.RenderSize));
            Assert(actionBounds.Left >= 0 && actionBounds.Right <= window.ActualWidth && actionBounds.Bottom <= window.ActualHeight, $"{width}x{height}: install action remains within the window");
            Capture(window, $"staff-installation-{width}-{height}");
        }
        window.Width = 1000; window.Height = 620; await Idle();
        foreach (var (width, height) in new[] { (1000d, 620d), (820d, 560d), (760d, 500d) })
        {
            window.Width = width; window.Height = height;
            window.SetAuditState("", error: "El modpack no está disponible en el servidor. Vuelve a comprobarlo más tarde.", edition: PackEdition.DestinyStaff);
            await Idle(); CheckCards(3, $"{width}x{height}, Staff with error footer");
            var view = Get<Grid>(window, "InstallationView");
            var footer = Get<Border>(window, "StatusFooter");
            var action = Get<Grid>(window, "InstallationActions");
            var actionBounds = action.TransformToAncestor(window).TransformBounds(new Rect(action.RenderSize));
            var footerBounds = footer.TransformToAncestor(window).TransformBounds(new Rect(footer.RenderSize));
            Assert(footer.IsVisible && actionBounds.Bottom <= footerBounds.Top + .5, $"{width}x{height}: actions fit above the visible error footer");
            Capture(window, $"installation-error-{width}-{height}");
        }
        window.Width = 1000; window.Height = 620; await Idle();
        handler.Staff = false; await service.RefreshAsync(CancellationToken.None); window.SetAuditState("", playerName: "Destiny_Test");
        Assert(Get<RadioButton>(window, "StaffButton").Visibility == Visibility.Collapsed, "Removing Discord role hides Staff again");
        CheckCards(2, "Staff revoked"); Capture(window, "installation-after-revoke");
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
