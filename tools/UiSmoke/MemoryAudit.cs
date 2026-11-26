using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using DestinyLauncher;

internal static partial class Program
{
    private static async Task AuditClosedWindows()
    {
        var windows = Enumerable.Range(0, 8).Select(CreateClosedWindow).ToArray();
        await Idle();
        // Collection is only used to detect retained roots in this leak regression,
        // never in the launcher or in the memory measurements above.
        for (var attempt = 0; attempt < 3 && windows.Any(window => window.IsAlive); attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Idle();
        }
        Assert(windows.All(window => !window.IsAlive), "Closed launcher windows are collectable after queued UI work drains");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateClosedWindow(int index)
    {
        var window = new MainWindow(previewMode: true, dataRoot: Path.Combine(_output, "closed-" + index))
        { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        window.Show(); window.UpdateLayout(); window.Close();
        return new WeakReference(window);
    }

    private static async Task AuditMemory()
    {
        // Reflection permits running this exact harness against the pre-change assemblies.
        // The original eager loader remains available to the pixel-verification tests.
        var create = typeof(IntroWindow).GetMethod("CreateAsync", BindingFlags.Static | BindingFlags.NonPublic);
        var intro = create is null ? new IntroWindow(await IntroWindow.LoadFramesAsync())
            : await (Task<IntroWindow>)create.Invoke(null, null)!;
        intro.ShowActivated = false; intro.ShowInTaskbar = false;
        intro.WindowStartupLocation = WindowStartupLocation.Manual; intro.Left = intro.Top = -32000;
        var samples = new List<object>();
        using var self = Process.GetCurrentProcess();
        void Sample(string phase)
        {
            self.Refresh();
            samples.Add(new { phase, workingSetMiB = self.WorkingSet64 / 1048576d,
                privateMiB = self.PrivateMemorySize64 / 1048576d, managedMiB = GC.GetTotalMemory(false) / 1048576d,
                allocatedMiB = GC.GetTotalAllocatedBytes() / 1048576d, handles = self.HandleCount });
        }
        var playback = intro.PlayAsync();
        Sample("intro-start");
        var window = new MainWindow(previewMode: true, dataRoot: Path.Combine(_output, "memory-data"))
        { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        await playback;
        window.Show(); intro.Close(); intro = null!;
        Sample("home-start");
        await Task.Delay(3000); Sample("idle-3s");
        await Task.Delay(7000); Sample("idle-10s");
        window.Close(); window = null!;
        File.WriteAllText(Path.Combine(_output, "memory.json"), JsonSerializer.Serialize(new
        {
            streamingIntro = create is not null,
            note = "Same isolated offscreen WPF host, original artwork, no network/video, no forced GC or working-set trimming. Not a published-launcher or Linux measurement.",
            samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert(true, "Memory sampling completed without forced collection or working-set trimming");
    }
}
