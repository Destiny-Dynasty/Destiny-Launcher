using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DestinyLauncher.Services;

internal sealed record LauncherRelease(int Schema, string Version, string Platform, string InstallerUrl, long Size, string Sha256);

internal sealed class LauncherUpdateService : IDisposable
{
    internal const string Repository = "Destiny-Dynasty/Destiny-Launcher";
    private static readonly Uri Latest = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private readonly HttpClient _http;
    private readonly string _publicKey;
    private readonly string _cache;
    private readonly Version _current;
    private readonly string _currentExecutable;
    internal static string PublicKey()
    {
        using var stream = typeof(LauncherUpdateService).Assembly.GetManifestResourceStream("DestinyLauncher.Assets.Signing.launcher-update-public.pem")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    internal LauncherUpdateService(LauncherPaths paths) : this(paths, new HttpClient(), PublicKey(),
        typeof(LauncherUpdateService).Assembly.GetName().Version!, Environment.ProcessPath!) { }
    internal LauncherUpdateService(LauncherPaths paths, HttpClient client, string publicKey, Version current, string executable)
    {
        _http = client; _publicKey = publicKey; _current = current; _currentExecutable = executable;
        _cache = Path.Combine(paths.Root, "updates");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DestinyLauncher/" + current.ToString(3));
    }
    internal async Task<LauncherRelease?> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await _http.GetAsync(Latest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // No public release yet.
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await ReadLimitedAsync(response, 262144, timeout.Token));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString()!;
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || Normalize(version) <= Normalize(_current)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        string Asset(string name) => assets.Single(a => a.GetProperty("name").GetString() == name).GetProperty("browser_download_url").GetString()!;
        var manifestUrl = Asset("launcher-update.json"); var signatureUrl = Asset("launcher-update.sig");
        ValidateAssetUri(manifestUrl, tag, "launcher-update.json"); ValidateAssetUri(signatureUrl, tag, "launcher-update.sig");
        var manifest = await GetLimitedAsync(manifestUrl, 16384, timeout.Token);
        var signature = await GetLimitedAsync(signatureUrl, 1024, timeout.Token);
        var release = VerifyManifest(manifest, signature, _publicKey);
        if (Normalize(Version.Parse(release.Version)) != Normalize(version)) throw new InvalidDataException("La versión publicada no coincide con su firma.");
        ValidateAssetUri(release.InstallerUrl, tag, "DestinyLauncherSetup.exe");
        return release;
    }
    internal static LauncherRelease VerifyManifest(byte[] manifest, byte[] signature, string publicKey)
    {
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("La firma de la actualización no es válida.");
        var release = JsonSerializer.Deserialize<LauncherRelease>(manifest, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Actualización vacía.");
        if (release.Schema != 1 || release.Platform != "win-x64" || !Version.TryParse(release.Version, out _)
            || release.Size is < 1 or > 536870912 || release.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(release.Sha256, "^[A-Fa-f0-9]{64}$"))
            throw new InvalidDataException("Manifiesto de actualización inválido.");
        return release;
    }
    internal async Task<string> DownloadAsync(LauncherRelease release, IProgress<double> progress, CancellationToken token)
    {
        if (Normalize(Version.Parse(release.Version)) <= Normalize(_current)) throw new InvalidDataException("Se rechazó una versión anterior.");
        var uri = new Uri(release.InstallerUrl);
        ValidateAssetUri(uri.AbsoluteUri, "v" + release.Version, "DestinyLauncherSetup.exe");
        InstallationDataService.RejectLinks(_cache);
        Directory.CreateDirectory(_cache);
        var final = Path.Combine(_cache, "DestinyLauncherSetup-" + release.Version + ".exe");
        var temporary = final + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("El tamaño de la actualización no coincide.");
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidDataException("La descarga supera el tamaño firmado.");
                    hash.AppendData(buffer, 0, read); await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    progress.Report((double)total / release.Size);
                }
                if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("La actualización está incompleta o fue modificada.");
            }
            // Once the installed launcher has a trusted Windows signature, upgrades
            // must preserve its publisher as well as the mandatory release-key signature.
            var currentPublisher = Authenticode.CertificateSubject(_currentExecutable);
            if (currentPublisher is not null && (Authenticode.TrustedPublisher(_currentExecutable) != currentPublisher
                || Authenticode.TrustedPublisher(temporary) != currentPublisher))
                throw new InvalidDataException("El editor de la actualización no coincide con el launcher instalado.");
            token.ThrowIfCancellationRequested();
            InstallationDataService.RejectLinks(final); File.Move(temporary, final, true);
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static ProcessStartInfo InstallCommand(string setup, string installDirectory)
    {
        var start = new ProcessStartInfo(setup) { UseShellExecute = false };
        foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATE=1", "/AUTORESTART=1", "/UPDATEPID=" + Environment.ProcessId, "/DIR=" + installDirectory })
            start.ArgumentList.Add(arg);
        return start;
    }
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    internal static void ValidateAssetUri(string text, string tag, string asset)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != $"/{Repository}/releases/download/{tag}/{asset}")
            throw new InvalidDataException("La descarga no pertenece a la release oficial de Destiny Launcher.");
    }
    private async Task<byte[]> GetLimitedAsync(string url, int max, CancellationToken token)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode(); return await ReadLimitedAsync(response, max, token);
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("Respuesta demasiado grande.");
        await using var source = await response.Content.ReadAsStreamAsync(token); using var target = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        { if (target.Length + read > max) throw new InvalidDataException("Respuesta demasiado grande."); target.Write(buffer, 0, read); }
        return target.ToArray();
    }
    public void Dispose() => _http.Dispose();
}

internal static class Authenticode
{
    internal static string? CertificateSubject(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            // The loader API does not extract certificates from signed PE files.
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (CryptographicException) { return null; }
    }
    internal static string? TrustedPublisher(string file)
    {
        if (!File.Exists(file)) return null;
        var fileInfo = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = file };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pointer, false);
            var trust = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Union = 1, File = pointer,
                StateAction = 0, ProviderFlags = 0x1000 }; // Cache-only chain lookup; never stall launch on certificate network I/O.
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref trust) != 0) return null;
            return CertificateSubject(file);
        }
        catch (CryptographicException) { return null; }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle; public IntPtr Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    { public uint Size; public IntPtr Policy; public IntPtr Sip; public uint Ui; public uint Revocation; public uint Union; public IntPtr File; public uint StateAction; public IntPtr State; public IntPtr Url; public uint ProviderFlags; public uint Context; public IntPtr Settings; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
