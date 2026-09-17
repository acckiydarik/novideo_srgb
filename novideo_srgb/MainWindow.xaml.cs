using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using Application = System.Windows.Application;
using MessageBox = System.Windows.Forms.MessageBox;

namespace novideo_srgb
{
    public partial class MainWindow
    {
        private readonly MainViewModel _viewModel;
        private bool _exitRequested;
        private bool _hideToTrayPending;

        // Last state the window was actually shown in. Needed because WindowState is
        // Minimized while hidden in the tray (and while merely minimized to the taskbar), so
        // neither restoring from the tray nor saving "was it maximized" can rely on the
        // instantaneous WindowState.
        private WindowState _lastNonMinimizedState = WindowState.Normal;

        // False until the window has actually been displayed at least once. With -minimize the
        // window is hidden before it is ever shown, and RestoreBounds of a never-shown window
        // is Rect.Empty (infinities) - persisting that would overwrite the user's real saved
        // geometry with garbage that the next startup silently rejects.
        private bool _windowShownOnce;

        // Exposed for App.Main(), which wires the CLI pipe server up to this window's view
        // model and dispatcher after construction.
        internal MainViewModel ViewModel => _viewModel;

        private ContextMenu _contextMenu;
        private NotifyIcon _notifyIcon;
        private System.Drawing.Icon _coloredIcon;
        private System.Drawing.Icon _grayIcon;
        private readonly List<MonitorData> _observedMonitors = new List<MonitorData>();

        private const int HotkeyId = 9000;
        private const int WM_HOTKEY = 0x0312;

        // Tells RegisterHotKey not to keep re-firing WM_HOTKEY while the combo is held down
        // (keyboard auto-repeat), so a single press toggles the clamp exactly once.
        private const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainWindow()
        {
            // Single-instance detection lives in App.Main() now (named mutex): by the time a
            // MainWindow is constructed, this process is guaranteed to be the primary instance.
            Logger.Log(LogLevel.Info, "Application started");

            InitializeComponent();
            _viewModel = (MainViewModel)DataContext;
            RestoreWindowGeometry();
            RestoreColumnSort();
            new WindowInteropHelper(this).EnsureHandle();

            SystemEvents.DisplaySettingsChanged += _viewModel.OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += _viewModel.OnPowerModeChanged;

            var args = Environment.GetCommandLineArgs().ToList();
            args.RemoveAt(0);
            var startedMinimized = args.Contains("-minimize");

            if (startedMinimized)
            {
                WindowState = WindowState.Minimized;
                Hide();
            }

            InitializeHotkey();
            InitializeTrayIcon();

            if (startedMinimized)
            {
                ShowTrayTipIfNeeded();
            }

            Closing += MainWindow_Closing;
        }

