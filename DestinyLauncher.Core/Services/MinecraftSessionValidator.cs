using CmlLib.Core.Auth;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;

public static class MinecraftSessionValidator
{
    public static void Validate(MSession session)
    {
        if (session.UserType != "msa" || !LauncherConfig.ValidPlayerName(session.Username)
            || !Guid.TryParseExact(session.UUID, "N", out _) || string.IsNullOrWhiteSpace(session.AccessToken)
            || session.AccessToken is "null" or "0" or "offline")
            throw new InvalidDataException("Microsoft no devolvió una sesión válida de Minecraft Java.");
    }
}
