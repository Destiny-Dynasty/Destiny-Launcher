using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

internal static class FloatingModsAudit
{
    internal static async Task Live(string root)
    {
        Directory.CreateDirectory(root);
        using var http = new HttpClient(); http.DefaultRequestHeaders.UserAgent.ParseAdd("DestinyLauncher-Test");
        using var response = await http.GetAsync($"https://raw.githubusercontent.com/Destiny-Dynasty/Mod-Releases/main/channels/destiny-realms.json?check={Guid.NewGuid():N}");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var asset = json.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() is string name && !name.EndsWith("-sources.jar") && name.EndsWith(".jar"));
        var file = new PackManifestFile { Path = "mods/destiny-realms.jar", Url = asset.GetProperty("browser_download_url").GetString()!,
            Size = asset.GetProperty("size").GetInt64(), Sha256 = asset.GetProperty("digest").GetString()![7..],
            GitHub = new() { Repository = "Destiny-Dynasty/Mod-Releases", Release = "latest", Channel = "destiny-realms", Asset = "destiny-realms-*.jar" } };
        var manifest = new PackManifest { PackId = "destiny", Version = "2026.0.1-alpha", SchemaVersion = 2, Files = [file] };
        var paths = new LauncherPaths(Path.Combine(root, Guid.NewGuid().ToString("N"))); paths.Ensure();
        var updater = new GitHubPackUpdater(paths, new AppLogger(paths), http);
        var pack = new PackDefinition();
        var initial = await updater.PrepareForPlayAsync(pack, manifest, "", null, CancellationToken.None);
        await updater.ApplyAsync(pack, initial, null, CancellationToken.None);
        var downloaded = Path.Combine(paths.Instance("destiny"), "mods", "destiny-realms.jar");
        using var jar = System.IO.Compression.ZipFile.OpenRead(downloaded);
        if (jar.GetEntry("fabric.mod.json") is null || (await updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None)).HasChanges) throw new Exception("Live GitHub mod verification failed");
        File.WriteAllText(Path.Combine(root, "live-github-result.json"), JsonSerializer.Serialize(new { verified = true, file.Url, file.Sha256, downloaded, executed = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: real GitHub latest asset downloaded, SHA-256 verified, sources excluded, and repeat check is a no-op. No game/mod code executed.");
    }

    internal static async Task Run(string root)
    {
        Directory.CreateDirectory(root);
        var paths = new LauncherPaths(Path.Combine(root, Guid.NewGuid().ToString("N"))); paths.Ensure();
        var handler = new ReleaseHandler();
        var updater = new GitHubPackUpdater(paths, new AppLogger(paths), new HttpClient(handler));
        var pack = new PackDefinition();
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); throw new Exception("Expected rejection: " + label); }
            catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException) { checks.Add(label); }
        }
        var manifest = await updater.ReadManifestAsync(pack, CancellationToken.None);
        Check(handler.ApiCalls == 0 && !Directory.Exists(paths.Instance(pack.Slug)), "Startup reads only modpack metadata without resolving latest or touching files");
        var install = await updater.PrepareForPlayAsync(pack, manifest, "", null, CancellationToken.None);
        Check(handler.ApiCalls == 1 && install.ChangedFiles.Count == 4, "Two floating mods from one repository share a single release lookup");
        await updater.ApplyAsync(pack, install, null, CancellationToken.None);
        var instance = paths.Instance(pack.Slug);
        var mod = Path.Combine(instance, "mods", "custom.jar");
        Check(File.ReadAllText(mod) == "custom-version-1", "Initial install verifies and downloads GitHub release assets");
        handler.Version = 2;
        using (var locked = new FileStream(Path.Combine(instance, "config", "base.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var lockedPinned = new FileStream(Path.Combine(instance, "mods", "pinned.jar"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var update = await updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None);
            Check(update.FloatingOnly && update.ChangedFiles.Count == 1 && update.ChangedFiles[0].Path == "mods/custom.jar", "Same pack version updates only the latest mod without opening static/pinned files");
            await updater.ApplyAsync(pack, update, null, CancellationToken.None);
        }
        Check(File.ReadAllText(mod) == "custom-version-2" && !Directory.GetFiles(Path.Combine(instance, "mods")).Any(f => f.EndsWith("custom-2.jar")), "Versioned release names are installed into one stable target without duplicate old JARs");
        var unchanged = await updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None);
        Check(!unchanged.HasChanges && handler.ConditionalCalls > 0 && handler.NotModified > 0, "Every Play checks releases, while ETag 304 avoids resolving unchanged metadata again");
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(instance, ".destiny-managed-files.json"))))
            Check(state.RootElement.GetProperty("Version").GetString() == "2026.0.1-alpha", "Independent mod updates do not change the installed modpack version");
        handler.Offline = true;
        Check(!(await updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None)).HasChanges && File.ReadAllText(mod) == "custom-version-2", "Offline checks keep the committed latest version instead of downgrading to the publishing snapshot");
        handler.Offline = false; handler.RateLimited = true;
        Check(!(await updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None)).HasChanges, "GitHub rate limits preserve the installed mod without blocking its launch");
        handler.RateLimited = false; handler.Version = 3; handler.Corrupt = true;
        var corrupt = await updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None);
        await Reject(() => updater.ApplyAsync(pack, corrupt, null, CancellationToken.None), "Corrupt floating assets fail SHA-256 before applying changes");
        Check(File.ReadAllText(mod) == "custom-version-2", "Failed latest download preserves the previously working mod");
        handler.Corrupt = false; handler.Version = 4; handler.NoDigest = true;
        await Reject(() => updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None), "Latest without a SHA-256 digest cannot bypass download verification");
        handler.NoDigest = false; handler.Version = 5; handler.Ambiguous = true;
        await Reject(() => updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None), "Ambiguous release asset selectors are rejected");
        handler.Ambiguous = false; handler.Version = 6; handler.WrongHost = true;
        await Reject(() => updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None), "Release assets cannot redirect the manifest to an unrelated download origin");
        handler.WrongHost = false; handler.Version = 7; handler.WrongTag = true;
        await Reject(() => updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None), "Channel assets must belong to the declared release tag, preventing stale draft aliases");
        handler.WrongTag = false; handler.Version = 8;
        File.WriteAllText(mod, "player edit");
        var edited = await updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None);
        Check(!edited.HasChanges && edited.PreservedOverrides.Contains("mods/custom.jar") && File.ReadAllText(mod) == "player edit", "Locally modified official mods keep existing customization protections");
        File.Delete(mod);
        Check(!(await updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, CancellationToken.None)).HasChanges, "Intentionally deleted official mods remain deleted on normal Play");
        var thirdParty = Path.Combine(instance, "mods", "player-added.jar"); File.WriteAllText(thirdParty, "personal mod");
        handler.Version = 8;
        var repaired = await updater.RepairAsync(pack, manifest, false, null, CancellationToken.None);
        Check(File.ReadAllText(mod) == "custom-version-8" && File.ReadAllText(thirdParty) == "personal mod", "Explicit repair resolves latest and restores official mods while keeping third-party mods");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Reject(() => updater.PrepareForPlayAsync(pack, manifest, manifest.Version, null, canceled.Token), "Canceling latest checks stops the operation before mutation");
        handler.Legacy = true;
        await Reject(() => updater.ReadManifestAsync(pack, CancellationToken.None), "Floating descriptors require schema 2 so older clients cannot silently ignore them");
        Check(paths.Instance("destiny-staff") == paths.Instance("destiny") + "-Staff", "Staff has its own independent instance directory");
        handler.Legacy = false; handler.UseChannel = true; handler.Version = 9;
        var channelManifest = await updater.ReadManifestAsync(pack, CancellationToken.None);
        var apiBefore = handler.ApiCalls;
        var channelUpdate = await updater.PrepareForPlayAsync(pack, channelManifest, channelManifest.Version, null, CancellationToken.None);
        await updater.ApplyAsync(pack, channelUpdate, null, CancellationToken.None);
        Check(handler.ApiCalls == apiBefore && handler.ChannelCalls == 1 && File.ReadAllText(mod) == "custom-version-9", "Public per-mod channels resolve releases from a shared repository without private credentials or GitHub API calls");
        File.WriteAllText(Path.Combine(root, "floating-results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {checks.Count} GitHub latest, integrity and per-variant assertions; {root}");
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        internal int Version = 1, ApiCalls, ConditionalCalls, NotModified, ChannelCalls;
        internal bool Offline, RateLimited, Corrupt, NoDigest, Ambiguous, WrongHost, WrongTag, Legacy, UseChannel;
        private static byte[] Data(string text) => Encoding.UTF8.GetBytes(text);
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static PackManifestFile File(string path, byte[] bytes, string url) => new() { Path = path, Size = bytes.Length, Sha256 = Hash(bytes), Url = url };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (uri.Host == "raw.githubusercontent.com" && !uri.AbsolutePath.Contains("/channels/"))
            {
                var main = File("mods/custom.jar", Data("custom-version-1"), "https://github.com/Fixture/Custom/releases/download/v1/custom-1.jar");
                main.GitHub = new() { Repository = "Fixture/Custom", Release = "latest", Asset = "custom-*.jar", Channel = UseChannel ? "custom" : null };
                var helper = File("mods/helper.jar", Data("helper"), "https://github.com/Fixture/Custom/releases/download/v1/helper-1.jar");
                helper.GitHub = new() { Repository = "Fixture/Custom", Release = "latest", Asset = "helper-*.jar", Channel = UseChannel ? "custom" : null };
                return Task.FromResult(Response(JsonSerializer.SerializeToUtf8Bytes(new PackManifest { PackId = "destiny", Version = "2026.0.1-alpha", SchemaVersion = Legacy ? 1 : 2,
                    Files = [main, helper, File("mods/pinned.jar", Data("pinned"), "https://fixture.invalid/pinned"), File("config/base.txt", Data("base"), "https://fixture.invalid/base")] })));
            }
            if (uri.Host == "api.github.com" || uri.AbsolutePath.Contains("/channels/"))
            {
                if (uri.Host == "api.github.com") ApiCalls++; else ChannelCalls++;
                if (Offline) throw new HttpRequestException("Offline fixture");
                if (RateLimited) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                var tag = $"\"release-{Version}\"";
                if (request.Headers.IfNoneMatch.Count > 0) ConditionalCalls++;
                if (request.Headers.IfNoneMatch.Any(h => h.Tag == tag)) { NotModified++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }
                object Asset(string name, byte[] bytes) => new { name, size = bytes.Length, digest = NoDigest ? null : "sha256:" + Hash(bytes), browser_download_url = WrongHost ? "https://evil.invalid/mod.jar" : $"https://github.com/Fixture/Custom/releases/download/v{Version}/{name}" };
                var assets = new List<object> { Asset($"custom-{Version}.jar", Data($"custom-version-{Version}")), Asset($"helper-{Version}.jar", Data("helper")) };
                if (Ambiguous) assets.Add(Asset("custom-other.jar", Data("other")));
                var response = Response(JsonSerializer.SerializeToUtf8Bytes(new { draft = false, prerelease = false, tag_name = WrongTag ? "untagged-invalid" : $"v{Version}", assets }));
                response.Headers.ETag = new EntityTagHeaderValue(tag);
                return Task.FromResult(response);
            }
            if (uri.Host == "github.com")
            {
                var name = Path.GetFileName(uri.AbsolutePath);
                var version = name.StartsWith("custom-") ? name[7..^4] : "";
                var bytes = name.StartsWith("helper-") ? Data("helper") : Data("custom-version-" + version);
                return Task.FromResult(Response(Corrupt ? new byte[bytes.Length] : bytes));
            }
            return Task.FromResult(Response(Data(uri.AbsolutePath.Contains("pinned") ? "pinned" : "base")));
        }
    }
}
