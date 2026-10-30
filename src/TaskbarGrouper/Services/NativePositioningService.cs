using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using StackBar.Common;

namespace TaskbarGrouper.Services
{
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

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        public static double PositionNearTaskbarCursorWithCaret(Window window, int? overrideCursorX, int? overrideCursorY, out bool isTaskbarTop)
        {
            isTaskbarTop = false;
            POINT cursor;
            GetCursorPos(out cursor);
            if (overrideCursorX.HasValue && overrideCursorX.Value > 0 &&
                overrideCursorY.HasValue && overrideCursorY.Value > 0)
            {
                cursor.X = overrideCursorX.Value;
                cursor.Y = overrideCursorY.Value;
            }

            var screen = Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
            var workArea = screen.WorkingArea;
            var screenBounds = screen.Bounds;

            // Per-monitor DPI detection via shcore
            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;
            try
            {
                IntPtr hMon = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
                if (hMon != IntPtr.Zero && GetDpiForMonitor(hMon, 0, out uint dx, out uint dy) == 0)
                {
                    dpiScaleX = dx / 96.0;
                    dpiScaleY = dy / 96.0;
                }
            }
            catch
            {
                var source = PresentationSource.FromVisual(window);
                dpiScaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                dpiScaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            }

            double winWidthPx = window.ActualWidth * dpiScaleX;
            double winHeightPx = window.ActualHeight * dpiScaleY;

            const int marginPx = 4;

            double leftPx = cursor.X - (winWidthPx / 2.0);
            if (leftPx < workArea.Left + marginPx)
                leftPx = workArea.Left + marginPx;
            if (leftPx + winWidthPx > workArea.Right - marginPx)
                leftPx = workArea.Right - winWidthPx - marginPx;

            double topPx;
            if (workArea.Top > screenBounds.Top)
            {
                // Taskbar is at top
                isTaskbarTop = true;
                topPx = workArea.Top + marginPx;
            }
            else if (workArea.Left > screenBounds.Left)
            {
                // Left taskbar
                leftPx = workArea.Left + marginPx;
                topPx = Polyfills.Clamp(cursor.Y - (winHeightPx / 2.0), workArea.Top + marginPx, workArea.Bottom - winHeightPx - marginPx);
            }
            else if (workArea.Right < screenBounds.Right)
            {
                // Right taskbar
                leftPx = workArea.Right - winWidthPx - marginPx;
                topPx = Polyfills.Clamp(cursor.Y - (winHeightPx / 2.0), workArea.Top + marginPx, workArea.Bottom - winHeightPx - marginPx);
            }
            else
            {
                // Default: Taskbar at bottom
                topPx = workArea.Bottom - winHeightPx - marginPx;
            }

            window.Left = leftPx / dpiScaleX;
            window.Top = topPx / dpiScaleY;

            return (cursor.X - leftPx) / dpiScaleX;
        }
    }
}
