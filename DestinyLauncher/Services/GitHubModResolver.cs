using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;

internal sealed class GitHubModResolver(LauncherPaths paths, AppLogger log, HttpClient http)
{
    private sealed record ReleaseCache(string? ETag, JsonElement Body);
    internal static bool IsLatest(PackManifestFile file) => file.GitHub?.Release == "latest";
    internal static string SourceKey(PackManifestFile file) => file.GitHub is null ? "" :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.GitHub.Repository.ToLowerInvariant()}\n{file.GitHub.Release}\n{file.GitHub.Asset}\n{file.GitHub.Channel}\n{file.Path.ToLowerInvariant()}")));

    internal static void Validate(PackManifestFile file)
    {
        var source = file.GitHub!;
        if (!Regex.IsMatch(source.Repository ?? "", @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
            || (source.Repository ?? "").Split('/').Any(x => x is "." or "..")
            || source.Release != "latest" || string.IsNullOrWhiteSpace(source.Asset) || source.Asset.Length > 200
            || source.Asset.IndexOfAny(['/', '\\', ':', '\0']) >= 0
            || (source.Channel is not null && !Regex.IsMatch(source.Channel, "^[a-z0-9][a-z0-9-]{0,63}$"))
            || !file.Path.Replace('\\', '/').StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
            || !file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Origen GitHub inválido para «{file.Path}». Las releases fijas deben resolverse al publicar.");
    }

    internal async Task<PackManifest> ResolveAsync(PackManifest manifest, IReadOnlyList<ManagedPackFile> installed,
        IProgress<(string status, double value)>? progress, CancellationToken token)
    {
        var releases = new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase);
        var files = new List<PackManifestFile>();
        foreach (var file in manifest.Files)
        {
            if (!IsLatest(file)) { files.Add(file); continue; }
            Validate(file);
            var repository = file.GitHub!.Repository;
            var key = repository + "\n" + file.GitHub.Channel;
            if (!releases.TryGetValue(key, out var release))
            {
                progress?.Report(("Buscando actualizaciones de mods", .02));
                release = await LatestAsync(repository, file.GitHub.Channel, token);
                releases[key] = release;
            }
            if (release is null)
            {
                // An offline/rate-limited check must never roll a floating mod back to
                // the older publishing snapshot. Prefer its last committed resolution.
                var previous = installed.FirstOrDefault(f => f.SourceKey == SourceKey(file));
                files.Add(previous is not null && Uri.TryCreate(previous.Url, UriKind.Absolute, out var url) && url.Scheme == "https"
                    ? new PackManifestFile { Path = file.Path, Url = previous.Url, Size = previous.Size, Sha256 = previous.Sha256, GitHub = file.GitHub }
                    : file);
                continue;
            }
            var body = release.Value;
            if (body.GetProperty("draft").GetBoolean() || body.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException($"La release latest de {repository} no es estable.");
            var assets = body.GetProperty("assets").EnumerateArray().Where(asset =>
            {
                var name = asset.GetProperty("name").GetString()!;
                return Matches(file.GitHub.Asset, name) && !Regex.IsMatch(name, "-(sources|javadoc)\\.jar$", RegexOptions.IgnoreCase);
            }).ToArray();
            if (assets.Length != 1) throw new InvalidDataException($"{repository}: el selector «{file.GitHub.Asset}» debe identificar un único mod en la release latest.");
            var asset = assets[0];
            var link = asset.GetProperty("browser_download_url").GetString()!;
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (digest is null || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$"))
                throw new InvalidDataException($"GitHub no ofrece un SHA-256 para el mod de {repository}. Publica de nuevo ese asset o fija su versión en el manifiesto.");
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || !body.TryGetProperty("tag_name", out var releaseTag) || string.IsNullOrWhiteSpace(releaseTag.GetString())
                || !uri.AbsolutePath.StartsWith($"/{repository}/releases/download/{Uri.EscapeDataString(releaseTag.GetString()!)}/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub devolvió una descarga ajena al repositorio del mod.");
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0) throw new InvalidDataException("GitHub devolvió un mod vacío.");
            files.Add(new PackManifestFile { Path = file.Path, Url = link, Size = size, Sha256 = digest[7..], GitHub = file.GitHub });
        }
        return new PackManifest { SchemaVersion = manifest.SchemaVersion, PackId = manifest.PackId, Version = manifest.Version,
            MinecraftVersion = manifest.MinecraftVersion, FabricLoaderVersion = manifest.FabricLoaderVersion, JavaVersion = manifest.JavaVersion, Files = files };
    }

    private static bool Matches(string pattern, string name) => Regex.IsMatch(name,
        "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private async Task<JsonElement?> LatestAsync(string repository, string? channel, CancellationToken token)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository.ToLowerInvariant() + "\n" + channel)));
        var cachePath = Path.Combine(paths.Cache, "github-mods", key + ".json");
        ReleaseCache? cached = null;
        try { cached = JsonSerializer.Deserialize<ReleaseCache>(await File.ReadAllTextAsync(cachePath, token)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var endpoint = channel is null ? $"https://api.github.com/repos/{repository}/releases/latest"
                // Raw's edge cache can serve a previous channel for several minutes
                // despite no-cache. Revalidate a fresh URL on Play; the saved ETag
                // still lets the origin answer 304 for unchanged channel content.
                : $"https://raw.githubusercontent.com/{repository}/main/channels/{channel}.json?check={Guid.NewGuid():N}";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            request.Headers.UserAgent.ParseAdd("DestinyLauncher");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (cached?.ETag is string tag && EntityTagHeaderValue.TryParse(tag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null) return cached.Body;
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[81920]; int read;
            while ((read = await source.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + read > 2097152) throw new InvalidDataException("Respuesta de GitHub demasiado grande.");
                buffer.Write(bytes, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var result = json.RootElement.Clone();
            var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                InstallationDataService.RejectLinks(cachePath);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ReleaseCache(response.Headers.ETag?.ToString(), result)), token);
                File.Move(temporary, cachePath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Info("Could not cache GitHub mod metadata: " + ex.Message); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.Info($"GitHub latest check unavailable for {repository}; retaining installed/published mod. {ex.Message}");
            // The committed instance state is newer/more reliable than a possibly stale
            // response left by another variant; prefer it through the null fallback.
            return null;
        }
    }
}
