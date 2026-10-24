using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CmlLib.Core.Auth.Microsoft;
using DestinyLauncher;
using DestinyLauncher.Services;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.OAuth.CodeFlow.Parameters;

internal static partial class Program
{
    private static async Task AuditMicrosoftProfile()
    {
        Assert(SisuExperiment.Enabled, "Microsoft entry is enabled only in the explicit experimental build");
        var window = new MainWindow(previewMode: true, dataRoot: Path.Combine(_output, "data"))
        { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        window.Show(); await Idle();
        var close = window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var dialog = Application.Current.Windows.OfType<ProfileDialog>().Single(w => w.IsVisible);
            var button = Descendants<Button>(dialog).Single(b => Equals(b.Content, "Iniciar sesión con Microsoft"));
            Assert(button.IsEnabled, "The profile card offers Microsoft sign in without enabling multicuentas");
            Assert(Descendants<Button>(dialog).Any(b => Equals(b.Content, "Guardar nombre")), "Existing offline-name editing remains available");
            await Task.Delay(350); Capture(dialog, "microsoft-entry");
            Descendants<Button>(dialog).Single(b => Equals(b.Content, "Cancelar")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }));
        Get<Button>(window, "ProfileButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await close;
        Assert(!File.Exists(Path.Combine(window.DataPaths.Root, "microsoft-accounts.bin")), "Canceling profile choice stores no Microsoft identity");
        var field = typeof(MainWindow).GetField("_config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var config = (DestinyLauncher.Models.LauncherConfig)field.GetValue(window)!;
        config.MicrosoftAccountId = "1234567890abcdef1234567890abcdef";
        window.SetAuditState("", playerName: "Premium_Fixture");
        var accountType = Get<StackPanel>(window, "PlayerAccountType");
        Assert(accountType.Visibility == Visibility.Visible && Descendants<System.Windows.Controls.Image>(accountType).Any(i => ReferenceEquals(i.Source, MicrosoftBrand.Symbol)), "Selected Microsoft identity has the official symbol and visible account-type label");
        var menu = Get<Button>(window, "ProfileButton").ContextMenu;
        Assert(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Cerrar sesión de Microsoft")), "Premium profile exposes local sign out");
        Capture(window, "microsoft-selected");
        window.Close();
    }
    private static async Task AuditMicrosoftWebView()
    {
        var paths = new LauncherPaths(Path.Combine(_output, "data")); paths.Ensure();
        var owner = new Window { Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false,
            Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        owner.Show();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var loaded = false;
        var web = new MicrosoftWebUi(owner, paths);
        web.PageLoaded += () => { loaded = true; cancel.Cancel(); };
        using var http = new System.Net.Http.HttpClient();
        var api = JELoginHandler.DefaultMicrosoftOAuthClientInfo.CreateApiClientForOAuthCode(http);
        var uri = new Uri(api.CreateAuthorizeCodeUrl(new CodeFlowAuthorizationParameter { State = new string('a', 64), Prompt = "select_account" }));
        try { await web.DisplayDialogAndInterceptUri(uri, new ProbeChecker(), cancel.Token); }
        catch (OperationCanceledException) { }
        finally { owner.Close(); }
        Assert(loaded, "The installed WebView2 runtime initializes InPrivate and loads the real Microsoft sign-in page");
        Assert(!File.Exists(Path.Combine(paths.Root, "microsoft-accounts.bin")), "Loading and canceling the Microsoft page creates no saved identity");
    }
    private sealed class ProbeChecker : ICodeFlowUrlChecker
    {
        public CodeFlowAuthorizationResult GetAuthCodeResult(Uri uri) => default;
    }
}
