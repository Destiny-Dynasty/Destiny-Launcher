using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;

public sealed class ZipPackInstaller
{
    private static readonly HashSet<string> ManagedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "kubejs", "scripts"
    };
    private static readonly HashSet<string> PersonalRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "screenshots", "logs", "crash-reports", "server-resource-packs"
    };
    private static readonly HashSet<string> PersonalFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "optionsof.txt", "servers.dat", "servers.dat_old", "launcher_profiles.json",
        "usercache.json", "usernamecache.json", "launcher_accounts.json", "latest.log",
        ".destiny-managed-files.json", "destiny-installed.json"
    };

    private readonly LauncherPaths _paths;
    private readonly AppLogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ZipPackInstaller(LauncherPaths paths, AppLogger log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task InstallAsync(PackDefinition pack, IProgress<(string status, double value)> progress, CancellationToken token)
    {
        if (!Uri.TryCreate(pack.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("La URL del modpack no es válida.");

        var instance = _paths.Instance(pack.Slug);
        var workspace = Path.Combine(_paths.Temp, $"zip-{pack.Slug}-{Guid.NewGuid():N}");
        var archive = Path.Combine(workspace, "pack.zip");
        var staging = Path.Combine(workspace, "extracted");
        var backupRoot = Path.Combine(workspace, "backup");
        var copyFiles = new List<(string source, string relative)>();
        var removeFiles = new List<string>();

        try
        {
            Directory.CreateDirectory(workspace);
            progress.Report(("Descargando " + pack.Name, 0));
            _log.Info($"ZIP download started: {pack.Name} {uri}");
            using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                var buffer = new byte[131072];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    received += count;
                    var fraction = total > 0 ? received / (double)total : 0;
                    progress.Report((total > 0 ? $"Descargando {pack.Name} · {Format(received)} / {Format(total)}" : "Descargando " + pack.Name, Math.Min(.54, fraction * .54)));
                }
            }

            progress.Report(("Verificando y preparando archivos", .56));
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(archive, staging);
            Directory.CreateDirectory(instance);

            var statePath = Path.Combine(instance, ".destiny-managed-files.json");
            var oldState = ReadState(statePath, pack.Slug);
            var oldFiles = (oldState?.Files ?? []).ToDictionary(x => NormalizePath(x.Path), StringComparer.OrdinalIgnoreCase);
            var overrides = (oldState?.Overrides ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var stagedFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToArray();
            var stagedManaged = new Dictionary<string, ManagedPackFile>(StringComparer.OrdinalIgnoreCase);
            var countDone = 0;
            foreach (var source in stagedFiles)
            {
                token.ThrowIfCancellationRequested();
                var relative = NormalizePath(Path.GetRelativePath(staging, source));
                if (IsPersonalPath(relative)) continue;
                copyFiles.Add((source, relative));

                if (IsManagedPath(relative))
                {
                    stagedManaged[relative] = new ManagedPackFile
                    {
                        Path = relative,
                        Size = new FileInfo(source).Length,
                        Sha256 = await HashFileAsync(source, token)
                    };
                }

                countDone++;
                progress.Report(($"Verificando archivos · {countDone}/{stagedFiles.Length}", .56 + .18 * countDone / Math.Max(1, stagedFiles.Length)));
            }

            foreach (var (relative, previous) in oldFiles)
            {
                if (stagedManaged.ContainsKey(relative)) continue;
                var target = SafeTargetPath(instance, relative);
                if (!File.Exists(target)) continue;
                var currentHash = await HashFileAsync(target, token);
                if (!overrides.Contains(relative) && currentHash.Equals(previous.Sha256, StringComparison.OrdinalIgnoreCase))
                    removeFiles.Add(relative);
                else
                    overrides.Add(relative);
            }

            // Keep local edits to previously managed files, especially player and mod configuration.
            var approvedCopies = new List<(string source, string relative)>();
            foreach (var copy in copyFiles)
            {
                if (oldFiles.TryGetValue(copy.relative, out var previous))
                {
                    var target = SafeTargetPath(instance, copy.relative);
                    if (File.Exists(target))
                    {
                        var currentHash = await HashFileAsync(target, token);
                        var newHash = stagedManaged.TryGetValue(copy.relative, out var managed) ? managed.Sha256 : null;
                        if (overrides.Contains(copy.relative) && newHash is not null && currentHash.Equals(newHash, StringComparison.OrdinalIgnoreCase))
                            overrides.Remove(copy.relative);
                        else if (overrides.Contains(copy.relative) || !currentHash.Equals(previous.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            overrides.Add(copy.relative);
                            continue;
                        }
                    }
                }
                approvedCopies.Add(copy);
            }

            var nextState = new ManagedPackState
            {
                PackId = pack.Slug,
                Version = pack.Version,
                Files = stagedManaged.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                Overrides = overrides.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            };
            var stateStaging = Path.Combine(workspace, ".destiny-managed-files.json.next");
            await File.WriteAllTextAsync(stateStaging, JsonSerializer.Serialize(nextState, _json), token);

            progress.Report(("Aplicando actualización segura", .78));
            var stateBackupRelative = ".destiny-managed-files.json";
            var affected = approvedCopies.Select(x => x.relative).Concat(removeFiles).Append(stateBackupRelative).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var backups = new List<(string relative, string backup, bool existed)>();
            Directory.CreateDirectory(backupRoot);
            foreach (var relative in affected)
            {
                var target = SafeTargetPath(instance, relative);
                var backup = SafeTargetPath(backupRoot, relative);
                var existed = File.Exists(target);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, true);
                }
                backups.Add((relative, backup, existed));
            }

            try
            {
                foreach (var copy in approvedCopies)
                {
                    token.ThrowIfCancellationRequested();
                    var target = SafeTargetPath(instance, copy.relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(copy.source, target, true);
                }

                foreach (var relative in removeFiles)
                {
                    var target = SafeTargetPath(instance, relative);
                    if (File.Exists(target)) File.Delete(target);
                }

                File.Copy(stateStaging, statePath + ".next", true);
                File.Move(statePath + ".next", statePath, true);
            }
            catch
            {
                foreach (var item in backups.AsEnumerable().Reverse())
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

            progress.Report(("Paquete instalado", 1));
            _log.Info($"ZIP install completed: {pack.Name} {pack.Version}; files={stagedFiles.Length}; preserved={overrides.Count}");
        }
        catch (Exception ex)
        {
            _log.Error("ZIP pack install failed", ex);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
            catch (Exception ex) { _log.Error("Could not clean ZIP staging files", ex); }
        }
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

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static bool IsManagedPath(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 1 && ManagedRoots.Contains(parts[0]) && parts.All(x => x is not "" and not "." and not "..");
    }

    private static bool IsPersonalPath(string path)
    {
        if (PersonalFiles.Contains(path)) return true;
        var root = path.Split('/')[0];
        return PersonalRoots.Contains(root);
    }

    private static string SafeTargetPath(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Una ruta del paquete sale del directorio de instancia.");
        return target;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    private static string Format(long bytes) => bytes > 1024L * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.00} GB"
        : $"{bytes / 1024d / 1024:0.0} MB";
}
