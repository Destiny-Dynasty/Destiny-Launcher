using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Serialization;
using CmlLib.Core.Auth.Microsoft.Sessions;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Accounts.JsonStorage;
using XboxAuthNet.Game.Serialization;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.OAuth.CodeFlow.Parameters;
using XboxAuthNet.XboxLive;

namespace DestinyLauncher.Services;

internal static class SisuExperiment
{
    internal static bool Enabled => true;
    internal static bool UsesIsolatedData
    {
        get
        {
#if SISU_EXPERIMENT
            return true;
#else
            return false;
#endif
        }
    }
}

internal sealed record MicrosoftProfile(string Id, string Username);

// All account material (including refresh tokens) stays in the selected data root,
// encrypted with this Windows user's DPAPI key. No default cml_accounts.json file.
internal sealed class MicrosoftAccountService
{
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string _file;
    private readonly HttpClient _http;
    private readonly Func<string, IJsonStorage> _storageFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal MicrosoftAccountService(LauncherPaths paths, Func<string, IJsonStorage> storageFactory, HttpClient? http = null)
    {
        _file = Path.Combine(paths.Root, "microsoft-accounts.bin");
        _storageFactory = storageFactory;
        _http = http ?? DefaultClient;
    }
    private static JsonXboxGameAccountManager Manager(IJsonStorage storage) => new(storage,
        JEGameAccount.FromSessionStorage, new JsonSerializerOptions(JsonXboxGameAccountManager.DefaultSerializerOption)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(MicrosoftAuthJsonContext.Default, XboxGameJsonContext.Default)
        });
    internal IReadOnlyList<MicrosoftProfile> Profiles()
    {
        return Manager(_storageFactory(_file)).GetAccounts().OfType<JEGameAccount>()
            .Where(a => a.Profile is not null).Select(a => new MicrosoftProfile(a.Identifier!, a.Profile!.Username!)).ToArray();
    }
    internal Task<MSession> SignInAsync(IWebUI web, IProgress<string>? progress, CancellationToken cancel)
        => AuthenticateAsync(null, web, null, progress, cancel);
    internal Task<MSession> SignInAsync(IWebUI web, string redirectUri, IProgress<string>? progress, CancellationToken cancel)
        => AuthenticateAsync(null, web, redirectUri, progress, cancel);
    internal Task<MSession> RefreshAsync(string id, IProgress<string>? progress, CancellationToken cancel)
        => AuthenticateAsync(id, null, null, progress, cancel);

    private async Task<MSession> AuthenticateAsync(string? id, IWebUI? web, string? redirectUri, IProgress<string>? progress, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(TimeSpan.FromMinutes(8));
            var storage = _storageFactory(_file) as ITransactionalJsonStorage
                ?? throw new InvalidOperationException("El almacén de cuentas no permite guardar transacciones seguras.");
            var manager = Manager(storage);
            if (id is null && manager.GetAccounts().Count >= 20) throw new InvalidOperationException("Elimina una cuenta de Microsoft antes de añadir otra.");
            var handler = new JELoginHandlerBuilder().WithAccountManager(manager).WithHttpClient(_http).Build();
            var account = id is null ? manager.NewAccount() : manager.GetAccounts().GetAccount(id);
            var auth = handler.CreateAuthenticator(account, deadline.Token);
            progress?.Report(web is not null ? "Inicia sesión con Microsoft…" : "Renovando sesión de Microsoft…");
            if (web is not null)
                auth.AddForceMicrosoftOAuthForJE(oauth => oauth.Interactive(builder => builder.WithWebUI(web),
                    new CodeFlowAuthorizationParameter { Prompt = "select_account", State = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), RedirectUri = redirectUri }));
            else
                auth.AddForceMicrosoftOAuthForJE(oauth => oauth.Silent());
            // Explicit SISU: never silently fall back to Basic, offline or a custom client ID.
            auth.AddForceXboxAuthForJE(xbox => xbox.Sisu(XboxGameTitles.MinecraftJava));
            auth.AddForceJEAuthenticator(je => je.WithGameOwnershipChecker().Build());
            var session = await auth.ExecuteForLauncherAsync().WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            ValidateSession(session);
            if (id is not null && !string.Equals(session.UUID, id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La cuenta recibida no coincide con la cuenta seleccionada.");
            // AccountSaver writes into the transaction; credentials only reach disk after
            // complete OAuth + SISU + Minecraft + ownership/profile validation and cancellation.
            storage.Commit();
            progress?.Report("Cuenta de Minecraft verificada");
            return session;
        }
        finally { _gate.Release(); }
    }
    internal static void ValidateSession(MSession session)
    {
        if (session.UserType != "msa" || !Models.LauncherConfig.ValidPlayerName(session.Username)
            || !Guid.TryParseExact(session.UUID, "N", out _) || string.IsNullOrWhiteSpace(session.AccessToken)
            || session.AccessToken is "null" or "0" or "offline")
            throw new InvalidDataException("Microsoft no devolvió una sesión válida de Minecraft Java.");
    }
    internal void Forget(string id) => ForgetMany([id]);
    internal void ForgetMany(IEnumerable<string> ids)
    {
        var storage = _storageFactory(_file) as ITransactionalJsonStorage
            ?? throw new InvalidOperationException("El almacén de cuentas no permite guardar transacciones seguras.");
        var node = storage.ReadAsJsonNode() as JsonObject;
        if (node is null) return;
        var requested = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var id in node.Select(item => item.Key).Where(requested.Contains).ToArray()) changed |= node.Remove(id);
        if (changed) { storage.Write(node, null); storage.Commit(); }
    }
    internal static string FriendlyError(Exception error)
    {
        if (error is OperationCanceledException) return "Inicio de sesión cancelado o agotado. Puedes volver a intentarlo.";
        if (error is JEAuthException je)
            return je.StatusCode switch
            {
                403 => "Minecraft rechazó el inicio de sesión (403). No se pudo validar la cuenta.",
                404 => "Esta cuenta no tiene un perfil de Minecraft Java disponible.",
                _ => "No se pudo verificar Minecraft Java. Comprueba que la cuenta tenga el juego y un perfil creado."
            };
        if (error is HttpRequestException or TimeoutException) return "Microsoft o Xbox no respondieron. Vuelve a intentarlo.";
        if (error is CryptographicException) return OperatingSystem.IsWindows()
            ? "No se pudieron leer las credenciales guardadas en este usuario de Windows."
            : "No se pudo acceder al almacén seguro de cuentas de Linux. Comprueba que Secret Service esté disponible.";
        if (error is KeyNotFoundException) return "La sesión guardada ya no está disponible. Inicia sesión de nuevo.";
        // Never display raw SDK messages or response bodies: they may contain tokens.
        return "No se pudo completar el inicio de sesión. Comprueba la conexión e inténtalo de nuevo.";
    }
}
