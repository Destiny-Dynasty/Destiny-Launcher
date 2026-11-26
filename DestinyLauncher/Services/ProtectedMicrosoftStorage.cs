using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxAuthNet.Game.Accounts.JsonStorage;

namespace DestinyLauncher.Services;

internal sealed class ProtectedMicrosoftStorage(string file) : ITransactionalJsonStorage
{
    private JsonNode? _pending;

    public JsonNode? ReadAsJsonNode()
    {
        InstallationDataService.RejectLinks(file);
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("Almacén de cuentas inválido.");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
        try { return JsonNode.Parse(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Write(JsonNode node, JsonSerializerOptions? serializerOptions) => _pending = node.DeepClone();

    public void Commit()
    {
        if (_pending is null) throw new InvalidOperationException("No hay una sesión verificada para guardar.");
        InstallationDataService.RejectLinks(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(_pending);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, file, true);
            _pending = null;
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
