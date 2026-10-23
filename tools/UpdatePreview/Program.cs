using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DestinyLauncher;
using DestinyLauncher.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var boundary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts")) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new Exception("Update demo must stay in workspace artifacts.");
        var target = Path.Combine(root, "program");
        var oldSetup = Path.Combine(root, "old", "DestinyLauncherSetup.exe");
        var newSetup = Path.Combine(root, "new", "DestinyLauncherSetup.exe");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DestinyLauncher;component/Themes/DestinyTheme.xaml", UriKind.Relative) });
        application.Startup += async (_, _) =>
        {
            try
            {
                foreach (var running in Process.GetProcessesByName("DestinyLauncher"))
                {
                    if (running.MainModule?.FileName != Path.Combine(target, "DestinyLauncher.exe")) continue;
                    if (running.MainWindowHandle == IntPtr.Zero || !running.CloseMainWindow()) throw new IOException("Cierra el launcher de prueba para repetir la actualización.");
                    await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                var start = new ProcessStartInfo(oldSetup) { UseShellExecute = false };
                foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=" + target }) start.ArgumentList.Add(arg);
                using (var setup = Process.Start(start)!) { await setup.WaitForExitAsync(); if (setup.ExitCode != 0) throw new IOException("No se pudo preparar la instalación de prueba."); }
                var installation = LauncherInstallation.Read(target);
                if (installation is not { Experimental: true } || installation.DataRoot != Path.Combine(root, "data")) throw new Exception("Unexpected demo installation.");
                var payload = File.ReadAllBytes(newSetup);
                using var key = RSA.Create(2048);
                var version = Version.Parse(FileVersionInfo.GetVersionInfo(newSetup).ProductVersion!).ToString(3);
                var url = $"https://github.com/{LauncherUpdateService.Repository}/releases/download/v{version}/DestinyLauncherSetup.exe";
                var release = new LauncherRelease(1, version, "win-x64", url, payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
                var manifest = JsonSerializer.SerializeToUtf8Bytes(release);
                var signature = key.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                var feed = JsonSerializer.SerializeToUtf8Bytes(new { draft = false, prerelease = false, tag_name = "v" + version, assets = new[] {
                    new { name = "launcher-update.json", browser_download_url = url.Replace("DestinyLauncherSetup.exe", "launcher-update.json") },
                    new { name = "launcher-update.sig", browser_download_url = url.Replace("DestinyLauncherSetup.exe", "launcher-update.sig") } } });
                var responses = new Dictionary<string, byte[]> {
                    ["https://api.github.com/repos/" + LauncherUpdateService.Repository + "/releases/latest"] = feed,
                    [url.Replace("DestinyLauncherSetup.exe", "launcher-update.json")] = manifest,
                    [url.Replace("DestinyLauncherSetup.exe", "launcher-update.sig")] = signature, [url] = payload };
                using var service = new LauncherUpdateService(new LauncherPaths(installation.DataRoot), new HttpClient(new DemoHandler(responses, url)), key.ExportSubjectPublicKeyInfoPem(), new Version(1, 1, 0), Path.Combine(target, "DestinyLauncher.exe"));
                var checkedRelease = await service.CheckAsync(CancellationToken.None) ?? throw new Exception("Demo release was not resolved");
                var owner = new MainWindow(previewMode: true, dataRoot: installation.DataRoot) { Title = "Destiny Launcher · Prueba de actualización" };
                ((TextBlock)owner.FindName("LauncherVersionText")).Text = "v1.1.0";
                owner.Show();
                var dialog = new UpdateDialog(service, checkedRelease, target) { Owner = owner };
                dialog.Loaded += async (_, _) =>
                {
                    await Task.Delay(500);
                    Capture(dialog, Path.Combine(root, "update-available.png"));
                    if (args.Contains("--auto"))
                    {
                        await Task.Delay(2000);
                        var button = Descendants(dialog.Content).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "InstallLauncherUpdate");
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(2200);
                        if (dialog.IsVisible) Capture(dialog, Path.Combine(root, "update-downloading.png"));
                    }
                };
                File.WriteAllText(Path.Combine(root, "preview-ready.txt"), DateTime.UtcNow.ToString("O"));
                dialog.ShowDialog();
                owner.Close();
                File.WriteAllText(Path.Combine(root, "preview-result.json"), JsonSerializer.Serialize(new { installerStarted = dialog.InstallerStarted, version, target }));
                application.Shutdown();
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root, "preview-error.txt"), ex.ToString()); MessageBox.Show(ex.Message, "Prueba de actualización"); application.Shutdown(1); }
        };
        return application.Run();
    }
    private static IEnumerable<DependencyObject> Descendants(object node)
    {
        if (node is not DependencyObject value) yield break;
        yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(value))
            foreach (var nested in Descendants(child)) yield return nested;
    }
    private static void Capture(Window window, string file)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
    }
    private sealed class DemoHandler(Dictionary<string, byte[]> responses, string installer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            if (!responses.TryGetValue(url, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            HttpContent content = url == installer ? new StreamContent(new DemoStream(bytes)) : new ByteArrayContent(bytes);
            content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class DemoStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(10, token); return await base.ReadAsync(buffer, token); }
    }
}
