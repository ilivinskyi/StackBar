using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
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

        public static Point GetRelativeCursorPos(Visual visual)
        {
            POINT pt;
            GetCursorPos(out pt);
            return visual.PointFromScreen(new Point(pt.X, pt.Y));
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromPoint(POINT pt, [MarshalAs(UnmanagedType.Interface)] out object ppvObject, [In, Out, MarshalAs(UnmanagedType.Struct)] ref object pvarChild);

        [ComImport]
        [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
        [InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IAccessible
        {
            [DispId(-5000)]
            void get_accParent(out object ppdispParent);
            [DispId(-5001)]
            int get_accChildCount();
            [DispId(-5002)]
            void get_accChild(object varChild, out object ppdispChild);
            [DispId(-5003)]
            string get_accName(object varChild);
            [DispId(-5004)]
            string get_accValue(object varChild);
            [DispId(-5005)]
            string get_accDescription(object varChild);
            [DispId(-5006)]
            object get_accRole(object varChild);
            [DispId(-5007)]
            object get_accState(object varChild);
            [DispId(-5008)]
            string get_accHelp(object varChild);
            [DispId(-5009)]
            int get_accHelpTopic(out string pszHelpFile, object varChild);
            [DispId(-5010)]
            string get_accKeyboardShortcut(object varChild);
            [DispId(-5011)]
            object get_accFocus();
            [DispId(-5012)]
            object get_accSelection();
            [DispId(-5013)]
            string get_accDefaultAction(object varChild);
            [DispId(-5014)]
            void accSelect(int flagsSelect, object varChild);
            [DispId(-5015)]
            void accLocation(out int pxLeft, out int pyTop, out int cxWidth, out int cyHeight, [In, MarshalAs(UnmanagedType.Struct)] object varChild);
        }

        public static int FindTaskbarButtonCenterX(POINT cursor)
        {
            try
            {
                object childId = 0;
                object accObj;
                int hr = AccessibleObjectFromPoint(cursor, out accObj, ref childId);
                if (hr == 0 && accObj is IAccessible acc)
                {
                    int l, t, w, h;
                    acc.accLocation(out l, out t, out w, out h, childId);
                    if (w >= 20 && w <= 320 && h >= 20 && h <= 120)
                    {
                        return l + (w / 2);
                    }
                }
            }
            catch { }
            return cursor.X;
        }

        public enum TaskbarEdge
        {
            Bottom,
            Top,
            Left,
            Right
        }

        /// <summary>Where a flyout should appear: the clicked point, the taskbar button center and the monitor.</summary>
        public sealed class FlyoutAnchor
        {
            public POINT Cursor;
            public int TargetCenterX;
            public System.Drawing.Rectangle WorkArea;
            public System.Drawing.Rectangle Bounds;
            public TaskbarEdge Edge;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        public static FlyoutAnchor ResolveAnchor(int? overrideCursorX, int? overrideCursorY)
        {
            GetCursorPos(out POINT cursor);
            // Monitors left of / above the primary have negative coordinates, so only "no value" means "not given"
            if (overrideCursorX.HasValue && overrideCursorY.HasValue)
            {
                cursor.X = overrideCursorX.Value;
                cursor.Y = overrideCursorY.Value;
            }

            var screen = Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
            var anchor = new FlyoutAnchor
            {
                Cursor = cursor,
                TargetCenterX = cursor.X,
                WorkArea = screen.WorkingArea,
                Bounds = screen.Bounds,
                Monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST)
            };

            if (anchor.WorkArea.Top > anchor.Bounds.Top) anchor.Edge = TaskbarEdge.Top;
            else if (anchor.WorkArea.Left > anchor.Bounds.Left) anchor.Edge = TaskbarEdge.Left;
            else if (anchor.WorkArea.Right < anchor.Bounds.Right) anchor.Edge = TaskbarEdge.Right;
            else anchor.Edge = TaskbarEdge.Bottom;

            // Snap to the taskbar button's center only when the click really was on the taskbar
            bool onTaskbar = !anchor.WorkArea.Contains(cursor.X, cursor.Y);
            if (onTaskbar && (anchor.Edge == TaskbarEdge.Top || anchor.Edge == TaskbarEdge.Bottom))
            {
                anchor.TargetCenterX = FindTaskbarButtonCenterX(cursor);
            }

            return anchor;
        }

        private static double GetMonitorScale(IntPtr monitor)
        {
            try
            {
                if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dx, out _) == 0)
                {
                    return dx / 96.0;
                }
            }
            catch { }
            return 1.0;
        }

        /// <summary>
        /// Places the window next to the taskbar in physical pixels.
        /// WPF converts Left/Top with the DPI of the monitor the window is currently on, which is wrong on
        /// mixed-DPI setups, so the window is first moved onto the target monitor (WPF then applies that
        /// monitor's DPI and re-sizes the content) and then positioned with its real pixel size.
        /// </summary>
        /// <returns>The caret tip's offset from the window's left edge, in DIPs.</returns>
        public static double PositionWindow(Window window, FlyoutAnchor anchor)
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;

            SetWindowPos(hwnd, IntPtr.Zero, anchor.WorkArea.Left, anchor.WorkArea.Top, 0, 0, flags);
            window.UpdateLayout();

            GetWindowRect(hwnd, out RECT rect);
            int winWidthPx = rect.Right - rect.Left;
            int winHeightPx = rect.Bottom - rect.Top;
            double scale = GetMonitorScale(anchor.Monitor);

            const int marginPx = 4;
            var work = anchor.WorkArea;
            double minLeft = work.Left + marginPx;
            double maxLeft = Math.Max(minLeft, work.Right - winWidthPx - marginPx);
            double minTop = work.Top + marginPx;
            double maxTop = Math.Max(minTop, work.Bottom - winHeightPx - marginPx);

            double leftPx;
            double topPx;
            switch (anchor.Edge)
            {
                case TaskbarEdge.Top:
                    leftPx = Polyfills.Clamp(anchor.TargetCenterX - winWidthPx / 2.0, minLeft, maxLeft);
                    topPx = minTop;
                    break;
                case TaskbarEdge.Left:
                    leftPx = minLeft;
                    topPx = Polyfills.Clamp(anchor.Cursor.Y - winHeightPx / 2.0, minTop, maxTop);
                    break;
                case TaskbarEdge.Right:
                    leftPx = maxLeft;
                    topPx = Polyfills.Clamp(anchor.Cursor.Y - winHeightPx / 2.0, minTop, maxTop);
                    break;
                default:
                    leftPx = Polyfills.Clamp(anchor.TargetCenterX - winWidthPx / 2.0, minLeft, maxLeft);
                    topPx = maxTop;
                    break;
            }

            SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(leftPx), (int)Math.Round(topPx), 0, 0, flags);
            return (anchor.TargetCenterX - Math.Round(leftPx)) / scale;
        }
    }
}