        // Applies the geometry saved in config.xml, following the same pattern LogWindow uses,
        // plus explicit sanity checks: the file is user-editable and can also be corrupted, and
        // silently applying NaN/negative/absurd bounds would produce an unusable window.
        private void RestoreWindowGeometry()
        {
            if (_viewModel.WindowMaximized)
            {
                _lastNonMinimizedState = WindowState.Maximized;
            }

            if (!_viewModel.WindowLeft.HasValue || !_viewModel.WindowTop.HasValue ||
                !_viewModel.WindowWidth.HasValue || !_viewModel.WindowHeight.HasValue)
            {
                // Nothing saved yet (first run, or upgrading from a version without this) -
                // keep the XAML defaults.
                ApplySavedMaximized();
                return;
            }

            var left = _viewModel.WindowLeft.Value;
            var top = _viewModel.WindowTop.Value;
            var width = _viewModel.WindowWidth.Value;
            var height = _viewModel.WindowHeight.Value;

            if (!IsFinite(left) || !IsFinite(top) || !IsFinite(width) || !IsFinite(height))
            {
                ApplySavedMaximized();
                return;
            }

            if (width < MinWidth || height < MinHeight)
            {
                ApplySavedMaximized();
                return;
            }

            // Guard against a corrupted file claiming an absurd size.
            if (width > SystemParameters.VirtualScreenWidth * 2 ||
                height > SystemParameters.VirtualScreenHeight * 2)
            {
                ApplySavedMaximized();
                return;
            }

            // The monitor the window used to live on may be gone, or the resolution may have
            // dropped (e.g. saved at 2560x1440, now running 1920x1080). A bare "intersects at
            // all" test would happily restore a window whose visible part is a few pixels in
            // the corner, or whose title bar sits above the top edge - in both cases the user
            // cannot grab it. Require a usable chunk of the window, including its title bar
            // row, to land inside the virtual screen.
            var bounds = new Rect(left, top, width, height);
            var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            var visible = Rect.Intersect(bounds, virtualScreen);
            const double minVisibleWidth = 120;
            const double minVisibleHeight = 40;
            var titleBarReachable = top >= virtualScreen.Top &&
                                    top <= virtualScreen.Bottom - minVisibleHeight;

            if (visible.IsEmpty || visible.Width < minVisibleWidth ||
                visible.Height < minVisibleHeight || !titleBarReachable)
            {
                ApplySavedMaximized();
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            ApplySavedMaximized();
        }

        private void ApplySavedMaximized()
        {
            if (_viewModel.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        // double.IsFinite does not exist in .NET Framework 4.8.
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // Re-applies the column sort saved in config.xml. Sorting lives on the collection view,
        // not on the items, so this survives UpdateMonitors() clearing and refilling Monitors.
        private void RestoreColumnSort()
        {
            if (string.IsNullOrEmpty(_viewModel.SortColumn)) return;

            // The saved column may no longer exist (e.g. after a layout change in a future
            // version) - leave the default order rather than guessing.
            var column = MonitorGrid.Columns
                .FirstOrDefault(c => c.SortMemberPath == _viewModel.SortColumn);
            if (column == null) return;

            var direction = _viewModel.SortDescending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            MonitorGrid.Items.SortDescriptions.Clear();
            MonitorGrid.Items.SortDescriptions.Add(
                new SortDescription(_viewModel.SortColumn, direction));
            // Drives the little arrow in the header; without it the list would be sorted but
            // the header would look unsorted.
            column.SortDirection = direction;
        }

        private void CaptureColumnSort()
        {
            if (MonitorGrid.Items.SortDescriptions.Count == 0)
            {
                _viewModel.SortColumn = null;
                _viewModel.SortDescending = false;
                return;
            }

            var sort = MonitorGrid.Items.SortDescriptions[0];
            _viewModel.SortColumn = sort.PropertyName;
            _viewModel.SortDescending = sort.Direction == ListSortDirection.Descending;
        }

        // Pushes the current bounds into the view model, which owns the config.xml schema.
        private void CaptureWindowGeometry()
        {
            // Never captured anything while the window was hidden the whole session: keep the
            // values loaded from the config instead of overwriting them with Rect.Empty.
            if (!_windowShownOnce) return;

            // RestoreBounds (not the live Width/Height) is what must be persisted: while
            // maximized the live size is the screen size, and restoring that as the "normal"
            // size would lose the size the user actually picked.
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            if (bounds.IsEmpty || !IsFinite(bounds.Left) || !IsFinite(bounds.Top) ||
                !IsFinite(bounds.Width) || !IsFinite(bounds.Height))
            {
                return;
            }

            _viewModel.WindowLeft = bounds.Left;
            _viewModel.WindowTop = bounds.Top;
            _viewModel.WindowWidth = bounds.Width;
            _viewModel.WindowHeight = bounds.Height;
            _viewModel.WindowMaximized = _lastNonMinimizedState == WindowState.Maximized;
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            // Capture geometry BEFORE the _exitRequested early-return: "Exit" from the tray
            // sets that flag and returns here immediately, so anything placed below would
            // never run on a real quit - the user's last resize would be lost whenever they
            // exited without hiding to tray first.
            CaptureWindowGeometry();
            CaptureColumnSort();
            _viewModel.SaveConfig(fatalOnError: false);

            if (_exitRequested) return;

            // Clicking the window's X hides it to the tray (out of the taskbar entirely).
            // The regular minimize button ("_") keeps standard Windows behavior (stays in the
            // taskbar) - only this path should also hide to tray. "Exit" in the tray menu is
            // the only way to actually quit.
            e.Cancel = true;

            if (WindowState == WindowState.Minimized)
            {
                // Already minimized (e.g. closed via a taskbar thumbnail or Alt+F4 while
                // minimized) - setting WindowState to its current value below would be a no-op
                // and OnStateChanged would never fire, leaving the window stuck and (on a later
                // unrelated minimize) incorrectly hiding to tray via a stale pending flag. Hide
                // directly instead of relying on the state-change round trip.
                Hide();
                ShowTrayTipIfNeeded();
            }
            else
            {
                _hideToTrayPending = true;
                WindowState = WindowState.Minimized;
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Normal || WindowState == WindowState.Maximized)
            {
                // Track this on every transition, not just when hiding to tray: the user can
                // maximize, press the ordinary minimize button (staying in the taskbar) and
                // only then close/exit - by that point WindowState is already Minimized and
                // the maximized state would be unrecoverable.
                _lastNonMinimizedState = WindowState;
            }

            if (WindowState == WindowState.Minimized && _hideToTrayPending)
            {
                _hideToTrayPending = false;
                Hide();
                ShowTrayTipIfNeeded();
            }

            base.OnStateChanged(e);
        }

        protected override void OnContentRendered(EventArgs e)
        {
            _windowShownOnce = true;
            base.OnContentRendered(e);
        }

        // Single shared restore path for both the tray double-click and the CLI ACTIVATE
        // command (a second launch attempt delegates its foreground right to this process via
        // AllowSetForegroundWindow before we get here). Keeping this in one method matters:
        // the window-QoL phase will teach it to restore Maximized instead of hardcoding
        // Normal, and that change must apply to both callers at once.
        public void RestoreWindowToForeground()
        {
            // Show() + WindowState alone can leave the window logically restored but still
            // behind other apps on screen; Activate() asks for the foreground (best effort -
            // Windows may refuse, but the window is at least visible and restored).
            // Restoring _lastNonMinimizedState rather than a hardcoded Normal keeps a
            // maximized window maximized across a hide-to-tray round trip.
            Show();
            WindowState = _lastNonMinimizedState;
            Activate();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs o)
        {
            var window = new AboutWindow
            {
                Owner = this
            };
            window.ShowDialog();
        }

        private void LogsButton_Click(object sender, RoutedEventArgs e)
        {
            OpenLogsWindow();
        }

        private void OpenLogsWindow()
        {
            if (Application.Current.Windows.Cast<Window>().Any(x => x is LogWindow)) return;
            var window = new LogWindow
            {
                Owner = this
            };
            window.Show();
        }

        private void HotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            OpenHotkeyWindow();
        }

        private void OpenHotkeyWindow()
        {
            if (Application.Current.Windows.Cast<Window>().Any(x => x is HotkeyWindow)) return;

            var oldModifiers = _viewModel.HotkeyModifiers;
            var oldKey = _viewModel.HotkeyKey;

            // Unregister the current hotkey while the assignment dialog is open, so the old
            // combo doesn't keep toggling the clamp in the background while it's being reassigned.
            TryRegisterHotkey(ModifierKeys.None, Key.None);

            var window = new HotkeyWindow(oldModifiers, oldKey)
            {
                Owner = this
            };

            if (window.ShowDialog() != true)
            {
                TryRegisterHotkey(oldModifiers, oldKey);
                return;
            }

            if (TryRegisterHotkey(window.ResultModifiers, window.ResultKey))
            {
                _viewModel.SetHotkey(window.ResultModifiers, window.ResultKey);
                Logger.Log(LogLevel.Info,
                    window.ResultKey == Key.None
                        ? "Hotkey disabled"
                        : "Hotkey changed to " + FormatHotkey(window.ResultModifiers, window.ResultKey));
            }
            else
            {
                TryRegisterHotkey(oldModifiers, oldKey);
                Logger.Log(LogLevel.Warning,
                    "Failed to set hotkey to " + FormatHotkey(window.ResultModifiers, window.ResultKey) +
                    ", it is already in use by another application");
                MessageBox.Show("This hotkey is already in use by another application.");
            }
        }

        private void AdvancedButton_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.Windows.Cast<Window>().Any(x => x is AdvancedWindow)) return;
            var monitor = ((FrameworkElement)sender).DataContext as MonitorData;
            var window = new AdvancedWindow(monitor)
            {
                Owner = this
            };

            void CloseWindow(object o, EventArgs e2) => window.Close();

            SystemEvents.DisplaySettingsChanged += CloseWindow;
            if (window.ShowDialog() == false) return;
            SystemEvents.DisplaySettingsChanged -= CloseWindow;

            if (window.ChangedCalibration)
            {
                _viewModel.SaveConfig();
                LogCalibrationChange(monitor);
                // Manual: the user just confirmed calibration changes in the Advanced dialog,
                // so a -104 rejection here should surface a popup after the retry grace period.
                monitor?.ReapplyClamp(true);
            }

            if (window.ChangedDither)
            {
                monitor?.ApplyDither(window.DitherState.SelectedIndex, Math.Max(window.DitherBits.SelectedIndex, 0),
                    Math.Max(window.DitherMode.SelectedIndex, 0));
            }
        }

