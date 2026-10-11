using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;

/// <summary>
/// Pulls a small JSON manifest (for example from GitHub Raw) and only downloads
/// changed files. File URLs can point at individual GitHub Release assets.
/// </summary>
public sealed class GitHubPackUpdater
{
    private const string StateFileName = ".destiny-managed-files.json";
    private static readonly HashSet<string> ManagedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "kubejs", "scripts"
    };
    private static readonly HashSet<string> ProtectedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "screenshots", "logs", "crash-reports", "assets", "libraries", "versions",
        "natives", "resource-cache", "webcache", "shader-cache", "server-resource-packs"
    };
    private static readonly HashSet<string> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "optionsof.txt", "servers.dat", "servers.dat_old", "launcher_profiles.json",
        "usercache.json", "usernamecache.json", "launcher_accounts.json", StateFileName,
        "destiny-installed.json", "latest.log"
    };

    private readonly LauncherPaths _paths;
    private readonly AppLogger _log;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public GitHubPackUpdater(LauncherPaths paths, AppLogger log, HttpClient? httpClient = null)
    {
        _paths = paths;
        _log = log;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<PackUpdateCheck> CheckAsync(PackDefinition pack, IProgress<(string status, double value)>? progress, CancellationToken token)
    {
        if (!Uri.TryCreate(pack.ManifestUrl, UriKind.Absolute, out var manifestUri) || manifestUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Configura una URL HTTPS válida para el manifiesto del modpack.");

        progress?.Report(("Consultando manifiesto de GitHub", .02));
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(token);
        var manifest = await JsonSerializer.DeserializeAsync<PackManifest>(responseStream, _json, token)
            ?? throw new InvalidDataException("El manifiesto del modpack está vacío o no es JSON válido.");
        ValidateManifest(pack, manifest);

        var statePath = Path.Combine(_paths.Instance(pack.Slug), StateFileName);
        var local = ReadState(statePath, pack.Slug);
        var localFiles = local?.Files.ToDictionary(x => NormalizePath(x.Path), StringComparer.OrdinalIgnoreCase)
                         ?? new Dictionary<string, ManagedPackFile>(StringComparer.OrdinalIgnoreCase);
        var overrides = (local?.Overrides ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preservedOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<PackManifestFile>();
        var completed = 0;
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var normalized = NormalizePath(file.Path);
            var target = SafeTargetPath(_paths.Instance(pack.Slug), normalized);
            var isCurrent = false;
            string? localHash = null;
            if (File.Exists(target))
            {
                var info = new FileInfo(target);
                localHash = await HashFileAsync(target, token);
                isCurrent = info.Length == file.Size && localHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
            }
            if (overrides.Contains(normalized) && File.Exists(target) && !isCurrent)
            {
                preservedOverrides.Add(normalized);
            }
            else if (localFiles.TryGetValue(normalized, out var previouslyManaged) && File.Exists(target) &&
                     !string.IsNullOrEmpty(localHash) && !localHash.Equals(previouslyManaged.Sha256, StringComparison.OrdinalIgnoreCase) && !isCurrent)
            {
                overrides.Add(normalized);
                preservedOverrides.Add(normalized);
            }
            else
            {
                overrides.Remove(normalized);
                if (!isCurrent) changed.Add(file);
            }
            completed++;
            progress?.Report(($"Comprobando archivos · {completed}/{manifest.Files.Count}", .02 + .38 * completed / Math.Max(1, manifest.Files.Count)));
        }

        var desired = manifest.Files.Select(x => NormalizePath(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        foreach (var (path, tracked) in localFiles.Where(x => !desired.Contains(x.Key)))
        {
            var target = SafeTargetPath(_paths.Instance(pack.Slug), path);
            if (!File.Exists(target)) continue;
            var localHash = await HashFileAsync(target, token);
            if (overrides.Contains(path) || !localHash.Equals(tracked.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                overrides.Add(path);
                preservedOverrides.Add(path);
            }
            else removed.Add(path);
        }
        _log.Info($"Manifest checked: {pack.Slug} {manifest.Version}; changed={changed.Count}, removed={removed.Count}, preserved={preservedOverrides.Count}");
        return new PackUpdateCheck(manifest, changed, removed, preservedOverrides.ToArray());
    }

    public async Task ApplyAsync(PackDefinition pack, PackUpdateCheck update, IProgress<(string status, double value)>? progress, CancellationToken token)
    {
        ValidateManifest(pack, update.Manifest);
        var instance = _paths.Instance(pack.Slug);
        Directory.CreateDirectory(instance);
        var workspace = Path.Combine(_paths.Temp, $"manifest-{pack.Slug}-{Guid.NewGuid():N}");
        var downloads = Path.Combine(workspace, "downloads");
        var backups = Path.Combine(workspace, "backups");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(backups);
        var oldStatePath = Path.Combine(instance, StateFileName);
        var oldState = ReadState(oldStatePath, pack.Slug);
        var oldManaged = (oldState?.Files ?? []).Select(x => NormalizePath(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var downloadNumber = 0;
            foreach (var file in update.ChangedFiles)
            {
                token.ThrowIfCancellationRequested();
                ValidateFile(file);
                var target = SafeTargetPath(instance, NormalizePath(file.Path));
                if (File.Exists(target) && !oldManaged.Contains(NormalizePath(file.Path)))
                    throw new IOException($"El archivo «{file.Path}» ya existe y no pertenece al launcher. Se conservó sin cambios.");

                var staged = SafeTargetPath(downloads, NormalizePath(file.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                using var response = await _http.GetAsync(new Uri(file.Url), HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength && contentLength != file.Size)
                    throw new InvalidDataException($"El tamaño anunciado para «{file.Path}» no coincide con el manifiesto.");

                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                {
                    var buffer = new byte[131072];
                    long received = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, token)) > 0)
                    {
                        received += count;
                        if (received > file.Size)
                            throw new InvalidDataException($"La descarga de «{file.Path}» supera el tamaño declarado en el manifiesto.");
                        hasher.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                    }

                    var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
                    if (received != file.Size || !actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"No coincide el tamaño o SHA-256 del archivo «{file.Path}».");
                }

                downloadNumber++;
                progress?.Report(($"Descargando modpack · {downloadNumber}/{update.ChangedFiles.Count}", .4 + .48 * downloadNumber / Math.Max(1, update.ChangedFiles.Count)));
            }

            var affected = update.Manifest.Files.Select(x => NormalizePath(x.Path)).Where(x => update.ChangedFiles.Any(f => NormalizePath(f.Path).Equals(x, StringComparison.OrdinalIgnoreCase)))
                .Concat(update.RemovedFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var backedUp = new List<(string relative, string backup, bool existed)>();
            try
            {
                foreach (var relative in affected)
                {
                    token.ThrowIfCancellationRequested();
                    var target = SafeTargetPath(instance, relative);
                    var backup = SafeTargetPath(backups, relative);
                    var existed = File.Exists(target);
                    if (existed)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(target, backup, true);
                    }
                    backedUp.Add((relative, backup, existed));
                }

                foreach (var file in update.ChangedFiles)
                {
                    var relative = NormalizePath(file.Path);
                    var target = SafeTargetPath(instance, relative);
                    var staged = SafeTargetPath(downloads, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(staged, target, true);
                }

                foreach (var relative in update.RemovedFiles)
                {
                    var target = SafeTargetPath(instance, relative);
                    if (File.Exists(target)) File.Delete(target);
                }

                var nextState = new ManagedPackState
                {
                    PackId = pack.Slug,
                    Version = update.Manifest.Version,
                    Overrides = update.PreservedOverrides.ToList(),
                    Files = update.Manifest.Files.Select(x => new ManagedPackFile
                    {
                        Path = NormalizePath(x.Path), Size = x.Size, Sha256 = x.Sha256.ToLowerInvariant()
                    }).ToList()
                };
                var newStatePath = Path.Combine(workspace, StateFileName);
                await File.WriteAllTextAsync(newStatePath, JsonSerializer.Serialize(nextState, _json), token);
                File.Copy(newStatePath, oldStatePath + ".next", true);
                File.Move(oldStatePath + ".next", oldStatePath, true);
                progress?.Report(($"Modpack actualizado · {update.Manifest.Version}", 1));
                _log.Info($"Manifest applied: {pack.Slug} {update.Manifest.Version}; downloaded={update.ChangedFiles.Count}, removed={update.RemovedFiles.Count}");
            }
            catch
            {
                foreach (var item in backedUp.AsEnumerable().Reverse())
                {
                    var target = SafeTargetPath(instance, item.relative);
                    if (item.existed)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (File.Exists(item.backup)) File.Copy(item.backup, target, true);
                    }
                    else if (File.Exists(target)) File.Delete(target);
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"GitHub manifest update failed for {pack.Slug}", ex);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); } catch (Exception ex) { _log.Error("Could not clean manifest staging files", ex); }
        }
    }

    private void ValidateManifest(PackDefinition pack, PackManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Versión de manifiesto no compatible: {manifest.SchemaVersion}.");
        if (!manifest.PackId.Equals(pack.Slug, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("El manifiesto pertenece a otra edición del modpack.");
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 64) throw new InvalidDataException("El manifiesto no contiene una versión válida.");
        if (string.IsNullOrWhiteSpace(manifest.MinecraftVersion) || string.IsNullOrWhiteSpace(manifest.FabricLoaderVersion)) throw new InvalidDataException("Faltan las versiones de Minecraft o Fabric en el manifiesto.");
        if (manifest.Files is null) throw new InvalidDataException("El manifiesto no contiene una lista de archivos.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ValidateFile(file);
            if (!paths.Add(NormalizePath(file.Path))) throw new InvalidDataException($"El manifiesto repite la ruta «{file.Path}».");
        }
    }

    private static void ValidateFile(PackManifestFile file)
    {
        var path = NormalizePath(file.Path);
        var parts = path.Split('/');
        if (parts.Length < 2 || !ManagedRoots.Contains(parts[0]) || parts.Any(x => x is "" or "." or "..") || path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException($"Ruta no permitida en el manifiesto: «{file.Path}».");
        if (ProtectedRoots.Contains(parts[0]) || ProtectedFiles.Contains(path)) throw new InvalidDataException($"El manifiesto intenta reemplazar datos personales: «{file.Path}».");
        if (file.Size < 0 || !Regex.IsMatch(file.Sha256 ?? "", "^[a-fA-F0-9]{64}$")) throw new InvalidDataException($"Tamaño o SHA-256 incorrecto para «{file.Path}».");
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException($"URL HTTPS inválida para «{file.Path}».");
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string SafeTargetPath(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Una ruta del manifiesto sale del directorio de la instancia.");
        return target;
    }

    private static ManagedPackState? ReadState(string path, string packId)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<ManagedPackState>(File.ReadAllText(path));
            return state is not null && state.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase) ? state : null;
        }
        catch { return null; }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash);
    }
}
