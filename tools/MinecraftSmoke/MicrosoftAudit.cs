using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using DestinyLauncher;
using DestinyLauncher.Models;
using DestinyLauncher.Services;
using XboxAuthNet.OAuth.CodeFlow;

internal static class MicrosoftAudit
{
    internal static async Task Run(string root)
    {
        Directory.CreateDirectory(root);
        var count = 0;
        void Check(bool valid, string name) { if (!valid) throw new Exception(name); count++; }
        async Task Rejected<T>(Func<Task> action, string name) where T : Exception
        {
            try { await action(); } catch (T) { count++; return; }
            throw new Exception(name);
        }
        var paths = new LauncherPaths(Path.Combine(root, Guid.NewGuid().ToString("N"))); paths.Ensure();
        using var handler = new AuthHandler(); using var http = new HttpClient(handler);
        var service = new MicrosoftAccountService(paths, http); var web = new FixtureWeb();
        var session = await service.SignInAsync(web, null, CancellationToken.None);
        Check(session.UserType == "msa" && session.UUID == AuthHandler.Uuid && session.Username == "Premium_Fixture", "The real SDK returns the Minecraft identity");
        Check(handler.SisuCalls == 1 && handler.SisuClient == "00000000402b5328", "SISU uses the explicit Minecraft client ID");
        Check(handler.RelyingParty == "rp://api.minecraftservices.com/", "Xbox authorization targets Minecraft Services");
        Check(web.Url is not null && MicrosoftWebUi.State(web.Url) is { Length: 64 }, "Interactive OAuth has an unpredictable state");
        Check(handler.ProfileCalls == 1 && handler.OwnershipCalls == 1, "Sign in validates the Java profile and game ownership");
        var encrypted = Path.Combine(paths.Root, "microsoft-accounts.bin");
        Check(File.Exists(encrypted) && !Encoding.UTF8.GetString(File.ReadAllBytes(encrypted)).Contains("fixture-refresh"), "Refresh token is not stored in plaintext");
        var restored = new MicrosoftAccountService(paths, http);
        Check(restored.Profiles().Single() == new MicrosoftProfile(AuthHandler.Uuid, "Premium_Fixture"), "DPAPI accounts restore across service restarts");
        var renewed = await restored.RefreshAsync(AuthHandler.Uuid, null, CancellationToken.None);
        Check(renewed.UUID == session.UUID && handler.RefreshCalls == 1 && handler.SisuCalls == 2, "Play refreshes silently with SISU");
        var config = new LauncherConfig { PlayerName = session.Username!, MicrosoftAccountId = session.UUID! };
        var options = MinecraftService.CreateLaunchOptions(config, renewed);
        Check(options.Session!.UserType == "msa" && options.Session.AccessToken == "fixture-minecraft-access" && options.Session.UUID == AuthHandler.Uuid, "The process builder receives the premium token and real UUID");
        try { MinecraftService.CreateLaunchOptions(config); throw new Exception("Offline fallback accepted"); }
        catch (InvalidOperationException) { count++; }
        var other = new MSession { Username = "Other_Player", UUID = new string('a', 32), AccessToken = "fixture-access", UserType = "msa" };
        try { MinecraftService.CreateLaunchOptions(config, other); throw new Exception("Different account accepted"); }
        catch (InvalidOperationException) { count++; }
        config.Accounts = ["Offline_Fixture"]; config.NormalizeAccounts();
        Check(config.Accounts.SequenceEqual(new[] { "Offline_Fixture" }), "Premium name is not silently added as an offline identity");
        config.MicrosoftAccountId = "";
        var offline = MinecraftService.CreateLaunchOptions(config).Session!;
        Check(offline.AccessToken != "fixture-minecraft-access" && offline.UUID != AuthHandler.Uuid, "Explicit offline selection still creates an offline session");
        restored.Forget(AuthHandler.Uuid);
        Check(new MicrosoftAccountService(paths, http).Profiles().Count == 0, "Local sign out removes the stored identity");

        handler.NoLicense = true;
        var deniedPaths = new LauncherPaths(Path.Combine(root, "no-license-" + Guid.NewGuid().ToString("N"))); deniedPaths.Ensure();
        await Rejected<JEAuthException>(() => new MicrosoftAccountService(deniedPaths, http).SignInAsync(web, null, CancellationToken.None), "Account without Java accepted");
        Check(!File.Exists(Path.Combine(deniedPaths.Root, "microsoft-accounts.bin")), "Failed ownership check saves no account material");
        handler.NoLicense = false; handler.DenyMinecraft = true;
        var denied = new MicrosoftAccountService(deniedPaths, http);
        try { await denied.SignInAsync(web, null, CancellationToken.None); throw new Exception("403 accepted"); }
        catch (JEAuthException ex)
        {
            var message = MicrosoftAccountService.FriendlyError(ex);
            Check(message.Contains("403") && !message.Contains("fixture-secret"), "403 is visible without exposing the SDK response body");
        }
        Check(!File.Exists(Path.Combine(deniedPaths.Root, "microsoft-accounts.bin")), "Minecraft rejection leaves no saved account");
        handler.DenyMinecraft = false;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Rejected<OperationCanceledException>(() => denied.SignInAsync(web, null, cancel.Token), "Canceled sign in continued");
        Check(!File.Exists(Path.Combine(deniedPaths.Root, "microsoft-accounts.bin")), "Cancellation never commits credentials");
        Check(MicrosoftWebUi.IsCallback(new Uri("https://login.live.com/oauth20_desktop.srf?code=fixture"))
            && !MicrosoftWebUi.IsCallback(new Uri("https://login.live.com.evil.invalid/oauth20_desktop.srf?code=fixture"))
            && !MicrosoftWebUi.IsCallback(new Uri("http://login.live.com/oauth20_desktop.srf?code=fixture")), "OAuth callback rejects lookalike hosts and HTTP");
        var deferredPath = Path.Combine(paths.Root, "deferred.bin"); var deferred = new ProtectedMicrosoftStorage(deferredPath);
        deferred.Write(JsonNode.Parse("{\"fixture\":\"token\"}")!, null);
        Check(!File.Exists(deferredPath), "SDK account writes are staged until verification commits");
        Console.WriteLine($"PASS: {count} Microsoft SISU assertions using the real SDK with isolated HTTP fixtures; no real account or game launch.");
    }
    private sealed class FixtureWeb : IWebUI
    {
        internal Uri? Url;
        public Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(Uri uri, ICodeFlowUrlChecker checker, CancellationToken cancel)
        {
            Url = uri; cancel.ThrowIfCancellationRequested();
            return Task.FromResult(new CodeFlowAuthorizationResult { Code = "fixture-auth-code", State = MicrosoftWebUi.State(uri) });
        }
        public Task DisplayDialogAndNavigateUri(Uri uri, CancellationToken cancel) => throw new NotSupportedException();
    }
    private sealed class AuthHandler : HttpMessageHandler
    {
        internal const string Uuid = "1234567890abcdef1234567890abcdef";
        internal int SisuCalls, ProfileCalls, OwnershipCalls, RefreshCalls;
        internal string? SisuClient, RelyingParty;
        internal bool NoLicense, DenyMinecraft;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var uri = request.RequestUri!;
            object body;
            var token = new { Token = "fixture-xbox-token", NotAfter = "2035-01-01T00:00:00Z", IssueInstant = "2026-01-01T00:00:00Z", DisplayClaims = new { xui = new[] { new { uhs = "fixture-user-hash", xid = "123456789" } } } };
            if (uri.Host == "login.live.com")
            {
                var input = await request.Content!.ReadAsStringAsync(cancel);
                if (input.Contains("grant_type=refresh_token")) RefreshCalls++;
                body = new { access_token = "fixture-oauth-access", refresh_token = "M.R3_BAY.fixture-refresh", expires_in = 3600, token_type = "bearer", scope = "service::user.auth.xboxlive.com::MBI_SSL" };
            }
            else if (uri.Host == "device.auth.xboxlive.com" || uri.Host == "xsts.auth.xboxlive.com") body = token;
            else if (uri.Host == "sisu.xboxlive.com")
            {
                using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancel));
                SisuClient = input.RootElement.GetProperty("AppId").GetString(); RelyingParty = input.RootElement.GetProperty("RelyingParty").GetString();
                SisuCalls++; body = new { AuthorizationToken = token, UserToken = token, TitleToken = token };
            }
            else if (uri.AbsolutePath == "/authentication/login_with_xbox")
            {
                if (DenyMinecraft) return new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"Forbidden\",\"errorMessage\":\"fixture-secret\"}") };
                body = new { access_token = "fixture-minecraft-access", expires_in = 3600, token_type = "Bearer" };
            }
            else if (uri.AbsolutePath == "/minecraft/profile") { ProfileCalls++; body = new { id = Uuid, name = "Premium_Fixture", skins = Array.Empty<object>(), capes = Array.Empty<object>() }; }
            else if (uri.AbsolutePath == "/entitlements/mcstore") { OwnershipCalls++; body = new { items = NoLicense ? Array.Empty<object>() : new object[] { new { name = "product_minecraft", signature = "fixture" } } }; }
            else throw new InvalidOperationException("Unexpected SDK endpoint: " + uri.Host + uri.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}
