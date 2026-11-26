using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using XboxAuthNet.OAuth.CodeFlow;

namespace DestinyLauncher.Linux;

/// <summary>Uses the user's default browser and a short-lived loopback callback for Microsoft OAuth.</summary>
internal sealed class LinuxSystemBrowserWebUI : IWebUI
{
    private readonly int _port;
    public string RedirectUri => $"http://127.0.0.1:{_port}";

    public LinuxSystemBrowserWebUI()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public async Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(Uri authorizationUri,
        ICodeFlowUrlChecker uriChecker, CancellationToken cancellationToken)
    {
        var expectedState = QueryValue(authorizationUri, "state");
        if (string.IsNullOrEmpty(expectedState)) throw new InvalidDataException("Microsoft no incluyó el estado de autenticación esperado.");

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        listener.Start();
        OpenBrowser(authorizationUri);
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            var callback = context.Request.Url;
            var remote = context.Request.RemoteEndPoint?.Address;
            if (callback is null || remote is null || !IPAddress.IsLoopback(remote)
                || callback.Scheme != Uri.UriSchemeHttp
                || !callback.IsLoopback
                || callback.Port != _port
                || callback.AbsolutePath != "/")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }

            var result = uriChecker.GetAuthCodeResult(callback);
            if (!string.Equals(result.State, expectedState, StringComparison.Ordinal))
            {
                Respond(context.Response, false);
                throw new InvalidDataException("La respuesta de Microsoft no coincide con la solicitud de inicio de sesión.");
            }
            Respond(context.Response, result.IsSuccess);
            return result;
        }
    }

    public Task DisplayDialogAndNavigateUri(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenBrowser(uri);
        return Task.CompletedTask;
    }

    private static void OpenBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("No se pudo abrir el navegador predeterminado de Linux.");
        }
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            if (string.Equals(key, name, StringComparison.Ordinal))
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
        }
        return null;
    }

    private static void Respond(HttpListenerResponse response, bool success)
    {
        const string ok = "<html><meta charset=\"utf-8\"><title>Destiny Launcher</title><body style=\"font:16px sans-serif;background:#10110f;color:#f5f1e6;padding:48px\"><h2>Inicio de sesión completado</h2><p>Ya puedes cerrar esta pestaña y volver a Destiny Launcher.</p></body></html>";
        const string fail = "<html><meta charset=\"utf-8\"><title>Destiny Launcher</title><body style=\"font:16px sans-serif;background:#10110f;color:#f5f1e6;padding:48px\"><h2>No se completó el inicio de sesión</h2><p>Vuelve a Destiny Launcher para reintentarlo.</p></body></html>";
        var body = System.Text.Encoding.UTF8.GetBytes(success ? ok : fail);
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body, 0, body.Length);
        response.Close();
    }
}
