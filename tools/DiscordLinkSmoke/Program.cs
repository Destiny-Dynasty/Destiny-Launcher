using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DestinyLauncher.Services;

var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/discord-link/client");
root = Path.Combine(root, Guid.NewGuid().ToString("N"));
var paths = new LauncherPaths(root); paths.Ensure();
var handler = new Fixture(); var http = new HttpClient(handler);
var service = new DiscordLinkService(paths, http, new Uri("http://127.0.0.1:24431/"));
var checks = new List<string>();
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
Check(await service.RefreshAsync(CancellationToken.None) is null && handler.Calls == 0, "Unlinked users make no authentication requests");
Uri? browser = null;
var access = await service.LinkAsync(url => browser = url, null, CancellationToken.None);
Check(browser?.AbsoluteUri == "http://127.0.0.1:24431/launcher/authorize?id=" + new string('b', 64), "Browser authorization always uses configured server origin");
Check(access.Staff && access.Username == "Destiny Test", "Staff session is received after proof-bound polling");
var credential = Path.Combine(root, "discord-session.bin");
Check(service.IsLinked && !Encoding.UTF8.GetString(File.ReadAllBytes(credential)).Contains(new string('c', 64)), "Saved credential is protected with Windows DPAPI, separate from launcher.json");
var migrated = new DiscordLinkService(paths, http, new Uri("https://destiny-dynasty-bot.tatnikhost.net/"));
var callsBeforeMigration = handler.Calls;
Check(!migrated.IsLinked && await migrated.RefreshAsync(CancellationToken.None) is null && handler.Calls == callsBeforeMigration,
    "Moving to the HTTPS origin offers a fresh link and never sends an old-origin credential to the new server");
var restored = new DiscordLinkService(paths, http, new Uri("http://127.0.0.1:24431/"));
Check((await restored.RefreshAsync(CancellationToken.None))?.Staff == true, "A fresh client restores and revalidates its saved session");
handler.Staff = false;
Check((await restored.RefreshAsync(CancellationToken.None))?.Staff == false, "Revoked roles remove Staff access");
handler.Staff = true; await restored.RefreshAsync(CancellationToken.None); handler.Offline = true;
try { await restored.RefreshAsync(CancellationToken.None); throw new Exception("Expected offline error"); }
catch (HttpRequestException ex) { Check(restored.Access is null && DiscordLinkService.FriendlyError(ex).Contains("No se pudo contactar"), "Server outage removes stale authorization and produces a visible connection error"); }
handler.Offline = false; handler.Unauthorized = true;
Check(await restored.RefreshAsync(CancellationToken.None) is null && !restored.IsLinked, "Expired/revoked server sessions discard local credentials");
handler.Unauthorized = false;
await service.LinkAsync(_ => {}, null, CancellationToken.None); await service.UnlinkAsync(CancellationToken.None);
Check(!service.IsLinked && service.Access is null && handler.Revoked, "Unlink revokes the server session and removes the protected credential");
using var cancel = new CancellationTokenSource(); cancel.Cancel();
try { await service.LinkAsync(_ => throw new Exception("Canceled flow opened browser"), null, cancel.Token); throw new Exception("Expected cancellation"); }
catch (OperationCanceledException) { Check(!service.IsLinked, "Cancellation leaves no local credential"); }
File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks.Count} Discord launcher assertions; {root}");

sealed class Fixture : HttpMessageHandler
{
    public int Calls; public bool Offline, Unauthorized, Revoked, Staff = true; private string? _challenge;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Calls++;
        if (Offline) throw new HttpRequestException("Offline fixture");
        HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("/health")) return Json(new { ready = true });
        if (route.EndsWith("/link")) { using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); _challenge = data.RootElement.GetProperty("challenge").GetString(); return Json(new { id = new string('b', 64) }); }
        if (route.EndsWith("/poll"))
        {
            var proof = request.Headers.Authorization!.Parameter!;
            if (_challenge != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof))).ToLowerInvariant()) throw new Exception("Polling proof does not match challenge");
            return Json(new { token = new string('c', 64), username = "Destiny Test", staff = Staff });
        }
        if (request.Headers.Authorization?.Parameter != new string('c', 64)) throw new Exception("Session token not sent in Authorization header");
        if (Unauthorized) return new(HttpStatusCode.Unauthorized);
        if (request.Method == HttpMethod.Delete) { Revoked = true; return Json(new { ok = true }); }
        return Json(new { username = "Destiny Test", staff = Staff });
    }
}
