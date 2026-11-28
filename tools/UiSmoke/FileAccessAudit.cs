using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using DestinyLauncher;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

internal static partial class Program
{
    private static async Task AuditFileAccessDialog()
    {
        var owner = new MainWindow(previewMode: true, dataRoot: Path.Combine(_output, "isolated-data"))
        { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        owner.Show();
        await Idle();
        var path = @"C:\Users\Jugador\AppData\Roaming\.minecraft\versions\DestinyDynasty\config\opciones-del-modpack.toml";
        var error = new ProfileConfigurationAccessException(path, new IOException("Archivo en uso."));
        string? copied = null;
        var completed = new TaskCompletionSource();
        _ = owner.Dispatcher.BeginInvoke(new Action(async () =>
        {
            FileAccessDialog? dialog = null;
            try
            {
                dialog = Application.Current.Windows.OfType<FileAccessDialog>().Single(window => window.IsVisible);
                await Task.Delay(350);
                Assert(dialog.Owner == owner && !FileAccessOwnerEnabled(new WindowInteropHelper(owner).Handle), "File access failure opens a real modal and disables its launcher owner");
                Assert(dialog.Title == ProfileFileAccessNotice.Title && Descendants<TextBlock>(dialog).Any(block => block.Text == ProfileFileAccessNotice.Instructions), "File failure dialog clearly explains the failed launch and how to retry");
                var pathBox = Descendants<TextBox>(dialog).Single(box => box.Name == "AffectedPath");
                Assert(pathBox.IsReadOnly && pathBox.Text == path && pathBox.ActualHeight <= 73, "The entire affected path remains selectable in a bounded read-only field");
                var close = Descendants<Button>(dialog).Single(button => Equals(button.Content, ProfileFileAccessNotice.Close));
                Assert(close.IsDefault && close.IsVisible, "Enter defaults to the visible acknowledgement action");
                Capture(dialog, "windows-file-access");
                var copy = Descendants<Button>(dialog).Single(button => Equals(button.Content, ProfileFileAccessNotice.Copy));
                copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(copied == path && Equals(copy.Content, ProfileFileAccessNotice.Copied), "Copy path uses the exact filename and reports success without editing any game file");
                close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
            finally { if (dialog?.IsVisible == true) dialog.Close(); }
        }));
        owner.ShowFileAccessFailure(error, value => copied = value);
        await completed.Task;
        Assert(FileAccessOwnerEnabled(new WindowInteropHelper(owner).Handle), "Acknowledging the error restores the launcher for a manual retry");

        var longPath = @"C:\Users\Jugador\" + string.Concat(Enumerable.Repeat("carpeta-larga-de-configuracion\\", 20)) + "archivo.toml";
        completed = new TaskCompletionSource();
        _ = owner.Dispatcher.BeginInvoke(new Action(async () =>
        {
            FileAccessDialog? dialog = null;
            try
            {
                dialog = Application.Current.Windows.OfType<FileAccessDialog>().Single(window => window.IsVisible);
                await Task.Delay(350);
                var pathBox = Descendants<TextBox>(dialog).Single(box => box.Name == "AffectedPath");
                Assert(pathBox.Text == longPath && pathBox.ActualHeight <= 73, "Very long paths scroll without expanding or hiding modal actions");
                var copy = Descendants<Button>(dialog).Single(button => Equals(button.Content, ProfileFileAccessNotice.Copy));
                copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(Equals(copy.Content, ProfileFileAccessNotice.CopyFailed), "A busy clipboard is handled inside the dialog without another popup");
                Capture(dialog, "windows-file-access-long-path");
                dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert(!dialog.IsVisible, "Escape closes the file access modal");
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
            finally { if (dialog?.IsVisible == true) dialog.Close(); }
        }));
        owner.ShowFileAccessFailure(new ProfileConfigurationAccessException(longPath, new IOException("Archivo en uso.")), _ => throw new InvalidOperationException("Clipboard fixture"));
        await completed.Task;
        owner.Close();
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FileAccessOwnerEnabled(IntPtr window);
}
