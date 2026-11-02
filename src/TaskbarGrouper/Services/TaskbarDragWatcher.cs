using System;
using System.Collections.Generic;
using System.Threading;

namespace TaskbarGrouper.Services
{
    /// <summary>
    /// Decides when something dragged over a pinned stack should spring its flyout open (like macOS
    /// spring-loaded folders). Pure state, fed one sample per poll, so it can be tested without a mouse.
    /// </summary>
    internal sealed class DragHoverTracker
    {
        public static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(450);

        private bool _wasDown;
        private bool _ignoreThisPress;
        private string? _hoverGroupId;
        private DateTime _hoverSince;
        private bool _firedForHover;

        /// <param name="buttonDown">Primary mouse button held.</param>
        /// <param name="ignorePress">
        /// Read at the moment the button goes down: the press started on the taskbar (the user is reordering
        /// pins or clicking) or in StackBar's own windows (a flyout reorder drag), so no stack should open.
        /// </param>
        /// <param name="groupIdUnderCursor">The stack whose pin is under the cursor, or null.</param>
        /// <returns>The stack to open now, or null.</returns>
        public string? Sample(DateTime now, bool buttonDown, Func<bool> ignorePress, Func<string?> groupIdUnderCursor)
        {
            if (!buttonDown)
            {
                _wasDown = false;
                _hoverGroupId = null;
                return null;
            }

            if (!_wasDown)
            {
                _wasDown = true;
                _ignoreThisPress = ignorePress();
                _hoverGroupId = null;
            }
            if (_ignoreThisPress) return null;

            string? groupId = groupIdUnderCursor();
            if (!string.Equals(groupId, _hoverGroupId, StringComparison.OrdinalIgnoreCase))
            {
                _hoverGroupId = groupId;
                _hoverSince = now;
                _firedForHover = false;
                return null;
            }

            if (groupId == null || _firedForHover || now - _hoverSince < HoverDelay) return null;
            _firedForHover = true;
            return groupId;
        }
    }

    /// <summary>
    /// Watches for a drag (files, folders, apps) hovering over one of this StackBar's pinned stacks and
    /// raises <see cref="SpringOpen"/> on a background thread. The taskbar keeps the drop for itself, so
    /// the flyout is opened above it and the user drops there.
    /// </summary>
    internal sealed class TaskbarDragWatcher : IDisposable
    {
        private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan DragPoll = TimeSpan.FromMilliseconds(40);

        private readonly string _aumidPrefix;
        private readonly DragHoverTracker _tracker = new DragHoverTracker();
        private volatile bool _stopped;

        // Taskbar buttons read for the current drag; the taskbar's own UI Automation hit-testing stops at
        // Shell_TrayWnd, so buttons are listed once and hit-tested here
        private IReadOnlyList<TaskbarButton>? _buttons;
        private DateTime _buttonsReadAt;
        private static readonly TimeSpan ButtonsMaxAge = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// Raised with the stack id and the cursor position (physical pixels). The id comes from a pin, so the
        /// stack may since have been deleted: the handler checks.
        /// </summary>
        public event Action<string, int, int>? SpringOpen;

        public TaskbarDragWatcher(string aumidPrefix)
        {
            _aumidPrefix = aumidPrefix;
            var thread = new Thread(Run) { IsBackground = true, Name = "StackBar_DragWatcher" };
            // UI Automation clients are meant to run on MTA threads
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Dispose() => _stopped = true;

        private void Run()
        {
            while (!_stopped)
            {
                bool down = NativePositioningService.IsPrimaryButtonHeld();
                if (!down) _buttons = null;   // the next drag reads the taskbar afresh
                try
                {
                    string? open = _tracker.Sample(DateTime.UtcNow, down,
                        () => NativePositioningService.IsCursorOverTaskbar() || NativePositioningService.IsCursorOverOwnWindow(),
                        StackUnderCursor);
                    if (open != null)
                    {
                        NativePositioningService.GetCursorPos(out var pt);
                        SpringOpen?.Invoke(open, pt.X, pt.Y);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log("Taskbar drag watcher: " + ex.Message);
                }
                Thread.Sleep(down ? DragPoll : IdlePoll);
            }
        }

        private string? StackUnderCursor()
        {
            if (!NativePositioningService.IsCursorOverTaskbar()) return null;
            NativePositioningService.GetCursorPos(out var pt);

            var now = DateTime.UtcNow;
            if (_buttons == null || now - _buttonsReadAt > ButtonsMaxAge)
            {
                _buttons = ReadTaskbarButtons();
                _buttonsReadAt = now;
            }
            return TryGetGroupIdFromAumid(AppIdAt(_buttons, pt.X, pt.Y), _aumidPrefix);
        }

        internal readonly struct TaskbarButton
        {
            public TaskbarButton(System.Windows.Rect bounds, string appId)
            {
                Bounds = bounds;
                AppId = appId;
            }

            public System.Windows.Rect Bounds { get; }
            public string AppId { get; }
        }

        internal static string? AppIdAt(IEnumerable<TaskbarButton> buttons, int x, int y)
        {
            foreach (var b in buttons)
            {
                if (x >= b.Bounds.Left && x < b.Bounds.Right && y >= b.Bounds.Top && y < b.Bounds.Bottom) return b.AppId;
            }
            return null;
        }

        /// <summary>
        /// App buttons on every taskbar (all monitors). Windows 11 exposes each button's app ID as its UI
        /// Automation id, "Appid: &lt;AppUserModelID&gt;".
        /// </summary>
        private static IReadOnlyList<TaskbarButton> ReadTaskbarButtons()
        {
            var result = new List<TaskbarButton>();
            var A = System.Windows.Automation.AutomationElement.RootElement;
            var taskbars = A.FindAll(System.Windows.Automation.TreeScope.Children, new System.Windows.Automation.OrCondition(
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd")));
            var buttonCondition = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Button);

            foreach (System.Windows.Automation.AutomationElement taskbar in taskbars)
            {
                foreach (System.Windows.Automation.AutomationElement button in taskbar.FindAll(System.Windows.Automation.TreeScope.Descendants, buttonCondition))
                {
                    string? appId = ParseTaskbarAutomationId(button.Current.AutomationId);
                    if (appId != null) result.Add(new TaskbarButton(button.Current.BoundingRectangle, appId));
                }
            }
            return result;
        }

        internal static string? ParseTaskbarAutomationId(string? automationId)
        {
            const string prefix = "Appid: ";
            if (automationId == null || !automationId.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string appId = automationId.Substring(prefix.Length).Trim();
            return appId.Length > 0 ? appId : null;
        }

        /// <summary>The stack id in one of this edition's pin app IDs ("StackBar.Group.&lt;id&gt;"), or null.</summary>
        internal static string? TryGetGroupIdFromAumid(string? aumid, string aumidPrefix)
        {
            if (aumid == null || !aumid.StartsWith(aumidPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            string id = aumid.Substring(aumidPrefix.Length);
            return id.Length > 0 ? id : null;
        }
    }
}
