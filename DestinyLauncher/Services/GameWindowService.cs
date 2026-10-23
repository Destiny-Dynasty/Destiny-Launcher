using System.Diagnostics;
using System.Runtime.InteropServices;
using DestinyLauncher.Models;

namespace DestinyLauncher.Services;

internal static class GameWindowService
{
    internal static async Task<bool> WaitForWindowAsync(Process process, GameWindowMode mode, CancellationToken token)
    {
        // This short-lived watcher ends at the first real window or process exit.
        // During the game, lifetime tracking continues through the OS exit event only.
        var timer = Stopwatch.StartNew();
        while (!process.HasExited && timer.Elapsed < TimeSpan.FromMinutes(3))
        {
            token.ThrowIfCancellationRequested(); process.Refresh();
            var window = process.MainWindowHandle;
            if (window != IntPtr.Zero && IsWindowVisible(window))
            {
                if (mode == GameWindowMode.Maximized)
                {
                    // GLFW finishes showing/sizing its window after the HWND becomes
                    // visible and can undo an immediate maximize. Let it settle first,
                    // then confirm a stable maximize during this bounded startup only.
                    await Task.Delay(500, token);
                    var settling = Stopwatch.StartNew();
                    var stable = Stopwatch.StartNew();
                    while (!process.HasExited && settling.Elapsed < TimeSpan.FromSeconds(5))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!IsZoomed(window)) { ShowWindowAsync(window, 3); stable.Restart(); }
                        else if (stable.Elapsed >= TimeSpan.FromSeconds(1)) break;
                        await Task.Delay(100, token);
                    }
                }
                return true;
            }
            await Task.Delay(100, token);
        }
        return false;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr window, int command);
}
