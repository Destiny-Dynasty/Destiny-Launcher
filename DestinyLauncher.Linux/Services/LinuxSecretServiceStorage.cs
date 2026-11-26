using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DestinyLauncher.Services;

namespace DestinyLauncher.Linux;

/// <summary>Stores Microsoft refresh-token data in the user's Linux Secret Service keyring.</summary>
internal sealed class LinuxSecretServiceStorage : ITransactionalJsonStorage
{
    private const string AttributeName = "destiny-launcher-account-store";
    private readonly string _account;
    private JsonNode? _pending;

    internal LinuxSecretServiceStorage(string account = "microsoft", string? scope = null)
    {
        _account = account + (scope is null ? "" : ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(scope)))));
    }

    public JsonNode? ReadAsJsonNode()
    {
        var value = RunSecretTool(["lookup", "service", AttributeName, "account", _account], null, allowMissing: true);
        if (value is null) return null;
        if (Encoding.UTF8.GetByteCount(value) > 2 * 1024 * 1024) throw new InvalidDataException("El almacén seguro de cuentas es demasiado grande.");
        return JsonNode.Parse(value);
    }

    public void Write(JsonNode node, JsonSerializerOptions? serializerOptions = null) => _pending = node.DeepClone();

    public void Commit()
    {
        if (_pending is null) throw new InvalidOperationException("No hay una sesión verificada para guardar.");
        var secret = JsonSerializer.SerializeToUtf8Bytes(_pending);
        try
        {
            RunSecretTool(["store", "--label=Destiny Launcher accounts", "service", AttributeName, "account", _account], secret, allowMissing: false);
            _pending = null;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal void Clear() => RunSecretTool(["clear", "service", AttributeName, "account", _account], null, allowMissing: true);

    private static string? RunSecretTool(IReadOnlyList<string> arguments, byte[]? input, bool allowMissing)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("secret-tool")
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new CryptographicException();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new CryptographicException("No se pudo acceder a Secret Service. Instala `libsecret-tools` y comprueba que el llavero de Ubuntu esté desbloqueado.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            process.StandardInput.BaseStream.Write(input, 0, input.Length);
            process.StandardInput.BaseStream.WriteByte((byte)'\n');
            process.StandardInput.Close();
        }
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new CryptographicException("Secret Service no respondió. Comprueba que el llavero de Ubuntu esté desbloqueado.");
        }
        var output = outputTask.GetAwaiter().GetResult();
        var errorText = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode == 0) return input is null ? output.TrimEnd('\r', '\n') : null;
        if (allowMissing && process.ExitCode == 1 && string.IsNullOrWhiteSpace(errorText)) return null;
        throw new CryptographicException("Secret Service rechazó el acceso al llavero. Comprueba que esté desbloqueado y vuelve a intentarlo.");
    }
}