        private static readonly string[] TargetColorSpaceNames = { "sRGB/BT.709", "Display P3", "Adobe RGB", "BT.2020" };

        private static void LogCalibrationChange(MonitorData monitor)
        {
            if (monitor == null) return;

            var target = TargetColorSpaceNames[monitor.Target];
            var source = monitor.UseIcc ? "ICC profile" : "EDID";
            var gamma = monitor.UseIcc && monitor.CalibrateGamma ? ", gamma calibration on" : "";
            Logger.Log(LogLevel.Info,
                "Calibration updated for " + monitor.Name + ": target=" + target + ", source=" + source + gamma);
        }

        private void ReapplyButton_Click(object sender, RoutedEventArgs e)
        {
            ReapplyMonitorSettings();
        }

        private void TipButton_Click(object sender, RoutedEventArgs e)
        {
            ShowTrayTipBalloon();
        }

        private void ShowTrayTipBalloon()
        {
            _notifyIcon.ShowBalloonTip(5000, "Novideo sRGB is still running",
                "Hidden in the tray now. Click the \"^\" arrow near the clock to find the icon - " +
                "drag it onto the taskbar to keep it visible, or double-click it to reopen.",
                ToolTipIcon.Info);
        }

        private void ShowTrayTipIfNeeded()
        {
            if (_viewModel.TrayTipShown) return;
            ShowTrayTipBalloon();
            _viewModel.MarkTrayTipShown();
        }

