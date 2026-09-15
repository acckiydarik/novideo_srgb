using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
            if (Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1)
            {
                Logger.Log(LogLevel.Warning, "Startup blocked: another instance is already running");
                MessageBox.Show("Already running!");
                Close();
                return;
            }

            Logger.Log(LogLevel.Info, "Application started");

            InitializeComponent();
            _viewModel = (MainViewModel)DataContext;
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

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_exitRequested) return;

            // Clicking the window's X hides it to the tray (out of the taskbar entirely).
            // The regular minimize button ("_") keeps standard Windows behavior (stays in the
            // taskbar) - only this path should also hide to tray. "Exit" in the tray menu is
            // the only way to actually quit.
            e.Cancel = true;
            _hideToTrayPending = true;
            WindowState = WindowState.Minimized;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _hideToTrayPending)
            {
                _hideToTrayPending = false;
                Hide();
                ShowTrayTipIfNeeded();
            }

            base.OnStateChanged(e);
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
                monitor?.ReapplyClamp();
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
                delegate
                {
                    // Show() + WindowState alone can leave the window logically restored but
                    // still behind other apps on screen; Activate() forces it to the foreground.
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                };

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
            if (e.PropertyName == nameof(MonitorData.Clamped))
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
            var anyClamped = _viewModel.Monitors.Any(m => m.Clamped);
            _notifyIcon.Icon = anyClamped ? _coloredIcon : _grayIcon;
            _notifyIcon.Text = "Novideo sRGB - " + (anyClamped ? "Clamped" : "Off");
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
                item.Checked = monitor.Clamped;
                item.Enabled = monitor.CanClamp;
                item.Click += (sender, args) => monitor.Clamped = !monitor.Clamped;
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