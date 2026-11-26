using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DestinyLauncher.Services;

internal sealed class JavaRuntimeException(string message, Exception inner) : IOException(message, inner);

// Private archive-based runtimes: never installs an OS package or changes PATH.
internal sealed class JavaRuntimeService(LauncherPaths paths, AppLogger log, HttpClient? client = null)
{
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly HttpClient _http = client ?? DefaultClient;
    internal sealed record Runtime(string Sha256, string Home, DateTime CheckedUtc);

    internal async Task<string> EnsureAsync(int major, IProgress<(string status, double value)> progress, CancellationToken token)
    {
        if (major is < 17 or > 99) throw new InvalidDataException("La versión de Java del modpack no es válida.");
        var isLinux = OperatingSystem.IsLinux();
        if (!isLinux && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("El runtime gestionado de Java solo está configurado para Windows y Linux.");
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "aarch64",
            _ => throw new PlatformNotSupportedException("El launcher requiere un runtime de Java x64 o ARM64.")
        };
        var os = isLinux ? "linux" : "windows";
        var archiveExtension = isLinux ? ".tar.gz" : ".zip";
        var javaRelativePath = Path.Combine("bin", isLinux ? "java" : "javaw.exe");
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
                return Path.Combine(root, active.Home, javaRelativePath);

            string? stage = null;
            string? archive = null;
            try
            {
                progress.Report(($"Consultando Java {major}", .60));
                using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                metadataTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var metadata = await _http.GetAsync($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture={architecture}&image_type=jre&os={os}&vendor=eclipse", metadataTimeout.Token);
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
                    || !uri.AbsolutePath.EndsWith(archiveExtension, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("La descarga de Java no pertenece a Adoptium.");
                if (active is null || !active.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    archive = Path.Combine(root, Guid.NewGuid().ToString("N") + archiveExtension + ".part");
                    using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    await using (var source = await response.Content.ReadAsStreamAsync(token))
                    await using (var target = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, true))
                    using (var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[64 * 1024]; long total = 0; int read;
                        var lastProgress = Environment.TickCount64;
                        while ((read = await source.ReadAsync(buffer, token)) > 0)
                        {
                            total += read;
                            if (total > size) throw new InvalidDataException("La descarga de Java supera el tamaño esperado.");
                            checksum.AppendData(buffer, 0, read);
                            await target.WriteAsync(buffer.AsMemory(0, read), token);
                            if (total == size || Environment.TickCount64 - lastProgress >= 120)
                            {
                                progress.Report(($"Descargando Java {major} · {total / 1048576d:F1} / {size / 1048576d:F1} MB", .60 + .12 * total / size));
                                lastProgress = Environment.TickCount64;
                            }
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
                    await VerifyExecutableAsync(Path.Combine(homes[0], javaRelativePath), major, token);
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
                return Path.Combine(root, active.Home, javaRelativePath);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (active is not null)
            {
                log.Info($"Java {major} update unavailable; keeping installed runtime. {ex.Message}");
                return Path.Combine(root, active.Home, javaRelativePath);
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
        var linux = OperatingSystem.IsLinux();
        var java = Path.Combine(directory, "bin", linux ? "java" : "javaw.exe");
        var jvm = linux ? Path.Combine(directory, "lib", "server", "libjvm.so") : Path.Combine(directory, "bin", "server", "jvm.dll");
        if (!File.Exists(release) || !File.Exists(java) || !File.Exists(jvm)) return false;
        return File.ReadLines(release).Any(line => line.StartsWith($"JAVA_VERSION=\"{major}.", StringComparison.Ordinal));
    }
    internal static void Extract(string archive, string destination, CancellationToken token)
    {
        if (OperatingSystem.IsLinux()) { ExtractTarGz(archive, destination, token); return; }
        ExtractZip(archive, destination, token);
    }

    private static void ExtractZip(string archive, string destination, CancellationToken token)
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
            if (!target.StartsWith(root, FileSystemPathPolicy.Comparison) || entry.FullName.Contains(':')
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || total > 1073741824)
                throw new InvalidDataException("El paquete de Java contiene una ruta o un archivo no permitido.");
            if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target); }
        }
    }

    private static void ExtractTarGz(string archive, string destination, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Los archivos tar.gz de Java solo se extraen en Linux.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        long totalBytes = 0;
        var entries = 0;
        var buffer = new byte[64 * 1024];
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            token.ThrowIfCancellationRequested();
            if (++entries > 100000) throw new InvalidDataException("El paquete de Java contiene demasiados archivos.");
            var target = TarTarget(root, rootPrefix, entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    EnsureDirectory(root, target);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    totalBytes = checked(totalBytes + entry.Length);
                    if (entry.Length < 0 || totalBytes > 1073741824) throw new InvalidDataException("El paquete de Java supera el tamaño permitido.");
                    EnsureParentDirectory(root, target);
                    if (entry.DataStream is null) throw new InvalidDataException("El paquete de Java contiene un archivo sin datos.");
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        int read;
                        while ((read = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                        }
                        if (output.Length != entry.Length) throw new InvalidDataException("Un archivo del paquete de Java tiene un tamaño incorrecto.");
                    }
                    var mode = entry.Mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    File.SetUnixFileMode(target, mode);
                    break;
                case TarEntryType.SymbolicLink:
                    EnsureParentDirectory(root, target);
                    var linkName = entry.LinkName;
                    if (string.IsNullOrWhiteSpace(linkName) || Path.IsPathFullyQualified(linkName))
                        throw new InvalidDataException("El paquete de Java contiene un enlace inseguro.");
                    var resolvedLink = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, linkName));
                    if (!resolvedLink.StartsWith(rootPrefix, FileSystemPathPolicy.Comparison) && !resolvedLink.Equals(root, FileSystemPathPolicy.Comparison))
                        throw new InvalidDataException("El paquete de Java contiene un enlace fuera de su carpeta.");
                    File.CreateSymbolicLink(target, linkName);
                    break;
                default:
                    throw new InvalidDataException("El paquete de Java contiene un tipo de archivo no permitido.");
            }
        }
    }

    private static string TarTarget(string root, string rootPrefix, string entryName)
    {
        var relative = entryName.Replace('\\', '/').TrimEnd('/');
        var parts = relative.Split('/');
        if (relative.StartsWith('/') || relative.Contains(':') || relative.Contains('\0') || parts.Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("El paquete de Java contiene una ruta no permitida.");
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(rootPrefix, FileSystemPathPolicy.Comparison)) throw new InvalidDataException("Una ruta del paquete de Java sale de su carpeta.");
        return target;
    }

    private static void EnsureParentDirectory(string root, string target) => EnsureDirectory(root, Path.GetDirectoryName(target)!);

    private static void EnsureDirectory(string root, string directory)
    {
        var relative = Path.GetRelativePath(root, directory);
        var current = root;
        if (relative == ".") return;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..") throw new InvalidDataException("El paquete de Java contiene una carpeta no permitida.");
            current = Path.Combine(current, component);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("El paquete de Java intenta escribir a través de un enlace.");
            if (File.Exists(current)) throw new InvalidDataException("El paquete de Java contiene rutas que colisionan.");
            Directory.CreateDirectory(current);
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