        private void InitializeTrayIcon()
        {
            _coloredIcon = (System.Drawing.Icon)Properties.Resources.icon.Clone();
            _grayIcon = CreateGrayscaleIcon(Properties.Resources.icon);

            _notifyIcon = new NotifyIcon
            {
                Icon = _coloredIcon,
                Visible = true
            };

            _notifyIcon.MouseDoubleClick +=
                delegate { RestoreWindowToForeground(); };

            _contextMenu = new ContextMenu();

            _contextMenu.Popup += delegate { UpdateContextMenu(); };

            _notifyIcon.ContextMenu = _contextMenu;

            _viewModel.Monitors.CollectionChanged += delegate { ObserveAllMonitors(); };
            ObserveAllMonitors();

            Closed += delegate
            {
                Logger.Log(LogLevel.Info, "Application closing");
                Logger.Shutdown();
                UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
                _notifyIcon.Dispose();
                _coloredIcon.Dispose();
                _grayIcon.Dispose();
                _viewModel.StopDriftPoll();
                foreach (var monitor in _observedMonitors)
                {
                    monitor.PropertyChanged -= Monitor_PropertyChanged;
                }
            };
        }

        private void InitializeHotkey()
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            var hwndSource = HwndSource.FromHwnd(hwnd);
            hwndSource.AddHook(HotkeyWndProc);

            if (_viewModel.HotkeyKey == Key.None) return;

            if (TryRegisterHotkey(_viewModel.HotkeyModifiers, _viewModel.HotkeyKey))
            {
                Logger.Log(LogLevel.Info,
                    "Hotkey registered: " + FormatHotkey(_viewModel.HotkeyModifiers, _viewModel.HotkeyKey));
            }
            else
            {
                Logger.Log(LogLevel.Warning,
                    "Failed to register hotkey " + FormatHotkey(_viewModel.HotkeyModifiers, _viewModel.HotkeyKey) +
                    " at startup, it may already be in use by another application");
            }
        }

