using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DestinyLauncher;

// Native clipping keeps rounded dialogs compatible with the WebView2 child window.
internal static class DialogWindowCorners
{
    internal static void Attach(Window window)
    {
        void Update()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds)) return;
            var dpi = VisualTreeHelper.GetDpi(window);
            var region = CreateRoundRectRgn(0, 0, bounds.Right - bounds.Left + 1, bounds.Bottom - bounds.Top + 1,
                (int)Math.Round(28 * dpi.DpiScaleX), (int)Math.Round(28 * dpi.DpiScaleY));
            if (region != IntPtr.Zero && SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
        }
        window.SourceInitialized += (_, _) => Update();
        window.SizeChanged += (_, _) => Update();
        window.DpiChanged += (_, _) => Update();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
