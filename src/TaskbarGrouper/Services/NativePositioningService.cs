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

    /// <summary>
    /// Positions a WPF window directly above (or adjacent to) the Windows taskbar near the mouse cursor,
    /// respecting multi-monitor DPI and working area bounds.
    /// </summary>
    public static void PositionNearTaskbarCursor(Window window)
    {
        GetCursorPos(out POINT cursor);

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
        var workArea = screen.WorkingArea;
        var screenBounds = screen.Bounds;

        // Obtain DPI scale for the window
        var source = PresentationSource.FromVisual(window);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        double winWidthPx = window.ActualWidth * dpiX;
        double winHeightPx = window.ActualHeight * dpiY;

        const int marginPx = 10;

        // Horizontal clamping centered on cursor X
        double leftPx = cursor.X - (winWidthPx / 2.0);
        if (leftPx < workArea.Left + marginPx)
            leftPx = workArea.Left + marginPx;
        if (leftPx + winWidthPx > workArea.Right - marginPx)
            leftPx = workArea.Right - winWidthPx - marginPx;

        // Determine taskbar edge (Bottom, Top, Left, Right)
        double topPx;
        if (workArea.Top > screenBounds.Top)
        {
            // Taskbar at Top
            topPx = workArea.Top + marginPx;
        }
        else if (workArea.Left > screenBounds.Left)
        {
            // Taskbar at Left
            leftPx = workArea.Left + marginPx;
            topPx = Math.Clamp(cursor.Y - (winHeightPx / 2.0), workArea.Top + marginPx, workArea.Bottom - winHeightPx - marginPx);
        }
        else if (workArea.Right < screenBounds.Right)
        {
            // Taskbar at Right
            leftPx = workArea.Right - winWidthPx - marginPx;
            topPx = Math.Clamp(cursor.Y - (winHeightPx / 2.0), workArea.Top + marginPx, workArea.Bottom - winHeightPx - marginPx);
        }
        else
        {
            // Default: Taskbar at Bottom
            topPx = workArea.Bottom - winHeightPx - marginPx;
        }

        window.Left = leftPx / dpiX;
        window.Top = topPx / dpiY;
    }
}
