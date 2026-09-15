using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using NvAPIWrapper.Display;

namespace novideo_srgb
{
    public class MainViewModel
    {
        public ObservableCollection<MonitorData> Monitors { get; }

        private bool _updatingMonitors;
        private bool _monitorsUpdatePending;

        private string _configPath;

        private string _startupName;
        private RegistryKey _startupKey;
        private string _startupValue;

        private readonly DispatcherTimer _driftPollTimer;

        public ModifierKeys HotkeyModifiers { get; private set; } = ModifierKeys.Control | ModifierKeys.Shift;
        public Key HotkeyKey { get; private set; } = Key.F9;
        public bool TrayTipShown { get; private set; }

        public void MarkTrayTipShown()
        {
            if (TrayTipShown) return;
            TrayTipShown = true;
            SaveConfig();
        }

        public MainViewModel()
        {
            Monitors = new ObservableCollection<MonitorData>();
            _configPath = AppDomain.CurrentDomain.BaseDirectory + "config.xml";

            _startupName = "novideo_srgb";
            _startupKey = Registry.CurrentUser.OpenSubKey
                ("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            _startupValue = Application.ExecutablePath + " -minimize";

            LoadHotkeySettings();
            UpdateMonitors();
            LogStartupStatus();

            _driftPollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _driftPollTimer.Tick += delegate
            {
                foreach (var monitor in Monitors)
                {
                    monitor.CheckForDrift();
                    monitor.CheckForMonitorWake();
                }
            };
            _driftPollTimer.Start();
        }

        public void StopDriftPoll()
        {
            _driftPollTimer.Stop();
        }

        public void SetHotkey(ModifierKeys modifiers, Key key)
        {
            HotkeyModifiers = modifiers;
            HotkeyKey = key;
            SaveConfig();
        }

        public void ToggleAllClamps()
        {
            var clampable = Monitors.Where(m => m.CanClamp).ToList();
            if (clampable.Count == 0)
            {
                Logger.Log(LogLevel.Warning, "Hotkey pressed, but no monitor can currently be clamped");
                return;
            }

            var turnOn = clampable.Any(m => !m.Clamped);
            foreach (var monitor in clampable)
            {
                monitor.SetClampedFromHotkey(turnOn);
            }
        }

        private void LoadHotkeySettings()
        {
            if (!File.Exists(_configPath)) return;

            try
            {
                var root = XElement.Load(_configPath);
                var modifiersAttr = root.Attribute("hotkey_modifiers");
                var keyAttr = root.Attribute("hotkey_key");

                if (modifiersAttr != null)
                {
                    HotkeyModifiers = (ModifierKeys)Enum.Parse(typeof(ModifierKeys), modifiersAttr.Value);
                }

                if (keyAttr != null)
                {
                    HotkeyKey = (Key)Enum.Parse(typeof(Key), keyAttr.Value);
                }

                var trayTipAttr = root.Attribute("tray_tip_shown");
                if (trayTipAttr != null)
                {
                    TrayTipShown = (bool)trayTipAttr;
                }
            }
            catch
            {
            }
        }

        private void LogStartupStatus()
        {
            foreach (var monitor in Monitors)
            {
                Logger.Log(monitor.Clamped ? LogLevel.Success : LogLevel.Off,
                    "Startup check: clamp for " + monitor.Name + " is " +
                    (monitor.Clamped ? "on" : "off (" + monitor.ClampOffReason + ")"));
            }
        }

        public bool? RunAtStartup
        {
            get
            {
                var keyValue = _startupKey.GetValue(_startupName);

                if (keyValue == null)
                {
                    return false;
                }

                if ((string)keyValue == _startupValue)
                {
                    return true;
                }

                return null;
            }
            set
            {
                try
                {
                    if (value == true)
                    {
                        _startupKey.SetValue(_startupName, _startupValue);
                    }
                    else
                    {
                        _startupKey.DeleteValue(_startupName, false);
                    }

                    Logger.Log(LogLevel.Info, "Run at startup " + (value == true ? "enabled" : "disabled"));
                }
                catch (Exception e)
                {
                    Logger.Log(LogLevel.Error, "Failed to update run-at-startup setting: " + e);
                    MessageBox.Show(e.Message);
                }
            }
        }

        private void UpdateMonitors()
        {
            // SystemEvents.DisplaySettingsChanged can fire reentrantly on the same thread: a
            // modal MessageBox shown from ReapplyClamp() (e.g. on a -104 NVAPI error) pumps a
            // nested message loop, and if another display-change message arrives while that's
            // open, this method gets re-entered while the outer call is still mid-`foreach` over
            // `Monitors` - clearing/rebuilding the collection out from under that enumeration and
            // crashing with "Collection was modified". Guard against running concurrently, and
            // just re-run once more afterwards if a change came in while we were busy, so the
            // latest display state is never silently dropped.
            if (_updatingMonitors)
            {
                _monitorsUpdatePending = true;
                return;
            }

            _updatingMonitors = true;
            try
            {
                do
                {
                    _monitorsUpdatePending = false;
                    RunUpdateMonitors();
                } while (_monitorsUpdatePending);
            }
            finally
            {
                _updatingMonitors = false;
            }
        }

        private void RunUpdateMonitors()
        {
            Monitors.Clear();
            List<XElement> config = null;
            if (File.Exists(_configPath))
            {
                config = XElement.Load(_configPath).Descendants("monitor").ToList();
            }

            var activePaths = DisplayConfigManager.GetActiveDisplayPaths();
            var hdrPaths = DisplayConfigManager.GetHdrDisplayPaths();

            var number = 1;
            foreach (var display in Display.GetDisplays())
            {
                var displays = WindowsDisplayAPI.Display.GetDisplays();
                var path = displays.First(x => x.DisplayName == display.Name).DevicePath;

                // Outputs DisplayConfig no longer reports as active (e.g. disconnected between
                // enumeration calls) are still listed, but CanClamp gates any clamp attempt on
                // them off, so we never call into NVAPI for a disconnected output.
                var isActive = activePaths.Contains(path);

                var hdrActive = hdrPaths.Contains(path);

                var settings = config?.FirstOrDefault(x => (string)x.Attribute("path") == path);
                MonitorData monitor;
                if (settings != null)
                {
                    monitor = new MonitorData(this, number++, display, path, hdrActive,
                        (bool)settings.Attribute("clamp_sdr"), isActive,
                        (bool)settings.Attribute("use_icc"),
                        (string)settings.Attribute("icc_path") ?? "",
                        (bool)settings.Attribute("calibrate_gamma"),
                        (int)settings.Attribute("selected_gamma"),
                        (double)settings.Attribute("custom_gamma"),
                        (double)settings.Attribute("custom_percentage"),
                        (int)settings.Attribute("target"),
                        (bool)settings.Attribute("disable_optimization"));
                }
                else
                {
                    monitor = new MonitorData(this, number++, display, path, hdrActive, false, isActive);
                }

                Monitors.Add(monitor);
            }

            foreach (var monitor in Monitors)
            {
                monitor.ReapplyClamp();
            }
        }

        public void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Logger.Log(LogLevel.Info, "Display configuration changed, re-detected monitors");
            UpdateMonitors();
        }

