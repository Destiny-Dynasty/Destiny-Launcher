using XboxAuthNet.Game.Accounts.JsonStorage;

namespace DestinyLauncher.Services;

internal interface ITransactionalJsonStorage : IJsonStorage
{
    void Commit();
}
