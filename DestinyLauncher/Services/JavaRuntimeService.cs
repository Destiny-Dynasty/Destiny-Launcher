using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace DestinyLauncher.Services;

internal sealed class JavaRuntimeException(string message, Exception inner) : IOException(message, inner);

// Private archive-based runtimes: never installs a Windows package or changes PATH.
internal sealed class JavaRuntimeService(LauncherPaths paths, AppLogger log, HttpClient? client = null)
{
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly HttpClient _http = client ?? DefaultClient;
    internal sealed record Runtime(string Sha256, string Home, DateTime CheckedUtc);

    internal async Task<string> EnsureAsync(int major, IProgress<(string status, double value)> progress, CancellationToken token)
    {
        if (major is < 17 or > 99) throw new InvalidDataException("La versión de Java del modpack no es válida.");
        await Gate.WaitAsync(token);
        try
        {
            var root = Path.Combine(paths.Cache, "java", "temurin-" + major);
            InstallationDataService.RejectLinks(root);
            Directory.CreateDirectory(root);
            var marker = Path.Combine(root, "active.json");
            Runtime? active = null;
            try
            {
                active = JsonSerializer.Deserialize<Runtime>(await File.ReadAllTextAsync(marker, token));
                if (active is null || !IsHash(active.Sha256) || !ValidHome(root, active.Home, major)) active = null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            if (active is not null && DateTime.UtcNow - active.CheckedUtc < TimeSpan.FromDays(7))
                return Path.Combine(root, active.Home, "bin", "javaw.exe");

            string? stage = null;
            string? archive = null;
            try
            {
                progress.Report(($"Consultando Java {major}", .60));
                using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                metadataTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var metadata = await _http.GetAsync($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse", metadataTimeout.Token);
                metadata.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(metadataTimeout.Token));
                var asset = json.RootElement.EnumerateArray().First();
                if (asset.GetProperty("version").GetProperty("major").GetInt32() != major) throw new InvalidDataException("Adoptium devolvió otra versión de Java.");
                var package = asset.GetProperty("binary").GetProperty("package");
                var hash = package.GetProperty("checksum").GetString()!;
                var size = package.GetProperty("size").GetInt64();
                var uri = new Uri(package.GetProperty("link").GetString()!);
                if (!IsHash(hash) || size is < 1 or > 536870912 || uri.Scheme != "https" || uri.Host != "github.com"
                    || !uri.AbsolutePath.StartsWith($"/adoptium/temurin{major}-binaries/releases/download/", StringComparison.Ordinal)
                    || !uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("La descarga de Java no pertenece a Adoptium.");
                if (active is null || !active.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    archive = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip.part");
                    using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    await using (var source = await response.Content.ReadAsStreamAsync(token))
                    await using (var target = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    using (var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[81920]; long total = 0; int read;
                        while ((read = await source.ReadAsync(buffer, token)) > 0)
                        {
                            total += read;
                            if (total > size) throw new InvalidDataException("La descarga de Java supera el tamaño esperado.");
                            checksum.AppendData(buffer, 0, read);
                            await target.WriteAsync(buffer.AsMemory(0, read), token);
                            progress.Report(($"Descargando Java {major} · {total / 1048576d:F1} / {size / 1048576d:F1} MB", .60 + .12 * total / size));
                        }
                        if (total != size || !Convert.ToHexString(checksum.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Java está incompleto o su checksum no coincide. Vuelve a intentarlo.");
                    }
                    progress.Report(($"Preparando Java {major}", .73));
                    stage = Path.Combine(root, ".extract-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stage);
                    await Task.Run(() => Extract(archive, stage, token), token);
                    var homes = Directory.GetDirectories(stage).Where(p => ValidHome(stage, Path.GetFileName(p), major)).ToArray();
                    if (homes.Length != 1) throw new InvalidDataException("El paquete no contiene un Java válido.");
                    await VerifyExecutableAsync(Path.Combine(homes[0], "bin", "java.exe"), major, token);
                    // Immutable version directories keep the working runtime intact until activation.
                    var directoryName = hash.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N")[..8];
                    var destination = Path.Combine(root, directoryName);
                    Directory.Move(homes[0], destination);
                    active = new Runtime(hash, directoryName, DateTime.UtcNow);
                    log.Info($"Installed Temurin Java {major}: {destination}");
                }
                active = active! with { CheckedUtc = DateTime.UtcNow };
                var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(active), token);
                    InstallationDataService.RejectLinks(marker);
                    File.Move(temporary, marker, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return Path.Combine(root, active.Home, "bin", "javaw.exe");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (active is not null)
            {
                log.Info($"Java {major} update unavailable; keeping installed runtime. {ex.Message}");
                return Path.Combine(root, active.Home, "bin", "javaw.exe");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            { throw new IOException($"No se pudo descargar Java {major}. Comprueba la conexión y vuelve a intentarlo.", ex); }
            finally
            {
                if (archive is not null && File.Exists(archive)) File.Delete(archive);
                if (stage is not null && Directory.Exists(stage)) Directory.Delete(stage, true);
            }
        }
        finally { Gate.Release(); }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidHome(string root, string home, int major)
    {
        if (string.IsNullOrEmpty(home) || home != Path.GetFileName(home) || home is "." or "..") return false;
        var directory = Path.Combine(root, home);
        InstallationDataService.RejectLinks(directory);
        var release = Path.Combine(directory, "release");
        if (!File.Exists(release) || !File.Exists(Path.Combine(directory, "bin", "java.exe"))
            || !File.Exists(Path.Combine(directory, "bin", "javaw.exe")) || !File.Exists(Path.Combine(directory, "bin", "server", "jvm.dll"))) return false;
        return File.ReadLines(release).Any(line => line.StartsWith($"JAVA_VERSION=\"{major}.", StringComparison.Ordinal));
    }
    internal static void Extract(string archive, string destination, CancellationToken token)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        long total = 0;
        if (zip.Entries.Count > 20000) throw new InvalidDataException("El paquete de Java contiene demasiados archivos.");
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            total += entry.Length;
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':')
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || total > 1073741824)
                throw new InvalidDataException("El paquete de Java contiene una ruta o un archivo no permitido.");
            if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target); }
        }
    }
    internal static async Task VerifyExecutableAsync(string executable, int major, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Process.Start(new ProcessStartInfo(executable, "-version")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })
            ?? throw new IOException("No se pudo comprobar Java.");
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stderr + await stdout;
            if (process.ExitCode != 0 || !output.Contains($"version \"{major}.")) throw new InvalidDataException("La versión de Java descargada no funciona correctamente.");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
