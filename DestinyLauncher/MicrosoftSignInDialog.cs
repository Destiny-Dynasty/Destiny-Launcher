using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CmlLib.Core.Auth;
using DestinyLauncher.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XboxAuthNet.OAuth.CodeFlow;

namespace DestinyLauncher;

internal sealed class MicrosoftSignInDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    internal MSession? Session { get; private set; }
    internal MicrosoftSignInDialog(MicrosoftAccountService service, LauncherPaths paths)
    {
        var body = new StackPanel { Margin = new Thickness(24) };
        var status = new TextBlock { Text = "Conectando con Microsoft…", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24), Foreground = ProfileDialog.ResourceBrush("Muted") };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        body.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = ProfileDialog.DialogButton("Cancelar", false); cancel.Click += (_, _) => Close(); actions.Children.Add(cancel);
        var retry = ProfileDialog.DialogButton("Reintentar", true); retry.Margin = new Thickness(10, 0, 0, 0); retry.Visibility = Visibility.Collapsed;
        actions.Children.Add(retry); body.Children.Add(actions);
        DialogChrome.Create(this, "Iniciar sesión con Microsoft", body, 490, 250);
        async Task Link()
        {
            retry.Visibility = Visibility.Collapsed; status.Text = "Conectando con Microsoft…"; status.Foreground = ProfileDialog.ResourceBrush("Muted");
            try
            {
                Session = await service.SignInAsync(new MicrosoftWebUi(this, paths), new Progress<string>(text => status.Text = text), _cancel.Token);
                if (!_cancel.IsCancellationRequested) DialogResult = true;
            }
            catch (Exception error)
            {
                if (_cancel.IsCancellationRequested) return;
                status.Text = MicrosoftAccountService.FriendlyError(error); status.Foreground = ProfileDialog.ResourceBrush("Error");
                retry.Visibility = Visibility.Visible;
            }
        }
        Loaded += async (_, _) => await Link(); retry.Click += async (_, _) => await Link();
        Closed += (_, _) => _cancel.Cancel();
    }
}

// A WebView2 window showing Microsoft's own pages. The application never reads
// passwords, page HTML, form fields, cookies or web messages.
internal sealed class MicrosoftWebUi(Window owner, LauncherPaths paths) : IWebUI
{
    internal event Action? PageLoaded;
    public Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(Uri uri, ICodeFlowUrlChecker checker, CancellationToken cancel)
        => owner.Dispatcher.InvokeAsync(() => ShowAsync(uri, checker, cancel)).Task.Unwrap();
    public Task DisplayDialogAndNavigateUri(Uri uri, CancellationToken cancel)
        => throw new NotSupportedException("Este método de inicio de sesión requiere el navegador integrado.");

    internal static bool IsCallback(Uri uri) => uri.Scheme == "https" && uri.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("/oauth20_desktop.srf", StringComparison.Ordinal);
    internal static string? State(Uri uri)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == "state") return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }
    private async Task<CodeFlowAuthorizationResult> ShowAsync(Uri uri, ICodeFlowUrlChecker checker, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (uri.Scheme != "https" || uri.Host != "login.live.com") throw new InvalidOperationException("Dirección de Microsoft inválida.");
        var state = State(uri);
        if (string.IsNullOrEmpty(state)) throw new InvalidOperationException("Falta el estado de autenticación.");
        var done = new TaskCompletionSource<CodeFlowAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window { Owner = owner, ShowInTaskbar = false };
        var browser = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        var content = new Grid { Margin = new Thickness(1) }; content.Children.Add(browser);
        DialogChrome.Create(window, "Iniciar sesión con Microsoft", content, 580, 700);
        window.Closed += (_, _) => done.TrySetCanceled();
        using var registration = cancel.Register(() => window.Dispatcher.BeginInvoke(new Action(() => { done.TrySetCanceled(cancel); window.Close(); })));
        window.Show();
        try
        {
            var userData = Path.Combine(paths.Cache, "microsoft-webview");
            InstallationDataService.RejectLinks(userData);
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            cancel.ThrowIfCancellationRequested();
            var options = environment.CreateCoreWebView2ControllerOptions(); options.IsInPrivateModeEnabled = true;
            await browser.EnsureCoreWebView2Async(environment, options);
            cancel.ThrowIfCancellationRequested();
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
            browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            browser.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            browser.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess && Uri.TryCreate(browser.CoreWebView2.Source, UriKind.Absolute, out var page)
                    && page.Scheme == "https" && page.Host is "login.live.com" or "login.microsoftonline.com" or "account.live.com")
                    PageLoaded?.Invoke();
            };
            browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var next)) { e.Cancel = true; return; }
                if (IsCallback(next))
                {
                    e.Cancel = true;
                    if (!string.Equals(State(next), state, StringComparison.Ordinal))
                        done.TrySetException(new CryptographicException("Estado de autenticación inválido."));
                    else
                    {
                        var result = checker.GetAuthCodeResult(next);
                        if (!result.IsEmpty) done.TrySetResult(result);
                    }
                }
                else if (next.Scheme != "https") e.Cancel = true;
            };
            browser.CoreWebView2.Navigate(uri.AbsoluteUri);
            return await done.Task.WaitAsync(cancel);
        }
        finally { browser.Dispose(); window.Close(); }
    }
}
