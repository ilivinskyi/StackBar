using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TaskbarGrouper.Services;

public static class NativePositioningService
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    public static void ApplyWindows11AcrylicEffects(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int cornerPref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

            int backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
        catch
        {
            // Ignore on older OS versions
        }
    }

    /// <summary>
    /// Positions a WPF window directly above (or adjacent to) the Windows taskbar near the mouse cursor,
    /// and returns the horizontal offset (in DIPs) of the cursor relative to the window's left edge for caret alignment.
    /// </summary>
    public static double PositionNearTaskbarCursorWithCaret(Window window, int? overrideCursorX = null, int? overrideCursorY = null)
    {
        GetCursorPos(out POINT cursor);
        if (overrideCursorX is > 0 && overrideCursorY is > 0)
        {
            cursor.X = overrideCursorX.Value;
            cursor.Y = overrideCursorY.Value;
        }

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
        var workArea = screen.WorkingArea;
        var screenBounds = screen.Bounds;

        var source = PresentationSource.FromVisual(window);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        double winWidthPx = window.ActualWidth * dpiX;
        double winHeightPx = window.ActualHeight * dpiY;

        const int marginPx = 6;

        double leftPx = cursor.X - (winWidthPx / 2.0);
        if (leftPx < workArea.Left + marginPx)
            leftPx = workArea.Left + marginPx;
        if (leftPx + winWidthPx > workArea.Right - marginPx)
            leftPx = workArea.Right - winWidthPx - marginPx;

        double topPx;
        if (workArea.Top > screenBounds.Top)
        {
            topPx = workArea.Top + marginPx;
        }
        else if (workArea.Left > screenBounds.Left)
        {
            leftPx = workArea.Left + marginPx;
            topPx = Math.Clamp(cursor.Y - (winHeightPx / 2.0), workArea.Top + marginPx, workArea.Bottom - winHeightPx - marginPx);
        }
        else if (workArea.Right < screenBounds.Right)
        {
            leftPx = workArea.Right - winWidthPx - marginPx;
            topPx = Math.Clamp(cursor.Y - (winHeightPx / 2.0), workArea.Top + marginPx, workArea.Bottom - winHeightPx - marginPx);
        }
        else
        {
            topPx = workArea.Bottom - winHeightPx - marginPx;
        }

        window.Left = leftPx / dpiX;
        window.Top = topPx / dpiY;

        return (cursor.X - leftPx) / dpiX;
    }
}