        public void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            OnDisplaySettingsChanged(null, null);
        }

        public void SaveConfig()
        {
            try
            {
                // Monitors not currently connected (e.g. a laptop undocked, or a display
                // temporarily powered off) are absent from `Monitors`, so preserve their old
                // entries here instead of silently dropping their settings on next save.
                List<XElement> offlineEntries = null;
                if (File.Exists(_configPath))
                {
                    var oldConfig = XElement.Load(_configPath).Descendants("monitor").ToList();
                    offlineEntries = oldConfig.FindAll(x => Monitors.All(m => m.Path != (string)x.Attribute("path")));
                }

                var xElem = new XElement("monitors",
                    new XAttribute("hotkey_modifiers", HotkeyModifiers),
                    new XAttribute("hotkey_key", HotkeyKey),
                    new XAttribute("tray_tip_shown", TrayTipShown),
                    Monitors.Select(x =>
                        new XElement("monitor", new XAttribute("path", x.Path),
                            new XAttribute("clamp_sdr", x.ClampSdr),
                            new XAttribute("use_icc", x.UseIcc),
                            new XAttribute("icc_path", x.ProfilePath),
                            new XAttribute("calibrate_gamma", x.CalibrateGamma),
                            new XAttribute("selected_gamma", x.SelectedGamma),
                            new XAttribute("custom_gamma", x.CustomGamma),
                            new XAttribute("custom_percentage", x.CustomPercentage),
                            new XAttribute("target", x.Target),
                            new XAttribute("disable_optimization", x.DisableOptimization))));

                if (offlineEntries != null)
                {
                    xElem.Add(offlineEntries);
                }

                xElem.Save(_configPath);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, "Failed to save config: " + ex.Message);
                MessageBox.Show(ex.Message + "\n\nTry extracting the program elsewhere.");
                Environment.Exit(1);
            }
        }
    }
}