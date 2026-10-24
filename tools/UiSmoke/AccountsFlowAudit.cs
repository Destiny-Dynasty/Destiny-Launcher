using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DestinyLauncher;
using DestinyLauncher.Services;

internal static partial class Program
{
    private static async Task AuditAccountFlow()
    {
        var names = new[] { "First_Player", "Second_Player" };
        var originalMicrosoft = new[] { new MicrosoftProfile(new string('a', 32), "Premium_Player") };
        var added = new MicrosoftProfile(new string('b', 32), "First_Player");
        var discarded = new List<string>(); var saved = false; var signIns = 0; Window? signInOwner = null;
        var manager = new AccountsDialog(names, "First_Player", _ => { saved = true; return true; }, microsoft: originalMicrosoft,
            signIn: owner => { signIns++; signInOwner = owner; return added; }, discardLinked: ids => discarded.AddRange(ids))
        { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        var script = manager.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Idle();
                Assert(!Descendants<TextBox>(manager.Editor).Any(), "Account manager opens without an inline text field");
                var add = Descendants<Button>(manager.Editor).Single(b => Equals(b.Content, "Añadir cuenta"));
                var fill = manager.Dispatcher.InvokeAsync(async () =>
                {
                    var choice = Application.Current.Windows.OfType<AddAccountDialog>().Single(w => w.IsVisible);
                    try
                    {
                        await Task.Delay(350);
                        Assert(choice.Owner == manager, "Add-account dialog belongs to the account manager");
                        var offline = Descendants<Button>(choice).Single(b => AutomationProperties.GetAutomationId(b) == "ChooseOfflineAccount");
                        var microsoft = Descendants<Button>(choice).Single(b => AutomationProperties.GetAutomationId(b) == "ChooseMicrosoftAccount");
                        Assert(offline.IsVisible && microsoft.IsVisible && !Descendants<TextBox>(choice).Any(t => t.IsVisible), "First step offers Offline and Microsoft only");
                        Assert(Descendants<Image>(microsoft).Any(i => ReferenceEquals(i.Source, MicrosoftBrand.Symbol)), "Microsoft choice uses the official vector symbol");
                        Capture(choice, "account-type-choice");
                        offline.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Idle();
                        var input = Descendants<TextBox>(choice).Single();
                        var confirm = Descendants<Button>(choice).Single(b => AutomationProperties.GetAutomationId(b) == "ConfirmOfflineAccount");
                        Assert(input.IsVisible && !microsoft.IsVisible, "Only Offline reveals the nickname field");
                        input.Text = "ab"; confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert(choice.IsVisible && Descendants<TextBlock>(choice).Any(t => t.IsVisible && t.Text.Contains("3 a 16")), "Too-short names cannot leave the add dialog");
                        input.Text = "first_player"; confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert(choice.IsVisible && Descendants<TextBlock>(choice).Any(t => t.IsVisible && t.Text.Contains("ya está guardada")), "Existing local names are rejected case-insensitively");
                        input.Text = "Third_Player"; await Idle();
                        Assert(Descendants<FrameworkElement>(input).Single(e => e.GetType().Name == "TextBoxView").ActualHeight >= 16, "Nickname input keeps a full visible line");
                        Capture(choice, "account-add-offline");
                        confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                    finally { if (choice.IsVisible) choice.Close(); }
                });
                add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await fill.Task.Unwrap();
                Assert(manager.Editor.AccountNames.Contains("Third_Player") && names.Length == 2, "New local account changes only the draft");
                var chooseMicrosoft = manager.Dispatcher.InvokeAsync(() =>
                {
                    var choice = Application.Current.Windows.OfType<AddAccountDialog>().Single(w => w.IsVisible);
                    Descendants<Button>(choice).Single(b => AutomationProperties.GetAutomationId(b) == "ChooseMicrosoftAccount").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                });
                add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await chooseMicrosoft;
                Assert(signIns == 1 && ReferenceEquals(signInOwner, manager), "Microsoft choice invokes the sign-in flow owned by the manager");
                Assert(manager.Editor.Rows.Count(r => r.Name == "First_Player") == 2 && manager.Editor.Rows.Any(r => r.MicrosoftId == added.Id), "Local and premium accounts with the same name are distinct rows");
                var list = Descendants<ListBox>(manager.Editor).Single();
                list.SelectedItem = manager.Editor.Rows.Single(r => r.MicrosoftId == originalMicrosoft[0].Id);
                Descendants<Button>(manager.Editor).Single(b => Equals(b.Content, "Eliminar")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(discarded.Count == 0 && originalMicrosoft.Length == 1, "Deleting a pre-existing Microsoft row does not sign it out before Save");
                await Idle(); Capture(manager, "accounts-manager");
                var beforeCancel = manager.Editor.Rows.Count;
                var cancelChoice = manager.Dispatcher.InvokeAsync(() =>
                {
                    var choice = Application.Current.Windows.OfType<AddAccountDialog>().Single(w => w.IsVisible);
                    Descendants<Button>(choice).Single(b => Equals(b.Content, "Cancelar")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                });
                add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await cancelChoice;
                Assert(manager.Editor.Rows.Count == beforeCancel, "Canceling the type chooser changes no draft accounts");
                Descendants<Button>(manager.Editor).Single(b => Equals(b.Content, "Cancelar")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            finally { if (manager.IsVisible) manager.Close(); }
        });
        manager.ShowDialog(); await script.Task.Unwrap();
        Assert(!saved && discarded.SequenceEqual(new[] { added.Id }), "Cancel discards only newly linked Microsoft credentials and preserves previous accounts");

        IReadOnlyList<AccountRow>? accepted = null; var cleanupAfterSave = false;
        var commit = new AccountsDialog(names, "First_Player", rows => { accepted = rows; return true; }, microsoft: originalMicrosoft,
            discardLinked: _ => cleanupAfterSave = true)
        { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        var saveScript = commit.Dispatcher.InvokeAsync(() =>
        {
            commit.Editor.AddLocal("New_Player");
            Descendants<Button>(commit.Editor).Single(b => Equals(b.Content, "Guardar")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        });
        Assert(commit.ShowDialog() == true, "Save closes the account manager successfully"); await saveScript;
        Assert(accepted?.Count == 4 && accepted.Any(a => a.IsMicrosoft) && !cleanupAfterSave, "Save preserves both account types and keeps accepted credentials");
    }
}
