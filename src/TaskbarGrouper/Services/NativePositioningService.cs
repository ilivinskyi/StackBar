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

        /// <param name="scale">Monitor DPI scale: the size limits are in physical pixels, so they grow with scaling.</param>
        public static int FindTaskbarButtonCenterX(POINT cursor, double scale = 1.0)
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
                    if (w >= 20 * scale && w <= 320 * scale && h >= 20 * scale && h <= 120 * scale)
                    {
                        return l + (w / 2);
                    }
                }
            }
            catch { }
            return cursor.X;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

        /// <summary>
        /// The taskbar window on the given monitor (primary: Shell_TrayWnd, others: Shell_SecondaryTrayWnd).
        /// Its rectangle is reported even while an auto-hidden taskbar is tucked away at the screen edge.
        /// </summary>
        private static System.Drawing.Rectangle? FindTaskbarRect(System.Drawing.Rectangle monitorBounds)
        {
            System.Drawing.Rectangle? best = null;
            int bestOverlap = 0;
            foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr hwnd = IntPtr.Zero;
                while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, cls, null)) != IntPtr.Zero)
                {
                    if (!GetWindowRect(hwnd, out RECT r)) continue;
                    var rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    var overlap = System.Drawing.Rectangle.Intersect(rect, monitorBounds);
                    int area = overlap.Width * overlap.Height;
                    if (area > bestOverlap)
                    {
                        bestOverlap = area;
                        best = rect;
                    }
                }
            }
            return best;
        }

        /// <summary>Which screen edge a taskbar rectangle sits on (a wide bar is top/bottom, a tall one left/right).</summary>
        internal static TaskbarEdge ClassifyEdge(System.Drawing.Rectangle taskbar, System.Drawing.Rectangle monitorBounds)
        {
            int taskbarCenterX = taskbar.Left + taskbar.Width / 2;
            int taskbarCenterY = taskbar.Top + taskbar.Height / 2;
            int monitorCenterX = monitorBounds.Left + monitorBounds.Width / 2;
            int monitorCenterY = monitorBounds.Top + monitorBounds.Height / 2;
            if (taskbar.Width >= taskbar.Height)
                return taskbarCenterY < monitorCenterY ? TaskbarEdge.Top : TaskbarEdge.Bottom;
            return taskbarCenterX < monitorCenterX ? TaskbarEdge.Left : TaskbarEdge.Right;
        }

        /// <summary>
        /// Area the flyout may use. With an auto-hidden taskbar the work area is the whole screen, so the
        /// taskbar's thickness is reserved explicitly (it is shown while the user clicks a pin).
        /// </summary>
        internal static System.Drawing.Rectangle UsableArea(System.Drawing.Rectangle workArea, System.Drawing.Rectangle monitorBounds,
            System.Drawing.Rectangle? taskbar, TaskbarEdge edge)
        {
            if (!taskbar.HasValue || workArea != monitorBounds) return workArea;
            var t = taskbar.Value;
            switch (edge)
            {
                case TaskbarEdge.Top: return System.Drawing.Rectangle.FromLTRB(workArea.Left, workArea.Top + t.Height, workArea.Right, workArea.Bottom);
                case TaskbarEdge.Left: return System.Drawing.Rectangle.FromLTRB(workArea.Left + t.Width, workArea.Top, workArea.Right, workArea.Bottom);
                case TaskbarEdge.Right: return System.Drawing.Rectangle.FromLTRB(workArea.Left, workArea.Top, workArea.Right - t.Width, workArea.Bottom);
                default: return System.Drawing.Rectangle.FromLTRB(workArea.Left, workArea.Top, workArea.Right, workArea.Bottom - t.Height);
            }
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

            // The real taskbar window decides the edge: comparing work area and bounds fails for auto-hidden
            // taskbars (they are equal), which used to put every flyout at the bottom
            var taskbar = FindTaskbarRect(anchor.Bounds);
            if (taskbar.HasValue)
            {
                anchor.Edge = ClassifyEdge(taskbar.Value, anchor.Bounds);
            }
            else if (anchor.WorkArea.Top > anchor.Bounds.Top) anchor.Edge = TaskbarEdge.Top;
            else if (anchor.WorkArea.Left > anchor.Bounds.Left) anchor.Edge = TaskbarEdge.Left;
            else if (anchor.WorkArea.Right < anchor.Bounds.Right) anchor.Edge = TaskbarEdge.Right;
            else anchor.Edge = TaskbarEdge.Bottom;
            anchor.WorkArea = UsableArea(anchor.WorkArea, anchor.Bounds, taskbar, anchor.Edge);

            if (anchor.Edge == TaskbarEdge.Top || anchor.Edge == TaskbarEdge.Bottom)
            {
                bool onTaskbar = taskbar.HasValue ? taskbar.Value.Contains(cursor.X, cursor.Y) : !anchor.WorkArea.Contains(cursor.X, cursor.Y);
                if (onTaskbar)
                {
                    // Snap to the clicked taskbar button's center
                    anchor.TargetCenterX = FindTaskbarButtonCenterX(cursor, GetMonitorScale(anchor.Monitor));
                }
                else if (taskbar.HasValue)
                {
                    // Opened from the keyboard (Win+number) or the Start menu: the mouse can be anywhere, so
                    // center on the taskbar instead of following the cursor
                    anchor.TargetCenterX = taskbar.Value.Left + taskbar.Value.Width / 2;
                }
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
