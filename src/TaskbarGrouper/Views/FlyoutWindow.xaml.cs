using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarGrouper.Models;
using TaskbarGrouper.Services;
using StackBar.Common;

namespace TaskbarGrouper.Views
{
    public class FlyoutItemViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));

        public string Name { get; set; } = string.Empty;
        public string ToolTipText { get; set; } = string.Empty;
        public string HotkeyHint { get; set; } = string.Empty;
        public double TileHeight { get; set; } = 82;
        public Visibility LabelVisibility { get; set; } = Visibility.Visible;
        public Visibility AdminOptionVisibility { get; set; } = Visibility.Visible;
        public Visibility FileLocationVisibility { get; set; } = Visibility.Visible;
        public TaskbarGrouper.Models.GroupItem Item { get; set; } = null!;
        public ImageSource? Icon { get; set; }

        private double _tileOpacity = 1.0;
        public double TileOpacity
        {
            get => _tileOpacity;
            set
            {
                if (_tileOpacity != value)
                {
                    _tileOpacity = value;
                    OnPropertyChanged(nameof(TileOpacity));
                }
            }
        }

        private double _tileScale = 1.0;
        public double TileScale
        {
            get => _tileScale;
            set
            {
                if (_tileScale != value)
                {
                    _tileScale = value;
                    OnPropertyChanged(nameof(TileScale));
                }
            }
        }

        private bool _isJiggling;
        public bool IsJiggling
        {
            get => _isJiggling;
            set
            {
                if (_isJiggling != value)
                {
                    _isJiggling = value;
                    OnPropertyChanged(nameof(IsJiggling));
                    OnPropertyChanged(nameof(DeleteBadgeVisibility));
                }
            }
        }

        public Visibility DeleteBadgeVisibility => IsJiggling ? Visibility.Visible : Visibility.Collapsed;
    }

    public class DragAdorner : Adorner
    {
        private readonly UIElement _child;
        private Point _location;

        public DragAdorner(UIElement adornedElement, UIElement visual) : base(adornedElement)
        {
            _child = visual;
            IsHitTestVisible = false;
            // Without this the ghost is not part of the visual tree (no layout invalidation, no inherited properties)
            AddVisualChild(_child);
        }

        public void UpdatePosition(Point point)
        {
            _location = point;
            // The adorner layer only re-reads GetDesiredTransform when asked to
            (Parent as AdornerLayer)?.Update(AdornedElement);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _child.Measure(constraint);
            return _child.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(_child.DesiredSize));
            return finalSize;
        }

        protected override Visual GetVisualChild(int index) => _child;
        protected override int VisualChildrenCount => 1;

        public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
        {
            var result = new GeneralTransformGroup();
            result.Children.Add(base.GetDesiredTransform(transform));
            result.Children.Add(new TranslateTransform(_location.X - 36, _location.Y - 36));
            return result;
        }
    }

    public partial class FlyoutWindow : Window
    {
        private const string ReorderDataFormat = "StackBar.FlyoutReorder";
        private static readonly TimeSpan ReorderAnimationDuration = TimeSpan.FromMilliseconds(160);

        private readonly AppGroup _group;
        private readonly AppConfig _config;
        private readonly ConfigService _configService;
        private readonly int? _cursorX;
        private readonly int? _cursorY;
        private readonly Action<string>? _onEditRequested;
        private readonly ObservableCollection<FlyoutItemViewModel> _viewModels = new ObservableCollection<FlyoutItemViewModel>();
        private bool _isClosing;
        private bool _isJiggleMode;
        private int _modalDepth;
        private System.Windows.Threading.DispatcherTimer? _longPressTimer;

        // Press state, recorded on mouse down; a drag only starts once the pointer actually moves
        private Point _pressStartPos;
        private FlyoutItemViewModel? _pressedVm;
        private FrameworkElement? _pressedElement;
        private bool _pressedOnInnerButton;

        // Active drag state
        private FlyoutItemViewModel? _dragVm;
        private bool _dragCancelled;
        private DateTime _reorderLockedUntil = DateTime.MinValue;

        public int Columns => Polyfills.Clamp(_group.Columns, 2, 8);

        public FlyoutWindow(AppGroup group, AppConfig config, ConfigService configService,
            int? cursorX = null, int? cursorY = null, Action<string>? onEditRequested = null)
        {
            InitializeComponent();
            _group = group;
            _config = config;
            _configService = configService;
            _cursorX = cursorX;
            _cursorY = cursorY;
            _onEditRequested = onEditRequested;
            DataContext = this;

            GroupTitleGlyph.Text = string.IsNullOrWhiteSpace(_group.IconGlyph) ? "📁" : _group.IconGlyph;
            GroupTitleText.Text = _group.Name;

            ApplyAccentTheme(_group.AccentColorHex);

            ItemsGrid.ItemsSource = _viewModels;
            PopulateViewModels();
        }

        private void PopulateViewModels()
        {
            var items = _group.Items ?? new List<TaskbarGrouper.Models.GroupItem>();
            _viewModels.Clear();
            ItemCountBadge.Text = $"{items.Count}";
            EmptyNotice.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            for (int idx = 0; idx < items.Count; idx++)
            {
                var item = items[idx];
                bool isShell = item.Kind == ItemKind.ShellApp;
                bool isFolder = item.Kind == ItemKind.Folder || Directory.Exists(item.TargetPath);

                _viewModels.Add(new FlyoutItemViewModel
                {
                    Name = item.Name,
                    ToolTipText = idx < 9 ? $"{item.Name} (Press {idx + 1})" : item.Name,
                    HotkeyHint = idx < 9 ? $"{idx + 1}" : string.Empty,
                    TileHeight = _group.ShowLabels ? 82 : 62,
                    LabelVisibility = _group.ShowLabels ? Visibility.Visible : Visibility.Collapsed,
                    AdminOptionVisibility = (isShell || isFolder) ? Visibility.Collapsed : Visibility.Visible,
                    FileLocationVisibility = isShell ? Visibility.Collapsed : Visibility.Visible,
                    Item = item,
                    Icon = IconHelper.ExtractIcon(item),
                    IsJiggling = _isJiggleMode
                });
            }
        }

        /// <summary>
        /// Saves the shared config and tells Studio, so its in-memory view stays in sync with this flyout's edits.
        /// </summary>
        private void PersistGroupChange()
        {
            _configService.Save(_config);
            ShortcutService.UpdatePinnedTaskbarShortcutForGroup(_group, _configService.AppDataDirectory);
            _group.NotifyAppearanceChanged();
            _configService.RaiseGroupChanged(_group.Id);
        }

        private MessageBoxResult ShowModal(string text, string caption, MessageBoxButton buttons, MessageBoxImage image)
        {
            // A message box takes activation; without this guard Window_Deactivated would close the flyout under it
            _modalDepth++;
            try
            {
                return MessageBox.Show(this, text, caption, buttons, image);
            }
            finally
            {
                _modalDepth--;
                if (!_isClosing && IsVisible) Activate();
            }
        }

        private void ApplyAccentTheme(string? hex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return;
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                AccentDot.Background = new SolidColorBrush(color);
                BorderAccentStop.Color = System.Windows.Media.Color.FromArgb(130, color.R, color.G, color.B);
                TopGradStop.Color = System.Windows.Media.Color.FromArgb(
                    244,
                    (byte)Math.Min(255, 24 + color.R / 7),
                    (byte)Math.Min(255, 26 + color.G / 7),
                    (byte)Math.Min(255, 36 + color.B / 7));
            }
            catch { }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var anchor = NativePositioningService.ResolveAnchor(_cursorX, _cursorY);
            var edge = anchor.Edge;

            // Caret visibility changes the window size, so set it before measuring and positioning
            TopCaretCanvas.Visibility = edge == NativePositioningService.TaskbarEdge.Top ? Visibility.Visible : Visibility.Collapsed;
            BottomCaretCanvas.Visibility = edge == NativePositioningService.TaskbarEdge.Bottom ? Visibility.Visible : Visibility.Collapsed;
            UpdateLayout();

            double caretOffsetDip = NativePositioningService.PositionWindow(this, anchor);

            // Subtract Grid Margin (14 DIPs) and polygon half-width (8 DIPs) so the arrow tip aligns with the taskbar button
            double caretCanvasX = caretOffsetDip - 14 - 8;
            double cardWidth = RootBorder.ActualWidth > 0 ? RootBorder.ActualWidth : (ActualWidth - 28);
            double clampedX = Polyfills.Clamp(caretCanvasX, 16, Math.Max(16, cardWidth - 32));
            Canvas.SetLeft(TopCaretPolygon, clampedX);
            Canvas.SetLeft(BottomCaretPolygon, clampedX);

            // The window stays invisible until it is at its final position (no flash at the old spot)
            Opacity = 1;

            Activate();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                IpcService.SetForegroundWindow(hwnd);
            }

            PlayEntranceAnimation(edge == NativePositioningService.TaskbarEdge.Top);
        }

        private void PlayEntranceAnimation(bool fromTop)
        {
            var duration = TimeSpan.FromMilliseconds(130);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            // Grow out of the taskbar side the flyout is attached to
            RootBorder.RenderTransformOrigin = fromTop ? new Point(0.5, 0.0) : new Point(0.5, 1.0);
            double slideFrom = fromTop ? -10 : 10;

            var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            RootBorder.BeginAnimation(OpacityProperty, fade);
            TopCaretCanvas.BeginAnimation(OpacityProperty, fade);
            BottomCaretCanvas.BeginAnimation(OpacityProperty, fade);
            PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = ease });
            PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, duration) { EasingFunction = ease });
            PopupTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slideFrom, 0, duration) { EasingFunction = ease });
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (!_isClosing && IsVisible && _modalDepth == 0 && _dragVm == null)
                Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            _longPressTimer?.Stop();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (GroupTitleBox.Visibility == Visibility.Visible)
                {
                    CancelRename();
                    e.Handled = true;
                    return;
                }
                if (_isJiggleMode)
                {
                    ExitJiggleMode();
                    e.Handled = true;
                    return;
                }
                Close();
                return;
            }

            if (GroupTitleBox.Visibility == Visibility.Visible) return;
            if (_isJiggleMode) return;

            int index = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9)
                index = e.Key - Key.D1;
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9)
                index = e.Key - Key.NumPad1;

            if (index >= 0 && index < _viewModels.Count)
            {
                bool asAdmin = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                LaunchItem(_viewModels[index], asAdmin);
            }
        }

        // ---------- Rename ----------

        private void GroupTitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            StartRenaming();
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            StartRenaming();
        }

        private void RenameSaveButton_Click(object sender, RoutedEventArgs e)
        {
            CommitRename();
        }

        private void RenameCancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelRename();
        }

        private void SetRenameMode(bool editing)
        {
            GroupTitleText.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            RenameButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            GroupTitleBox.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            RenameSaveButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            RenameCancelButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StartRenaming()
        {
            if (_isJiggleMode) ExitJiggleMode();
            GroupTitleBox.Text = _group.Name;
            SetRenameMode(true);
            GroupTitleBox.Focus();
            GroupTitleBox.SelectAll();
        }

        private void CommitRename()
        {
            if (GroupTitleBox.Visibility != Visibility.Visible) return;
            string newName = GroupTitleBox.Text.Trim();
            // Leave edit mode first: hiding the TextBox raises LostFocus, which calls back in here
            SetRenameMode(false);

            if (!string.IsNullOrEmpty(newName) && !newName.Equals(_group.Name, StringComparison.Ordinal))
            {
                _group.Name = newName;
                PersistGroupChange();
            }

            GroupTitleText.Text = _group.Name;
        }

        private void CancelRename()
        {
            SetRenameMode(false);
        }

        private void GroupTitleBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitRename();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelRename();
                e.Handled = true;
            }
        }

        private void GroupTitleBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitRename();
        }

        private void EditStackButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            _onEditRequested?.Invoke(_group.Id);
        }

        // ---------- Jiggle mode ----------

        private void EnterJiggleMode()
        {
            if (GroupTitleBox.Visibility == Visibility.Visible) CancelRename();
            _isJiggleMode = true;
            DoneButton.Visibility = Visibility.Visible;
            EditStackButton.Visibility = Visibility.Collapsed;
            foreach (var vm in _viewModels)
            {
                vm.IsJiggling = true;
            }
        }

        private void ExitJiggleMode()
        {
            _isJiggleMode = false;
            DoneButton.Visibility = Visibility.Collapsed;
            EditStackButton.Visibility = Visibility.Visible;
            foreach (var vm in _viewModels)
            {
                vm.IsJiggling = false;
            }
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            ExitJiggleMode();
        }

        private void ContextRearrange_Click(object sender, RoutedEventArgs e)
        {
            EnterJiggleMode();
        }

        private void ItemButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressStartPos = e.GetPosition(this);
            _pressedElement = sender as FrameworkElement;
            _pressedVm = _pressedElement?.DataContext as FlyoutItemViewModel;
            _pressedOnInnerButton = _pressedElement != null && IsInsideNestedButton(e.OriginalSource as DependencyObject, _pressedElement);

            // Never mark this event handled: the tile's delete badge must still receive its click.
            // In jiggle mode the drag starts from PreviewMouseMove once the pointer has moved.
            if (_isJiggleMode) return;

            _longPressTimer?.Stop();
            _longPressTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _longPressTimer.Tick += (s, ev) =>
            {
                _longPressTimer?.Stop();
                EnterJiggleMode();
            };
            _longPressTimer.Start();
        }

        private static bool IsInsideNestedButton(DependencyObject? source, FrameworkElement tile)
        {
            for (var current = source; current != null && current != tile; current = GetParent(current))
            {
                if (current is ButtonBase) return true;
            }
            return false;
        }

        private static DependencyObject? GetParent(DependencyObject child)
        {
            return child is Visual || child is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }

        private void ItemButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressedVm == null || !ReferenceEquals(sender, _pressedElement)) return;

            Point cur = e.GetPosition(this);
            bool moved = Math.Abs(cur.X - _pressStartPos.X) > SystemParameters.MinimumHorizontalDragDistance ||
                         Math.Abs(cur.Y - _pressStartPos.Y) > SystemParameters.MinimumVerticalDragDistance;
            if (!moved) return;

            _longPressTimer?.Stop();

            if (_isJiggleMode && !_pressedOnInnerButton && _dragVm == null && _pressedElement != null)
            {
                var element = _pressedElement;
                var vm = _pressedVm;
                _pressedVm = null;
                RunReorderDrag(element, vm);
            }
        }

        private void ItemButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _longPressTimer?.Stop();
            _pressedVm = null;
            _pressedElement = null;
        }

        // ---------- Drag & drop reordering ----------

        /// <summary>
        /// Runs the modal OLE drag. Tiles reorder live while dragging (see ItemButton_DragOver);
        /// the new order is committed only after DoDragDrop has returned, never from inside a drag callback.
        /// </summary>
        private void RunReorderDrag(FrameworkElement source, FlyoutItemViewModel vm)
        {
            _dragVm = vm;
            _dragCancelled = false;
            vm.TileOpacity = 0.35;

            var adornerLayer = AdornerLayer.GetAdornerLayer(ItemsGrid);
            DragAdorner? adorner = null;
            if (adornerLayer != null)
            {
                adorner = new DragAdorner(ItemsGrid, CreateGhostTile(vm));
                adornerLayer.Add(adorner);
                UpdateGhost(adorner);
            }

            GiveFeedbackEventHandler feedbackHandler = (s, fbe) => UpdateGhost(adorner);
            QueryContinueDragEventHandler queryHandler = (s, qe) =>
            {
                if (qe.EscapePressed) _dragCancelled = true;
                UpdateGhost(adorner);
            };

            source.GiveFeedback += feedbackHandler;
            source.QueryContinueDrag += queryHandler;
            try
            {
                // The tile button captured the mouse on mouse down; OLE runs its own capture loop
                if (Mouse.Captured != null) Mouse.Capture(null);
                DragDrop.DoDragDrop(source, new DataObject(ReorderDataFormat, vm), DragDropEffects.Move);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException)
            {
                AppLogger.Log("Flyout reorder drag failed: " + ex);
                _dragCancelled = true;
            }
            finally
            {
                source.GiveFeedback -= feedbackHandler;
                source.QueryContinueDrag -= queryHandler;
                if (adornerLayer != null && adorner != null) adornerLayer.Remove(adorner);
                vm.TileOpacity = 1.0;
                _dragVm = null;
            }

            CommitReorder();
        }

        private void CommitReorder()
        {
            var newOrder = _viewModels.Select(v => v.Item).ToList();
            if (!_dragCancelled && !newOrder.SequenceEqual(_group.Items))
            {
                _group.Items.Clear();
                _group.Items.AddRange(newOrder);
                PersistGroupChange();
            }

            // Rebuild after the OLE loop has fully unwound: refreshes hotkey hints, or restores the order on cancel
            Dispatcher.BeginInvoke(new Action(PopulateViewModels));
        }

        private UIElement CreateGhostTile(FlyoutItemViewModel vm)
        {
            var ghostImg = new Image
            {
                Source = vm.Icon,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(ghostImg, BitmapScalingMode.HighQuality);

            return new Border
            {
                Width = 72,
                Height = 72,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(220, 30, 32, 44)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(10),
                IsHitTestVisible = false,
                Child = ghostImg
            };
        }

        private void UpdateGhost(DragAdorner? adorner)
        {
            if (adorner == null) return;
            try
            {
                adorner.UpdatePosition(NativePositioningService.GetRelativeCursorPos(ItemsGrid));
            }
            catch (InvalidOperationException)
            {
                // ItemsGrid is not connected to a PresentationSource (window closing); nothing to draw
            }
        }

        private void ItemButton_DragOver(object sender, DragEventArgs e)
        {
            if (_dragVm == null || !e.Data.GetDataPresent(ReorderDataFormat)) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            if (!(sender is FrameworkElement fe) || !(fe.DataContext is FlyoutItemViewModel targetVm) || targetVm == _dragVm) return;
            // Tiles are still sliding: hit-testing them now would bounce the dragged tile back and forth
            if (DateTime.UtcNow < _reorderLockedUntil) return;

            int from = _viewModels.IndexOf(_dragVm);
            int to = _viewModels.IndexOf(targetVm);
            if (from < 0 || to < 0 || from == to) return;

            AnimateReorder(() => _viewModels.Move(from, to));
            _reorderLockedUntil = DateTime.UtcNow + ReorderAnimationDuration;
        }

        /// <summary>
        /// FLIP animation: remember every tile's position, apply the move, then slide each tile from its old
        /// position to its new one so neighbors glide out of the way instead of jumping.
        /// </summary>
        private void AnimateReorder(Action applyMove)
        {
            var before = new Dictionary<FlyoutItemViewModel, Point>();
            foreach (var vm in _viewModels)
            {
                if (ItemsGrid.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement c)
                    before[vm] = c.TranslatePoint(new Point(0, 0), ItemsGrid);
            }

            applyMove();
            ItemsGrid.UpdateLayout();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (var pair in before)
            {
                if (!(ItemsGrid.ItemContainerGenerator.ContainerFromItem(pair.Key) is FrameworkElement c)) continue;

                var translate = c.RenderTransform as TranslateTransform;
                if (translate == null)
                {
                    translate = new TranslateTransform();
                    c.RenderTransform = translate;
                }
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.X = 0;
                translate.Y = 0;

                Point after = c.TranslatePoint(new Point(0, 0), ItemsGrid);
                double dx = pair.Value.X - after.X;
                double dy = pair.Value.Y - after.Y;
                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;

                translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, ReorderAnimationDuration) { EasingFunction = ease });
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, ReorderAnimationDuration) { EasingFunction = ease });
            }
        }

        private void ItemButton_Drop(object sender, DragEventArgs e)
        {
            // The live order already reflects the drop position; RunReorderDrag commits it
            if (e.Data.GetDataPresent(ReorderDataFormat)) e.Handled = true;
        }

        private void RootBorder_DragOver(object sender, DragEventArgs e)
        {
            // Gaps between tiles still accept the drop (otherwise the cursor shows "not allowed")
            e.Effects = _dragVm != null && e.Data.GetDataPresent(ReorderDataFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void RootBorder_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
        }

        private void DeleteBadge_Click(object sender, RoutedEventArgs e)
        {
            // Stop the Click from bubbling to the tile: once the last item is gone jiggle mode ends,
            // and the tile's own Click handler would then launch the removed app.
            e.Handled = true;

            if (sender is FrameworkElement fe && fe.Tag is FlyoutItemViewModel vm)
            {
                var res = ShowModal($"Remove '{vm.Name}' from this stack?", "Remove from Stack", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    _group.Items.Remove(vm.Item);
                    ShortcutService.DeletePersistedLinkIfUnused(vm.Item, _config, _configService.AppDataDirectory);
                    PersistGroupChange();
                    if (_group.Items.Count == 0)
                        ExitJiggleMode();
                    PopulateViewModels();
                }
            }
        }

        // ---------- Launching ----------

        private void ItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isJiggleMode) return;
            if (sender is FrameworkElement fe && fe.Tag is FlyoutItemViewModel vm)
            {
                bool asAdmin = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                LaunchItem(vm, asAdmin);
            }
        }

        private void ContextLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.CommandParameter is FlyoutItemViewModel vm)
                LaunchItem(vm, asAdmin: false);
        }

        private void ContextRunAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.CommandParameter is FlyoutItemViewModel vm)
                LaunchItem(vm, asAdmin: true);
        }

        private void ContextOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.CommandParameter is FlyoutItemViewModel vm)
            {
                string target = vm.Item.TargetPath;
                if (Directory.Exists(target))
                {
                    Process.Start("explorer.exe", $"\"{target}\"");
                    Close();
                    return;
                }

                if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string? realTarget = ShortcutService.ParseShortcut(target).TargetPath;
                    if (!string.IsNullOrEmpty(realTarget) && File.Exists(realTarget))
                        target = realTarget;
                }

                if (File.Exists(target))
                {
                    Process.Start("explorer.exe", $"/select,\"{target}\"");
                    Close();
                }
            }
        }

        private void LaunchItem(FlyoutItemViewModel vm, bool asAdmin)
        {
            try
            {
                // Custom Folder: Open in Windows Explorer directly!
                if (vm.Item.Kind == ItemKind.Folder || Directory.Exists(vm.Item.TargetPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{vm.Item.TargetPath}\"",
                        UseShellExecute = true
                    });
                }
                // Shell Apps (Store/UWP)
                else if (vm.Item.Kind == ItemKind.ShellApp || vm.Item.TargetPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{vm.Item.TargetPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    string workDir = vm.Item.WorkingDirectory;
                    if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
                    {
                        workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = vm.Item.TargetPath,
                        Arguments = vm.Item.Arguments,
                        WorkingDirectory = workDir,
                        UseShellExecute = true
                    };
                    if (asAdmin)
                        psi.Verb = "runas";

                    Process.Start(psi);
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (asAdmin && ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: the user declined the UAC prompt, not an error worth reporting
            }
            catch (Exception ex)
            {
                ShowModal($"Could not launch '{vm.Name}':\n{ex.Message}", "StackBar Launch Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (_group.CloseOnLaunch && !_isClosing)
                Close();
        }
    }
}
