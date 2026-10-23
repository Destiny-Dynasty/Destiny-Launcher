using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DestinyLauncher.Services;

public sealed record DiscordAccess(string Username, bool Staff);

public sealed class DiscordLinkService
{
    // Test-server HTTP is intentional. Production only needs a new HTTPS endpoint;
    // no Discord secret or bot token is ever distributed with the application.
    public const string DefaultEndpoint = "http://66.45.252.226:24431/";
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _credentialFile;
    private sealed record Credential(string Endpoint, string Token);
    public DiscordAccess? Access { get; private set; }
    public bool IsLinked => File.Exists(_credentialFile);
    public DiscordLinkService(LauncherPaths paths, HttpClient? http = null, Uri? endpoint = null)
    {
        _credentialFile = Path.Combine(paths.Root, "discord-session.bin");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 16384 };
        _endpoint = endpoint ?? new Uri(DefaultEndpoint);
    }
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, new Uri(_endpoint, "launcher/" + path));
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        // Buffer small replies under the timeout as well as the response headers.
        var response = await _http.SendAsync(request, timeout.Token);
        if (response.Content.Headers.ContentLength > 16384) { response.Dispose(); throw new InvalidDataException("Respuesta de vinculación demasiado grande."); }
        return response;
    }
    private static async Task<JsonElement> Read(HttpResponseMessage response, CancellationToken cancel)
    {
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
        if (bytes.Length > 16384) throw new InvalidDataException("Respuesta de vinculación demasiado grande.");
        using var json = JsonDocument.Parse(bytes); return json.RootElement.Clone();
    }
    private string? Load()
    {
        if (!IsLinked) return null;
        try
        {
            InstallationDataService.RejectLinks(_credentialFile);
            if (new FileInfo(_credentialFile).Length > 16384) throw new InvalidDataException();
            var data = JsonSerializer.Deserialize<Credential>(ProtectedData.Unprotect(File.ReadAllBytes(_credentialFile), null, DataProtectionScope.CurrentUser));
            return data?.Endpoint == _endpoint.AbsoluteUri ? data.Token : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException) { return null; }
    }
    private void Save(string token)
    {
        InstallationDataService.RejectLinks(_credentialFile);
        Directory.CreateDirectory(Path.GetDirectoryName(_credentialFile)!);
        var temporary = _credentialFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Credential(_endpoint.AbsoluteUri, token));
            File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, _credentialFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static DiscordAccess Parse(JsonElement data) => new(data.GetProperty("username").GetString() ?? "Discord", data.GetProperty("staff").GetBoolean());
    public async Task<DiscordAccess?> RefreshAsync(CancellationToken cancel)
    {
        Access = null; var token = Load(); if (token is null) return null;
        using var response = await Send(HttpMethod.Get, "session", null, token, cancel);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { Forget(); return null; }
        return Access = Parse(await Read(response, cancel));
    }
    public async Task<DiscordAccess> LinkAsync(Action<Uri> openBrowser, IProgress<string>? progress, CancellationToken cancel)
    {
        using var health = await Send(HttpMethod.Get, "health", null, null, cancel);
        await Read(health, cancel);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var challenge = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        using var begin = await Send(HttpMethod.Post, "link", new { challenge }, null, cancel);
        var pairing = await Read(begin, cancel);
        var id = pairing.GetProperty("id").GetString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "^[a-f0-9]{64}$")) throw new InvalidDataException("Vinculación inválida.");
        // Never accept an arbitrary external URL from the server response.
        openBrowser(new Uri(_endpoint, "launcher/authorize?id=" + id));
        progress?.Report("Esperando autorización de Discord…");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                using var poll = await Send(HttpMethod.Post, "poll", new { id }, secret, deadline.Token);
                if (poll.StatusCode == HttpStatusCode.Accepted) continue;
                if (poll.StatusCode == HttpStatusCode.Gone) throw new InvalidOperationException("La vinculación ha caducado. Inténtalo de nuevo.");
                var result = await Read(poll, deadline.Token);
                var token = result.GetProperty("token").GetString()!;
                if (!System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-f0-9]{64}$")) throw new InvalidDataException("Sesión inválida.");
                var access = Parse(result); cancel.ThrowIfCancellationRequested(); Save(token); return Access = access;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancel.IsCancellationRequested) { throw new TimeoutException("La vinculación ha caducado. Inténtalo de nuevo."); }
    }
    public async Task UnlinkAsync(CancellationToken cancel)
    {
        var token = Load();
        if (token is not null) { using var response = await Send(HttpMethod.Delete, "session", null, token, cancel); if (response.StatusCode != HttpStatusCode.Unauthorized) response.EnsureSuccessStatusCode(); }
        Forget();
    }
    private void Forget() { InstallationDataService.RejectLinks(_credentialFile); if (File.Exists(_credentialFile)) File.Delete(_credentialFile); Access = null; }
    public static string FriendlyError(Exception error) => error is HttpRequestException or TaskCanceledException
        ? "No se pudo contactar con el servidor. Inténtalo de nuevo." : error.Message;
}
