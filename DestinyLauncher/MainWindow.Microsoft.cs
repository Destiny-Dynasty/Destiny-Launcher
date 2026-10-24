using DestinyLauncher.Services;

namespace DestinyLauncher;

public partial class MainWindow
{
    private IReadOnlyList<MicrosoftProfile> MicrosoftProfiles()
    {
        try { return _microsoft?.Profiles() ?? []; }
        catch { return []; } // A broken credential never prevents access to local profiles.
    }
    private void SignInMicrosoft()
    {
        if (SignInMicrosoftForManager(this) is { } profile) SelectMicrosoft(profile);
    }
    private MicrosoftProfile? SignInMicrosoftForManager(System.Windows.Window owner)
    {
        if (_busy || _microsoft is null) return null;
        if (_previewMode) { LauncherDialog.Inform(owner, "Microsoft", "Inicio de sesión desactivado en la vista de prueba."); return null; }
        var dialog = new MicrosoftSignInDialog(_microsoft, _paths) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Session is { } session ? new MicrosoftProfile(session.UUID!, session.Username!) : null;
    }
    private void DiscardLinkedMicrosoft(IEnumerable<string> ids)
    {
        if (_previewMode) return;
        try { _microsoft?.ForgetMany(ids); }
        catch { SetNotice("No se pudo cerrar la sesión", "Puedes eliminar la cuenta desde Gestionar cuentas.", "Error"); }
        Refresh();
    }
    internal bool SaveManagedAccounts(IReadOnlyList<AccountRow> rows)
    {
        if (_busy || !_config.MultiAccountEnabled) return false;
        var oldNames = _config.Accounts; var oldName = _config.PlayerName; var oldId = _config.MicrosoftAccountId;
        var retained = rows.Where(a => a.IsMicrosoft).Select(a => a.MicrosoftId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profiles = MicrosoftProfiles();
        if (retained.Any(id => !profiles.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))) return false;
        _config.Accounts = rows.Where(a => !a.IsMicrosoft).Select(a => a.Name).Where(Models.LauncherConfig.ValidPlayerName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var activeRetained = !string.IsNullOrEmpty(oldId) ? retained.Contains(oldId) : _config.Accounts.Contains(oldName, StringComparer.OrdinalIgnoreCase);
        if (!activeRetained)
        {
            var next = rows.FirstOrDefault(a => !a.IsMicrosoft) ?? rows.FirstOrDefault();
            _config.MicrosoftAccountId = next?.MicrosoftId ?? ""; _config.PlayerName = next?.Name ?? "";
        }
        else if (!string.IsNullOrEmpty(oldId))
            _config.PlayerName = rows.First(a => a.MicrosoftId.Equals(oldId, StringComparison.OrdinalIgnoreCase)).Name;
        bool Restore() { _config.Accounts = oldNames; _config.PlayerName = oldName; _config.MicrosoftAccountId = oldId; return false; }
        if (!_previewMode && !TrySaveConfig()) return Restore();
        try { if (!_previewMode) _microsoft?.ForgetMany(profiles.Where(p => !retained.Contains(p.Id)).Select(p => p.Id)); }
        catch
        {
            Restore(); if (!_previewMode) TrySaveConfig(); return false;
        }
        Refresh(); return true;
    }
    private void SelectMicrosoft(MicrosoftProfile profile)
    {
        if (_busy || _microsoft is null) return;
        var previousId = _config.MicrosoftAccountId; var previousName = _config.PlayerName;
        _config.MicrosoftAccountId = profile.Id; _config.PlayerName = profile.Username;
        if (!_previewMode && !TrySaveConfig()) { _config.MicrosoftAccountId = previousId; _config.PlayerName = previousName; return; }
        Refresh();
    }
    private void SignOutMicrosoft()
    {
        if (_busy || _microsoft is null) return;
        try
        {
            _microsoft.Forget(_config.MicrosoftAccountId);
            _config.MicrosoftAccountId = ""; _config.PlayerName = _config.Accounts.FirstOrDefault() ?? "";
            if (!_previewMode) TrySaveConfig(); Refresh();
        }
        catch (Exception error) { LauncherDialog.Inform(this, "Microsoft", MicrosoftAccountService.FriendlyError(error)); }
    }
}
