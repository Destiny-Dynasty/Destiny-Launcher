using System.Text.Json;
using CmlLib.Core.Auth;
using DestinyLauncher.Linux;
using DestinyLauncher.Models;
using XboxAuthNet.OAuth.CodeFlow;

namespace DestinyLauncher.Services;

public sealed record PackCatalogEntry(PackEdition Edition, string Name, string Version, string MinecraftVersion, int JavaVersion);
public sealed record LauncherAccountProfile(string Id, string Username);

/// <summary>Linux entry point for shared launcher settings and the public pack catalog.</summary>
public sealed class LinuxLauncherService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConfigService _config;
    private readonly GitHubPackUpdater _packUpdater;
    private readonly InstallationDataService _installations;
    private readonly AppLogger _log;
    private readonly MicrosoftAccountService _microsoft;
    private readonly LauncherPaths _paths;
    private LauncherConfig _settings;

    public LauncherPaths Paths => _paths;
    public LauncherConfig Settings => _settings;
    public bool StaffAllowed { get; private set; }

    public LinuxLauncherService(string? dataRoot = null) : this(dataRoot, null) { }

    internal LinuxLauncherService(string? dataRoot, HttpClient? packHttp)
    {
        _paths = new LauncherPaths(dataRoot);
        _paths.Ensure();
        var hasSettings = File.Exists(_paths.ConfigFile);
        _config = new ConfigService(_paths);
        _settings = _config.Load();
        if (dataRoot is not null && !hasSettings) _settings.InstallDirectory = _paths.InstallDirectory;
        _paths.SetInstallDirectory(_settings.InstallDirectory);
        _log = new AppLogger(_paths);
        _packUpdater = new GitHubPackUpdater(_paths, _log, packHttp);
        _installations = new InstallationDataService(_paths);
        _microsoft = new MicrosoftAccountService(_paths, _ => new LinuxSecretServiceStorage(scope: _paths.Root));
    }

    public async Task<IReadOnlyList<PackCatalogEntry>> RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        var packs = new List<(PackEdition Edition, PackDefinition Pack)>
        {
            (PackEdition.Destiny, new PackDefinition()),
            (PackEdition.DestinyLite, new PackDefinition { Name = "Destiny Lite", Slug = "destiny-lite" })
        };
        if (StaffAllowed) packs.Add((PackEdition.DestinyStaff, new PackDefinition { Name = "Destiny Staff", Slug = "destiny-staff" }));
        var entries = new List<PackCatalogEntry>(packs.Count);
        foreach (var (edition, pack) in packs)
        {
            using var response = await Http.GetAsync(pack.ManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<PackManifest>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
                ?? throw new InvalidDataException($"El manifiesto de {pack.Name} está vacío.");
            if (!string.Equals(manifest.PackId, pack.Slug, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(manifest.Version)
                || manifest.JavaVersion is < 17 or > 99)
                throw new InvalidDataException($"El manifiesto público de {pack.Name} no es válido.");
            entries.Add(new PackCatalogEntry(edition, pack.Name, manifest.Version, manifest.MinecraftVersion, manifest.JavaVersion));
        }
        return entries;
    }

    public void SelectEdition(PackEdition edition)
    {
        if (edition is not (PackEdition.Destiny or PackEdition.DestinyLite) && !(edition == PackEdition.DestinyStaff && StaffAllowed))
            throw new ArgumentOutOfRangeException(nameof(edition), "Esta compilación solo muestra las variantes públicas.");
        _settings.SelectedEdition = edition;
        _config.Save(_settings);
    }
    public void SetStaffAccess(bool allowed)
    {
        StaffAllowed = allowed;
        if (!allowed && _settings.SelectedEdition == PackEdition.DestinyStaff) { _settings.SelectedEdition = PackEdition.Destiny; _config.Save(_settings); }
    }

    public IReadOnlyList<LauncherAccountProfile> MicrosoftProfiles()
    {
        if (!File.Exists(Path.Combine(_paths.Root, "microsoft-accounts.bin")) && string.IsNullOrEmpty(_settings.MicrosoftAccountId)) return [];
        return _microsoft.Profiles().Select(profile => new LauncherAccountProfile(profile.Id, profile.Username)).ToArray();
    }

    public void ForgetMicrosoft(IEnumerable<string> ids) => _microsoft.ForgetMany(ids);

    public async Task<LauncherAccountProfile> SignInMicrosoftAsync(IWebUI web, string redirectUri,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, bool makeActive = true)
    {
        var session = await _microsoft.SignInAsync(web, redirectUri, progress, cancellationToken);
        File.WriteAllText(Path.Combine(_paths.Root, "microsoft-accounts.bin"), "Credential stored in Secret Service.");
        if (makeActive)
        {
            _settings.MicrosoftAccountId = session.UUID!;
            _settings.PlayerName = session.Username!;
            _config.Save(_settings);
        }
        return new LauncherAccountProfile(session.UUID!, session.Username!);
    }

    public void SignOutMicrosoft()
    {
        if (string.IsNullOrWhiteSpace(_settings.MicrosoftAccountId)) return;
        _microsoft.Forget(_settings.MicrosoftAccountId);
        _settings.MicrosoftAccountId = "";
        if (!LauncherConfig.ValidPlayerName(_settings.PlayerName)) _settings.PlayerName = "";
        _config.Save(_settings);
    }

    public void SetInstallDirectory(string? path) => _paths.SetInstallDirectory(path);

    public void RegisterExistingInstallations() => _installations.RegisterConfigured(_settings.InstallDirectory);
    public void RemoveInstance(string slug) => _installations.RemoveInstance(_paths.Instance(slug));

    public void SaveSettings() => _config.Save(_settings);

    public async Task RepairPackAsync(PackEdition edition, bool removeThirdPartyMods,
        IProgress<(string status, double value)>? progress = null, CancellationToken cancellationToken = default)
    {
        var pack = GetPack(edition);
        var manifest = await _packUpdater.ReadManifestAsync(pack, cancellationToken);
        await _packUpdater.RepairAsync(pack, manifest, removeThirdPartyMods, progress, cancellationToken);
        _config.SaveInstalled(pack.Slug, manifest.Version, manifest.JavaVersion);
    }

    public async Task<PackUpdateCheck> InstallOrUpdatePackAsync(PackEdition edition,
        IProgress<(string status, double value)>? progress = null, CancellationToken cancellationToken = default)
    {
        var pack = GetPack(edition);
        var manifest = await _packUpdater.ReadManifestAsync(pack, cancellationToken);
        var update = await _packUpdater.PrepareForPlayAsync(pack, manifest, _config.InstalledVersion(pack.Slug), progress, cancellationToken);
        await _packUpdater.ApplyAsync(pack, update, progress, cancellationToken);
        _config.SaveInstalled(pack.Slug, manifest.Version, manifest.JavaVersion);
        _installations.Register(_paths.Instance(pack.Slug));
        return update;
    }

    public async Task<MinecraftGameSession> LaunchOfflineAsync(PackEdition edition, string playerName,
        IProgress<(string status, double value)> progress, CancellationToken cancellationToken = default)
    {
        if (!LauncherConfig.ValidPlayerName(playerName))
            throw new InvalidOperationException("El nombre offline debe tener de 3 a 16 letras, números o guiones bajos.");
        if (!string.IsNullOrWhiteSpace(_settings.MicrosoftAccountId))
            throw new InvalidOperationException("Cierra la sesión Microsoft antes de iniciar un perfil offline.");

        return await LaunchCoreAsync(edition, playerName, null, progress, cancellationToken);
    }

    public async Task<MinecraftGameSession> LaunchMicrosoftAsync(PackEdition edition,
        IProgress<(string status, double value)> progress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.MicrosoftAccountId))
            throw new InvalidOperationException("Inicia sesión con Microsoft antes de jugar con una cuenta premium.");
        var session = await _microsoft.RefreshAsync(_settings.MicrosoftAccountId, null, cancellationToken);
        return await LaunchCoreAsync(edition, session.Username!, session, progress, cancellationToken);
    }

    private async Task<MinecraftGameSession> LaunchCoreAsync(PackEdition edition, string playerName, MSession? session,
        IProgress<(string status, double value)> progress, CancellationToken cancellationToken)
    {
        var instance = _paths.Instance(GetPack(edition).Slug);
        await MinecraftService.InitializeNewInstanceAsync(_paths.GameDirectory, cancellationToken);
        var update = await InstallOrUpdatePackAsync(edition, progress, cancellationToken);
        var pack = GetPack(edition);
        pack.Version = update.Manifest.Version;
        pack.JavaVersion = update.Manifest.JavaVersion;
        var config = new LauncherConfig
        {
            PlayerName = playerName,
            MicrosoftAccountId = session is null ? "" : _settings.MicrosoftAccountId,
            SelectedEdition = edition,
            InstallDirectory = _settings.InstallDirectory,
            RamGb = _settings.RamGb,
            JavaArguments = _settings.JavaArguments,
            AutoJoinServer = session is null ? false : _settings.AutoJoinServer,
            WindowMode = _settings.WindowMode,
            GameWidth = _settings.GameWidth,
            GameHeight = _settings.GameHeight,
            MinecraftVersion = update.Manifest.MinecraftVersion,
            FabricLoaderVersion = update.Manifest.FabricLoaderVersion
        };
        _settings.PlayerName = playerName;
        _config.Save(_settings);
        return await new MinecraftService(_paths, _log).PrepareAndLaunchAsync(config, pack, progress, cancellationToken, session);
    }

    private PackDefinition GetPack(PackEdition edition) => edition switch
    {
        PackEdition.Destiny => new PackDefinition(),
        PackEdition.DestinyLite => new PackDefinition { Name = "Destiny Lite", Slug = "destiny-lite" },
        PackEdition.DestinyStaff when StaffAllowed => new PackDefinition { Name = "Destiny Staff", Slug = "destiny-staff" },
        _ => throw new ArgumentOutOfRangeException(nameof(edition), "Esta compilación solo muestra las variantes públicas.")
    };

    public void Dispose() { }
}
