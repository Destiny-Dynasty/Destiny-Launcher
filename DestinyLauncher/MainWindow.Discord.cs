using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher;

public partial class MainWindow
{
    private bool _discordChecking;
    private async Task<bool> RefreshDiscordAsync()
    {
        if (_discordChecking) return false;
        _discordChecking = true;
        try { await _discord.RefreshAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception error) { if (!_closed) SetNotice("Discord no disponible", DiscordLinkService.FriendlyError(error), "Error"); }
        finally { _discordChecking = false; }
        if (_closed) return false;
        if (_discord.Access?.Staff != true && _config.SelectedEdition == PackEdition.DestinyStaff)
        {
            _config.SelectedEdition = PackEdition.Destiny;
            _manifestCheck = null; _manifestError = null; _editionChecks.Remove(PackEdition.DestinyStaff);
            if (!_previewMode) TrySaveConfig();
        }
        Refresh(); return _discord.Access?.Staff == true;
    }
    private string DiscordStatus => _discord.Access is { } access ? access.Username + (access.Staff ? " · Staff" : "")
        : _discord.IsLinked ? "Sin verificar" : "Sin vincular";
    private async Task ManageDiscordAsync()
    {
        if (_previewMode) { LauncherDialog.Inform(this, "Discord", "Vinculación desactivada en la vista de prueba."); return; }
        if (_discord.IsLinked)
        {
            if (!LauncherDialog.Confirm(this, "Desvincular Discord", "¿Desvincular esta cuenta de Discord?", "Desvincular", "Cancelar")) return;
            try { await _discord.UnlinkAsync(_lifetime.Token); }
            catch (Exception error) { LauncherDialog.Inform(this, "Discord", DiscordLinkService.FriendlyError(error)); return; }
            await RefreshDiscordAsync();
        }
        else
        {
            var dialog = new DiscordLinkDialog(_discord, OpenExternalLink) { Owner = this };
            if (dialog.ShowDialog() == true) Refresh();
        }
    }
}
