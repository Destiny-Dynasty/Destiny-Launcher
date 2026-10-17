using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DestinyLauncher;

/// <summary>Applies the fixed-window policy to native title-bar commands and sizing limits.</summary>
internal static class WindowWorkArea
{
    internal static void Attach(Window window)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            var command = wParam.ToInt64() & 0xFFF0;
            if ((message == 0x0112 && (command == 0xF030 || command == 0xF000)) // SC_MAXIMIZE / SC_SIZE
                || (message == 0x00A3 && wParam.ToInt64() == 2)) // double-click on HTCAPTION
            {
                handled = true;
                return IntPtr.Zero;
            }
            if (message != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO
            var monitor = MonitorFromWindow(hwnd, 2); // nearest monitor, including secondary displays
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;
            var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            bounds.MaxPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
            bounds.MaxPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
            bounds.MaxSize.X = monitorInfo.Work.Right - monitorInfo.Work.Left;
            bounds.MaxSize.Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
            var dpi = VisualTreeHelper.GetDpi(window);
            bounds.MinTrackSize.X = (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX);
            bounds.MinTrackSize.Y = (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY);
            if (window.MinWidth == window.MaxWidth && window.MinHeight == window.MaxHeight)
            {
                bounds.MaxTrackSize = bounds.MinTrackSize;
                bounds.MaxSize = bounds.MinTrackSize;
            }
            Marshal.StructureToPtr(bounds, lParam, false);
            handled = true;
            return IntPtr.Zero;
        });
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public int Flags;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
