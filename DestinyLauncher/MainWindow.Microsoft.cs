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
        if (_busy || _microsoft is null) return;
        if (_previewMode) { LauncherDialog.Inform(this, "Microsoft", "Inicio de sesión desactivado en la vista de prueba."); return; }
        var dialog = new MicrosoftSignInDialog(_microsoft, _paths) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Session is { } session)
            SelectMicrosoft(new MicrosoftProfile(session.UUID!, session.Username!));
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