        private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                _viewModel.ToggleAllClamps();
            }

            return IntPtr.Zero;
        }

        public bool TryRegisterHotkey(ModifierKeys modifiers, Key key)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HotkeyId);

            if (key == Key.None) return true;

            // ModifierKeys values (Alt=1, Control=2, Shift=4, Windows=8) intentionally match the
            // Win32 MOD_* constants, so the cast below is safe as-is.
            return RegisterHotKey(hwnd, HotkeyId, (uint)modifiers | ModNoRepeat,
                (uint)KeyInterop.VirtualKeyFromKey(key));
        }

        public static string FormatHotkey(ModifierKeys modifiers, Key key)
        {
            if (key == Key.None) return "(none)";

            var parts = new System.Collections.Generic.List<string>();
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(FormatKeyName(key));
            return string.Join("+", parts);
        }

        private static string FormatKeyName(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
            return key.ToString();
        }

        private void ObserveAllMonitors()
        {
            foreach (var monitor in _observedMonitors)
            {
                monitor.PropertyChanged -= Monitor_PropertyChanged;
            }

            _observedMonitors.Clear();
            _observedMonitors.AddRange(_viewModel.Monitors);

            foreach (var monitor in _observedMonitors)
            {
                monitor.PropertyChanged += Monitor_PropertyChanged;
            }

            UpdateTrayIconState();
        }

        private void Monitor_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MonitorData.Clamped) ||
                e.PropertyName == nameof(MonitorData.IsClampPending))
            {
                UpdateTrayIconState();
            }
        }

        private void UpdateTrayIconState()
        {
            if (_viewModel.Monitors.Count == 0)
            {
                _notifyIcon.Icon = _grayIcon;
                _notifyIcon.Text = "Novideo sRGB - no monitors found";
                return;
            }

            // Reflects whether the clamp is active on any monitor, matching ToggleAllClamps'
            // notion of "on" for the hotkey (rather than only tracking a single monitor).
            // The icon always shows the ACTUAL state; a pending (-104 deferred) change is
            // surfaced via the tooltip suffix only.
            var anyClamped = _viewModel.Monitors.Any(m => m.Clamped);
            var anyPending = _viewModel.Monitors.Any(m => m.IsClampPending);
            _notifyIcon.Icon = anyClamped ? _coloredIcon : _grayIcon;
            _notifyIcon.Text = "Novideo sRGB - " + (anyClamped ? "Clamped" : "Off") +
                               (anyPending ? " (applying...)" : "");
        }

        private static System.Drawing.Icon CreateGrayscaleIcon(System.Drawing.Icon source)
        {
            var size = SystemInformation.SmallIconSize;
            using (var sizedIcon = new System.Drawing.Icon(source, size))
            using (var bitmap = sizedIcon.ToBitmap())
            using (var grayBitmap = new Bitmap(bitmap.Width, bitmap.Height))
            {
                var colorMatrix = new ColorMatrix(new[]
                {
                    new[] { 0.3f, 0.3f, 0.3f, 0, 0 },
                    new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                    new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 0, 0, 0, 0, 1 }
                });

                using (var attributes = new ImageAttributes())
                {
                    attributes.SetColorMatrix(colorMatrix);
                    using (var graphics = Graphics.FromImage(grayBitmap))
                    {
                        graphics.DrawImage(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height), 0, 0,
                            bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
                    }
                }

                var hIcon = grayBitmap.GetHicon();
                try
                {
                    using (var handleIcon = System.Drawing.Icon.FromHandle(hIcon))
                    {
                        return (System.Drawing.Icon)handleIcon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private void UpdateContextMenu()
        {
            _contextMenu.MenuItems.Clear();

            foreach (var monitor in _viewModel.Monitors)
            {
                var item = new MenuItem();
                _contextMenu.MenuItems.Add(item);
                item.Text = monitor.Name;
                // Same intent-based semantics as the checkbox and the hotkey: while a -104
                // retry is pending this shows (and flips) what was requested, not the state
                // the driver has accepted so far.
                item.Checked = monitor.ClampRequested;
                item.Enabled = monitor.CanClamp;
                item.Click += (sender, args) => monitor.ClampRequested = !monitor.ClampRequested;
            }

            _contextMenu.MenuItems.Add("-");

            var logsItem = new MenuItem();
            _contextMenu.MenuItems.Add(logsItem);
            logsItem.Text = "Logs";
            logsItem.Click += delegate { OpenLogsWindow(); };

            var hotkeyItem = new MenuItem();
            _contextMenu.MenuItems.Add(hotkeyItem);
            hotkeyItem.Text = "Hotkey settings...";
            hotkeyItem.Click += delegate { OpenHotkeyWindow(); };

            var reapplyItem = new MenuItem();
            _contextMenu.MenuItems.Add(reapplyItem);
            reapplyItem.Text = "Reapply";
            reapplyItem.Click += delegate { ReapplyMonitorSettings(); };

            var trayTipItem = new MenuItem();
            _contextMenu.MenuItems.Add(trayTipItem);
            trayTipItem.Text = "Show tray tip";
            trayTipItem.Click += delegate { ShowTrayTipBalloon(); };

            var exitItem = new MenuItem();
            _contextMenu.MenuItems.Add(exitItem);
            exitItem.Text = "Exit";
            exitItem.Click += delegate
            {
                _exitRequested = true;
                Close();
            };
        }

        private void ReapplyMonitorSettings()
        {
            foreach (var monitor in _viewModel.Monitors)
            {
                monitor.ReapplyClamp(true);
            }
        }
    }
}