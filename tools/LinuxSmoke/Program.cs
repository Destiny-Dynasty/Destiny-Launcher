using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DestinyLauncher.Linux;
using DestinyLauncher.Models;
using DestinyLauncher.Services;
using SkiaSharp;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private static string _output = "";
    [STAThread]
    private static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/linux-smoke");
        if (OperatingSystem.IsLinux())
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(LibVLCSharp.Shared.Core).Assembly,
                (name, assembly, searchPath) => name == "libvlc" && System.Runtime.InteropServices.NativeLibrary.TryLoad("libvlc.so.5", assembly, searchPath, out var handle) ? handle : IntPtr.Zero);
        Directory.CreateDirectory(_output);
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        if (args.Contains("--video-only")) return VideoAudit.Run(_output);
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            AuditDiscord().GetAwaiter().GetResult();
            if (args.Contains("--discord-only"))
            {
                File.WriteAllText(Path.Combine(_output, "results.json"), JsonSerializer.Serialize(Passed, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS: {Passed.Count} Linux Discord checks using fake HTTP and in-memory credentials.");
                return 0;
            }
            AuditPortraits().GetAwaiter().GetResult();
            AuditNews().GetAwaiter().GetResult();
            AuditStaffGate();
            AuditVideoPlaybackPolicy();
            AuditIndividualRemoval();
            AuditLauncherUpdates().GetAwaiter().GetResult();
            AuditLinuxFloatingMod().GetAwaiter().GetResult();
            var defaults = new ConfigService(Paths("autojoin-default"));
            Assert(!defaults.Load().AutoJoinServer, "New launcher settings leave automatic server join disabled");
            Assert(JsonSerializer.Deserialize<LauncherConfig>("{}")?.AutoJoinServer == false, "Settings without a stored automatic join field default to disabled");
            var optedIn = defaults.Load(); optedIn.AutoJoinServer = true; defaults.Save(optedIn);
            Assert(defaults.Load().AutoJoinServer, "Existing explicit automatic server join preference is preserved");
            Assert(LauncherPresentation.ProgressStatus("Descargando modpack · 10485760/20971520 bytes") == "Descargando modpack · 10 MB de 20 MB", "Download status formats byte counts as MB");
            Assert(LauncherPresentation.Progress("Descargando modpack · 10485760/20971520 bytes", .4) == .5, "Download bar follows the byte ratio");
            File.WriteAllText(Path.Combine(_output, "results.json"), JsonSerializer.Serialize(Passed, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {Passed.Count} Linux checks using fake HTTP and in-memory credentials; no sign-in or game launch.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task AuditLinuxFloatingMod()
    {
        var version = 1;
        var packVersion = "2026.0.1-alpha";
        var pinnedVersion = 1;
        var offline = false;
        var root = Paths("linux-floating").Root;
        using var http = new HttpClient(new FakeHttp(request =>
        {
            var uri = request.RequestUri!;
            var fallback = Encoding.UTF8.GetBytes("fallback-old");
            var latest = Encoding.UTF8.GetBytes("current-" + version);
            var pinned = Encoding.UTF8.GetBytes("fixed-mod-" + pinnedVersion);
            string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
            const string baseUrl = "https://github.com/Destiny-Dynasty/Mod-Releases/releases/download/";
            var assetUrl = baseUrl + $"mod-destiny-dynasty-{version}/destiny-dynasty-{version}.jar";
            if (uri.Host == "raw.githubusercontent.com" && uri.AbsolutePath.EndsWith("/channels/destiny-dynasty.json"))
            {
                if (offline) throw new HttpRequestException("Offline test");
                return Task.FromResult(Json(new { draft = false, prerelease = false, tag_name = $"mod-destiny-dynasty-{version}", assets = new[] { new { name = $"destiny-dynasty-{version}.jar", size = latest.Length, digest = "sha256:" + Hash(latest), browser_download_url = assetUrl } } }));
            }
            if (uri.Host == "raw.githubusercontent.com")
                return Task.FromResult(Json(new { schemaVersion = 2, packId = "destiny", version = packVersion, minecraftVersion = "1.20.1", fabricLoaderVersion = "0.19.5", javaVersion = 21, files = new object[] { new { path = "mods/destiny-dynasty.jar", url = baseUrl + "old/destiny-dynasty-old.jar", size = fallback.Length, sha256 = Hash(fallback), github = new { repository = "Destiny-Dynasty/Mod-Releases", release = "latest", channel = "destiny-dynasty", asset = "destiny-dynasty-*.jar" } }, new { path = "mods/fixed.jar", url = "https://example.invalid/fixed.jar", size = pinned.Length, sha256 = Hash(pinned) } } }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(uri.AbsoluteUri == assetUrl ? latest : uri.Host == "example.invalid" ? pinned : fallback) });
        }));
        using var launcher = new LinuxLauncherService(root, http);
        offline = true;
        await Throws<HttpRequestException>(() => launcher.InstallOrUpdatePackAsync(PackEdition.Destiny), "A new installation does not silently install an outdated floating mod when its channel is unavailable");
        offline = false;
        var directPlan = await new GitHubPackUpdater(launcher.Paths, new AppLogger(launcher.Paths), http).CheckAsync(new PackDefinition(), null, CancellationToken.None);
        Assert(directPlan.Manifest.Files.Single(f => f.Path == "mods/destiny-dynasty.jar").Url.EndsWith("destiny-dynasty-1.jar"), "The general check path resolves latest channels too");
        await launcher.InstallOrUpdatePackAsync(PackEdition.Destiny);
        var mod = Path.Combine(launcher.Paths.Instance("destiny"), "mods", "destiny-dynasty.jar");
        Assert(File.ReadAllText(mod) == "current-1", "Linux initial installation resolves the current channel instead of the older manifest fallback");
        Assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(mod)!, "fixed.jar")) == "fixed-mod-1", "Linux installs pinned mods along with latest channel mods");
        version = 2;
        await launcher.InstallOrUpdatePackAsync(PackEdition.Destiny);
        Assert(File.ReadAllText(mod) == "current-2", "Linux updates a floating mod even when the pack version stays unchanged");
        offline = true;
        await launcher.InstallOrUpdatePackAsync(PackEdition.Destiny);
        Assert(File.ReadAllText(mod) == "current-2", "An unavailable channel preserves the previously installed mod version");
        offline = false;
        packVersion = "2026.0.2-alpha"; pinnedVersion = 2;
        await launcher.InstallOrUpdatePackAsync(PackEdition.Destiny);
        Assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(mod)!, "fixed.jar")) == "fixed-mod-2", "A new pack version updates pinned mods as well as resolving latest mods");
        var recoveredRoot = Paths("linux-missing-state").Root;
        using var recovered = new LinuxLauncherService(recoveredRoot, http);
        new ConfigService(recovered.Paths).SaveInstalled("destiny", packVersion);
        await recovered.InstallOrUpdatePackAsync(PackEdition.Destiny);
        Assert(File.ReadAllText(Path.Combine(recovered.Paths.Instance("destiny"), "mods", "fixed.jar")) == "fixed-mod-2", "A missing managed-file record does not skip pinned mods even if the version marker exists");
    }
    private static async Task AuditLauncherUpdates()
    {
        using var key = RSA.Create(2048);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var payload = Encoding.UTF8.GetBytes("verified deb fixture");
        var url = "https://github.com/Destiny-Dynasty/Destiny-Launcher/releases/download/v2.0.0/destiny-launcher_2.0.0_amd64.deb";
        var release = new LauncherRelease(1, "2.0.0", "linux-x64", url, payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(release);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        try { LauncherUpdateService.VerifyManifest(bytes, signature, publicKey, "linux-arm64"); throw new Exception("Wrong architecture accepted"); }
        catch (InvalidDataException) { Passed.Add("Linux rejects a signed update for another architecture"); }
        var responses = new Dictionary<string, byte[]> {
            ["https://api.github.com/repos/Destiny-Dynasty/Destiny-Launcher/releases/latest"] = JsonSerializer.SerializeToUtf8Bytes(new { draft = false, prerelease = false, tag_name = "v2.0.0", assets = new[] {
                new { name = "launcher-update-linux-x64.json", browser_download_url = url.Replace("destiny-launcher_2.0.0_amd64.deb", "launcher-update-linux-x64.json") },
                new { name = "launcher-update-linux-x64.sig", browser_download_url = url.Replace("destiny-launcher_2.0.0_amd64.deb", "launcher-update-linux-x64.sig") } } }),
            [url.Replace("destiny-launcher_2.0.0_amd64.deb", "launcher-update-linux-x64.json")] = bytes,
            [url.Replace("destiny-launcher_2.0.0_amd64.deb", "launcher-update-linux-x64.sig")] = signature,
            [url] = payload
        };
        using var http = new HttpClient(new FakeHttp(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(responses[request.RequestUri!.AbsoluteUri]) })));
        using var updater = new LauncherUpdateService(Paths("launcher-update"), http, publicKey, new Version(1, 1, 0), "fixture", "linux-x64");
        var found = await updater.CheckAsync(CancellationToken.None);
        Assert(found == release, "Linux resolves the correctly signed release for its architecture");
        var path = await updater.DownloadAsync(found!, new Progress<double>(), CancellationToken.None);
        Assert(File.ReadAllBytes(path).SequenceEqual(payload), "Linux stages a package only after verifying its hash and size");
        responses[url] = Encoding.UTF8.GetBytes("corrupt deb fixture!");
        await Throws<InvalidDataException>(() => updater.DownloadAsync(found!, new Progress<double>(), CancellationToken.None), "Corrupt Linux package is rejected");
        Assert(File.ReadAllBytes(path).SequenceEqual(payload), "A failed download preserves the previously verified package");
        using var current = new LauncherUpdateService(Paths("launcher-update-current"), new HttpClient(new FakeHttp(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(responses[request.RequestUri!.AbsoluteUri]) }))), publicKey, new Version(2, 0, 0), "fixture", "linux-x64");
        Assert(await current.CheckAsync(CancellationToken.None) is null, "Linux does not offer the installed or an older version");
        bytes[0] ^= 1;
        try { LauncherUpdateService.VerifyManifest(bytes, signature, publicKey, "linux-x64"); throw new Exception("Tampered metadata was accepted"); }
        catch (InvalidDataException) { Passed.Add("Tampered Linux update metadata is rejected"); }
    }
    private static void AuditIndividualRemoval()
    {
        var paths = Paths("individual-removal");
        var data = new InstallationDataService(paths);
        var first = Path.Combine(paths.Root, "packs", "Destiny");
        var second = Path.Combine(paths.Root, "packs", "Destiny-Lite");
        foreach (var directory in new[] { first, second })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "destiny-installed.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "world.txt"), "fixture");
            data.Register(directory);
        }
        data.RemoveInstance(second);
        Assert(!Directory.Exists(second) && File.Exists(Path.Combine(first, "world.txt")), "Removing one modpack leaves the other untouched");
        Assert(data.PlanGameRemoval().SequenceEqual(new[] { first }), "Removing one modpack retains the other ownership record");
        var unowned = Path.Combine(paths.Root, "unowned"); Directory.CreateDirectory(unowned);
        try { data.RemoveInstance(unowned); throw new Exception("Unregistered folder was accepted"); }
        catch (InvalidOperationException) { Assert(Directory.Exists(unowned), "Unregistered folders cannot be removed"); }
    }
    private static void AuditVideoPlaybackPolicy()
    {
        Assert(VideoPlaybackPolicy.Select(false, true, true, false, true) == VideoPlaybackAction.Play, "Visible home plays background video");
        Assert(VideoPlaybackPolicy.Select(false, true, false, true, true) == VideoPlaybackAction.Pause, "Minimizing pauses and preserves the player");
        Assert(VideoPlaybackPolicy.Select(false, true, false, false, true) == VideoPlaybackAction.Pause, "Losing focus pauses the player");
        Assert(VideoPlaybackPolicy.Select(false, true, true, false, false) == VideoPlaybackAction.Pause, "Leaving home pauses the player");
        Assert(VideoPlaybackPolicy.Select(true, true, true, false, true) == VideoPlaybackAction.Release, "Game preparation releases the player");
        Assert(VideoPlaybackPolicy.Select(false, false, false, false, true) == VideoPlaybackAction.Release, "Hiding releases the player");
    }
    private static LauncherPaths Paths(string name)
    {
        var paths = new LauncherPaths(Path.Combine(_output, name + "-" + Guid.NewGuid().ToString("N")));
        paths.Ensure(); return paths;
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Passed.Add(message);
    }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { Passed.Add(message); return; }
        throw new InvalidOperationException(message);
    }
    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static async Task AuditDiscord()
    {
        var storage = new MemoryStorage(); var paths = Paths("discord");
        var endpoint = new Uri("https://bot.destinydynasty.site/");
        var token = new string('a', 64); var pairingId = new string('b', 64);
        string? challenge = null; var unauthorized = false; var revoked = false;
        using var http = new HttpClient(new FakeHttp(async request =>
        {
            Assert(request.RequestUri!.GetLeftPart(UriPartial.Authority) == "https://bot.destinydynasty.site",
                "Linux Discord requests use the new default HTTPS origin");
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/launcher/health": return Json(new { ok = true });
                case "/launcher/link":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync())) challenge = body.RootElement.GetProperty("challenge").GetString();
                    return Json(new { id = pairingId, url = "https://evil.example.invalid/" });
                case "/launcher/poll":
                    var secret = request.Headers.Authorization?.Parameter;
                    Assert(secret is { Length: 64 } && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).Equals(challenge, StringComparison.OrdinalIgnoreCase), "Discord polling proves possession of the browser challenge");
                    return Json(new { token, username = "TestUser", staff = true });
                case "/launcher/session":
                    Assert(request.Headers.Authorization?.Parameter == token, "Discord session uses the verified bearer token");
                    if (request.Method == HttpMethod.Delete) revoked = true;
                    return unauthorized ? Json(new { }, HttpStatusCode.Unauthorized) : Json(new { username = "TestUser", staff = true });
                default: throw new InvalidOperationException("Unexpected HTTP request");
            }
        }));
        var service = new LinuxDiscordLinkService(paths, http, null, storage, storage.Clear);
        Uri? opened = null;
        var access = await service.LinkAsync(uri => opened = uri, null, CancellationToken.None);
        Assert(opened == new Uri(endpoint, "launcher/authorize?id=" + pairingId), "Discord ignores a supplied external authorize URL");
        Assert(access.Staff && access.Username == "TestUser" && storage.Committed, "Discord grants Staff only after verified polling and secure commit");
        Assert(!File.ReadAllText(Path.Combine(paths.Root, "discord-session.bin")).Contains(token), "Discord marker contains no credential material");
        Assert((await service.RefreshAsync(CancellationToken.None))?.Staff == true, "A linked Discord session can be reverified");
        await service.UnlinkAsync(CancellationToken.None);
        Assert(revoked && storage.Value is null && !service.HasCredentialMarker && service.Access is null, "Discord unlink revokes the remote session and clears local access");
        await service.LinkAsync(_ => { }, null, CancellationToken.None);
        unauthorized = true;
        Assert(await service.RefreshAsync(CancellationToken.None) is null && !service.HasCredentialMarker, "An expired Discord session cannot retain Staff access");
        using var badHttp = new HttpClient(new FakeHttp(_ => Task.FromResult(Json(new { id = "malformed" }))));
        var bad = new LinuxDiscordLinkService(Paths("discord-invalid"), badHttp, endpoint, new MemoryStorage(), () => { });
        await Throws<InvalidDataException>(() => bad.LinkAsync(_ => throw new InvalidOperationException("Browser must not open"), null, CancellationToken.None), "Invalid Discord pairing IDs are rejected before opening a browser");
        using var cancellation = new CancellationTokenSource();
        unauthorized = false;
        await Throws<OperationCanceledException>(() => service.LinkAsync(_ => cancellation.Cancel(), null, cancellation.Token), "Closing Discord linking cancels before credentials are saved");
        Assert(!service.HasCredentialMarker, "Cancelled linking does not create a credential marker");
    }
    private static byte[] Skin(int height, byte alpha)
    {
        using var bitmap = new SKBitmap(64, height);
        bitmap.Erase(SKColors.Transparent);
        for (var y = 8; y < 16; y++) for (var x = 8; x < 16; x++) bitmap.SetPixel(x, y, SKColors.Red);
        for (var y = 8; y < 16; y++) for (var x = 40; x < 48; x++) bitmap.SetPixel(x, y, new SKColor(0, 255, 0, alpha));
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static byte[] Pixels(Bitmap bitmap)
    {
        using var target = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = target.Lock(); bitmap.CopyPixels(frame);
        var data = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
        for (var y = 0; y < bitmap.PixelSize.Height; y++) System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(frame.Address, y * frame.RowBytes), data, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
        return data;
    }
    private static async Task AuditPortraits()
    {
        foreach (var (height, alpha) in new[] { (64, (byte)128), (32, (byte)255) })
        {
            var requests = 0; var skin = Skin(height, alpha);
            using var http = new HttpClient(new FakeHttp(request =>
            {
                requests++;
                return Task.FromResult(request.RequestUri!.Host switch
                {
                    "api.mojang.com" => Json(new { id = new string('a', 32) }),
                    "sessionserver.mojang.com" => Json(new { properties = new[] { new { name = "textures", value = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { textures = new { SKIN = new { url = "http://textures.minecraft.net/texture/abc123" } } }))) } } }),
                    "textures.minecraft.net" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(skin) },
                    _ => throw new InvalidOperationException("Unexpected portrait host")
                });
            }));
            using var service = new LinuxPlayerHeadService(Paths("heads-" + height), http);
            using var portrait = await service.GetHeadAsync("TestPlayer", CancellationToken.None);
            Assert(portrait?.PixelSize == new PixelSize(8, 8), "Verified " + height + "px skin extracts an 8x8 portrait");
            var pixels = Pixels(portrait!);
            Assert(height == 32 ? pixels[1] == 0 && pixels[2] == 255 : pixels[1] is >= 127 and <= 129 && pixels[2] is >= 126 and <= 128, height == 32 ? "Legacy opaque hat rectangles do not cover the face" : "Modern hat layers are alpha-composited over the face");
            using var cached = await service.GetHeadAsync("TestPlayer", CancellationToken.None);
            Assert(requests == 3 && Pixels(cached!).SequenceEqual(pixels), "Verified portraits are reused from cache without network calls");
        }
    }
    private static async Task AuditNews()
    {
        var paths = Paths("news"); var badFeed = false; var imageRequests = 0;
        using var http = new HttpClient(new FakeHttp(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/news.json") return Task.FromResult(Json(new { schemaVersion = 1, items = new[] { new { id = "test", caption = "Noticia de prueba", imageUrl = badFeed ? "http://example.invalid/unsafe.png" : "https://example.invalid/test.png", linkUrl = "/novedades/test/", enabled = true } } }));
            imageRequests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Skin(64, 128)) });
        }));
        using var service = new LinuxNewsFeedService(paths, http);
        var fresh = await service.RefreshAsync(CancellationToken.None);
        Assert(fresh.Count == 1 && fresh[0].Link == new Uri(LinuxNewsFeedService.FeedUri, "/novedades/test/"), "News uses the same public feed and resolves safe relative links");
        Assert(fresh[0].Image.PixelSize == new PixelSize(64, 64), "Small news images keep their source resolution instead of allocating an upscaled native bitmap");
        var cached = await service.ReadCacheAsync(CancellationToken.None);
        Assert(cached?.Count == 1 && imageRequests == 1, "News remains available from the verified image cache");
        badFeed = true;
        await Throws<InvalidDataException>(() => service.RefreshAsync(CancellationToken.None), "Malformed news cannot replace a working cache with unsafe HTTP content");
        var preserved = await service.ReadCacheAsync(CancellationToken.None);
        Assert(preserved?.Count == 1, "Working news survives a failed refresh");
        foreach (var slide in fresh.Concat(cached ?? []).Concat(preserved ?? [])) slide.Image.Dispose();
    }
    private static void AuditStaffGate()
    {
        using var launcher = new LinuxLauncherService(Paths("staff").Root);
        try { launcher.SelectEdition(PackEdition.DestinyStaff); throw new InvalidOperationException("Unverified Staff access was accepted"); }
        catch (ArgumentOutOfRangeException) { Passed.Add("Staff edition rejects an unverified selection"); }
        launcher.SetStaffAccess(true); launcher.SelectEdition(PackEdition.DestinyStaff);
        launcher.SetStaffAccess(false);
        Assert(launcher.Settings.SelectedEdition == PackEdition.Destiny && !launcher.StaffAllowed, "Revoking Staff access resets the selected edition before launch");
        Assert(launcher.Settings.InstallDirectory.StartsWith(_output, StringComparison.OrdinalIgnoreCase), "Isolated previews cannot select the real installation by default");
    }
    private sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); var response = await respond(request); response.RequestMessage = request; return response; }
    }
    private sealed class MemoryStorage : ITransactionalJsonStorage
    {
        internal JsonNode? Value;
        internal bool Committed;
        public JsonNode? ReadAsJsonNode() => Value?.DeepClone();
        public void Write(JsonNode node, JsonSerializerOptions? serializerOptions = null) => Value = node.DeepClone();
        public void Commit() => Committed = true;
        internal void Clear() { Value = null; Committed = false; }
    }
}
