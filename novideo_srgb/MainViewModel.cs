using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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

        // Main window geometry. SaveConfig() rebuilds the whole <monitors> root element on
        // every call (hotkey change, clamp toggle, tray tip, Advanced confirm...), so any
        // setting written to config.xml from outside this class would be wiped by the very
        // next save. The view model therefore owns these values; MainWindow only pushes its
        // current bounds into them right before closing.
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }

        // Which column the monitor list is sorted by, as the property name WPF uses in its
        // SortDescriptions (e.g. "Name"). Null means the default, unsorted order.
        public string SortColumn { get; set; }
        public bool SortDescending { get; set; }

        // When the process last restarted itself after a WPF render thread failure. Used as the
        // loop-guard: a second failure inside the guard window means restarting is not helping,
        // so the app stays up in a degraded state instead of cycling.
        public DateTime? LastAutoRestart { get; set; }

        // Set once the loop-guard trips. Software rendering cannot repair an already-zombied
        // partition (verified experimentally), but it does avoid the GPU path entirely on the
        // next start, which is the only form in which this fallback works at all.
        public bool ForceSoftwareRendering { get; set; }

        // Read before any MainViewModel instance exists: the render mode has to be set before
        // the first window is created, which happens before the view model is constructed.
        public static bool ReadForceSoftwareRenderingFlag()
        {
            try
            {
                var path = AppDomain.CurrentDomain.BaseDirectory + "config.xml";
                if (!File.Exists(path)) return false;

                var attr = XElement.Load(path).Attribute("force_software_rendering");
                bool value;
                return attr != null && bool.TryParse(attr.Value, out value) && value;
            }
            catch
            {
                return false;
            }
        }

        // Consumes the one-shot flag. Edits the file in place rather than going through
        // SaveConfig, which does not exist yet at this point in startup - and which would
        // rewrite monitor entries from an empty collection if it did.
        public static void ClearForceSoftwareRenderingFlag()
        {
            try
            {
                var path = AppDomain.CurrentDomain.BaseDirectory + "config.xml";
                if (!File.Exists(path)) return;

                var root = XElement.Load(path);
                var attr = root.Attribute("force_software_rendering");
                if (attr == null) return;

                attr.Remove();
                root.Save(path);
            }
            catch
            {
            }
        }

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
                // Release a suppressed exception storm's tally once it has gone quiet. Done
                // from the existing poll rather than a dedicated timer, and outside the monitor
                // loop because it is unrelated to any single monitor.
                var aggregate = RenderThreadFailure.FlushIfQuiet();
                if (aggregate != null) Logger.Log(LogLevel.Error, aggregate);

                // Snapshot: RetryPendingClamp can show a modal popup, whose nested message loop
                // may run UpdateMonitors (display change) and clear/rebuild Monitors mid-loop.
                foreach (var monitor in Monitors.ToList())
                {
                    monitor.RetryPendingClamp();
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

            // Steer by the effective target (pending intent when a -104 retry is in flight,
            // actual state otherwise) so repeated presses keep flipping in the expected
            // direction even before the driver has accepted the previous change.
            var turnOn = clampable.Any(m => !m.EffectiveClampTarget);
            foreach (var monitor in clampable)
            {
                monitor.SetClampedFromHotkey(turnOn);
            }
        }

        // ---- CLI command handlers (called from the pipe server via Dispatcher.Invoke) ----

        public CliCommandResult EnableClamp(int? index) => ApplyCliClamp(index, _ => true);

        public CliCommandResult DisableClamp(int? index) => ApplyCliClamp(index, _ => false);

        public CliCommandResult ToggleClamp(int? index)
        {
            if (index.HasValue) return ApplyCliClamp(index, m => !m.EffectiveClampTarget);

            // Unindexed toggle must mirror ToggleAllClamps(): compute ONE shared target from
            // all clampable monitors and apply that same value everywhere. Toggling each
            // monitor independently would drive mixed states in opposite directions, the exact
            // opposite of the hotkey behavior this command promises to match.
            var clampable = Monitors.Where(m => m.CanClamp).ToList();
            var turnOn = clampable.Any(m => !m.EffectiveClampTarget);
            return ApplyCliClamp(null, _ => turnOn);
        }

        private CliCommandResult ApplyCliClamp(int? index, Func<MonitorData, bool> targetFor)
        {
            if (index.HasValue)
            {
                var monitor = Monitors.FirstOrDefault(m => m.Number == index.Value);
                if (monitor == null)
                {
                    return new CliCommandResult(3, "No monitor with index " + index.Value);
                }

                var outcome = monitor.SetClampedFromCli(targetFor(monitor));
                return CliCommandResult.FromOutcome(outcome, monitor);
            }

            // Unindexed command: apply to every monitor, aggregating per the documented
            // policy - skipped (CanClamp == false) monitors are listed but only fail the call
            // when nothing at all was processed; any real Error fails the whole call.
            var results = Monitors.ToList()
                .Select(m => new { Monitor = m, Outcome = m.SetClampedFromCli(targetFor(m)) })
                .ToList();

            if (results.Count == 0)
            {
                return new CliCommandResult(3, "No monitors found");
            }

            var lines = results
                .Select(r => "#" + r.Monitor.Number + " " + r.Monitor.Name + ": " +
                             CliCommandResult.DescribeOutcome(r.Outcome, r.Monitor))
                .ToList();
            var text = string.Join("; ", lines);

            if (results.Any(r => r.Outcome == ClampCommandOutcome.Error))
            {
                return new CliCommandResult(5, text);
            }

            // All monitors unavailable and none already in the requested state: nothing was
            // done at all - report code 4 with per-monitor reasons instead of a silent success.
            if (results.All(r => r.Outcome == ClampCommandOutcome.Unavailable))
            {
                return new CliCommandResult(4, text);
            }

            return new CliCommandResult(0, text);
        }

        public CliCommandResult GetStatus(int? index)
        {
            var monitors = index.HasValue
                ? Monitors.Where(m => m.Number == index.Value).ToList()
                : Monitors.ToList();

            if (monitors.Count == 0)
            {
                return index.HasValue
                    ? new CliCommandResult(3, "No monitor with index " + index.Value)
                    : new CliCommandResult(3, "No monitors found");
            }

            var lines = monitors.Select(m =>
                "#" + m.Number + " " + m.Name + ": " +
                (m.Clamped ? "clamped" : "not clamped") +
                (m.IsClampPending ? ", applying" : "") +
                (!m.CanClamp ? " (" + m.ClampOffReason + ")" : ""));
            return new CliCommandResult(0, string.Join("; ", lines));
        }

        // Loads the root-level settings that are not per-monitor: hotkey, tray tip flag and
        // main window geometry.
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

            LoadWindowGeometry();
        }

        // Read separately from the hotkey block: a single corrupted geometry attribute must not
        // abort the whole load and silently reset the hotkey/tray-tip settings with it.
        private void LoadWindowGeometry()
        {
            try
            {
                var root = XElement.Load(_configPath);

                WindowLeft = ReadDouble(root, "window_left");
                WindowTop = ReadDouble(root, "window_top");
                WindowWidth = ReadDouble(root, "window_width");
                WindowHeight = ReadDouble(root, "window_height");

                var maximizedAttr = root.Attribute("window_maximized");
                bool maximized;
                if (maximizedAttr != null && bool.TryParse(maximizedAttr.Value, out maximized))
                {
                    WindowMaximized = maximized;
                }

                var sortColumnAttr = root.Attribute("sort_column");
                if (sortColumnAttr != null)
                {
                    SortColumn = sortColumnAttr.Value;
                }

                var sortDescendingAttr = root.Attribute("sort_descending");
                bool descending;
                if (sortDescendingAttr != null && bool.TryParse(sortDescendingAttr.Value, out descending))
                {
                    SortDescending = descending;
                }

                var lastRestartAttr = root.Attribute("last_auto_restart");
                DateTime lastRestart;
                // Round-trip parsing to match how the value is written; a machine-local format
                // would break the loop-guard after a locale change.
                if (lastRestartAttr != null && DateTime.TryParse(lastRestartAttr.Value,
                        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastRestart))
                {
                    LastAutoRestart = lastRestart;
                }

                var softwareAttr = root.Attribute("force_software_rendering");
                bool forceSoftware;
                if (softwareAttr != null && bool.TryParse(softwareAttr.Value, out forceSoftware))
                {
                    ForceSoftwareRendering = forceSoftware;
                }
            }
            catch
            {
            }
        }

        private static double? ReadDouble(XElement root, string name)
        {
            var attr = root.Attribute(name);
            if (attr == null) return null;
            double value;
            // Invariant parsing: the value is written with XAttribute's invariant formatting,
            // so a machine with a comma decimal separator must not fail to read it back.
            return double.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : (double?)null;
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
            SaveConfig(fatalOnError: true);
        }

        // fatalOnError:false is for saves the user did not explicitly ask for (the window
        // geometry write that now happens on every close, including hide-to-tray). Losing a
        // window size to a transient file lock is not worth a modal error box plus an
        // Environment.Exit that would skip the Closed handler and leak the tray icon and the
        // global hotkey registration.
        public void SaveConfig(bool fatalOnError)
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

                // Window geometry must be written here, as part of the same element that is
                // rebuilt from scratch on every save - writing it anywhere else would make it
                // disappear on the next unrelated SaveConfig() call.
                AddGeometry(xElem, "window_left", WindowLeft);
                AddGeometry(xElem, "window_top", WindowTop);
                AddGeometry(xElem, "window_width", WindowWidth);
                AddGeometry(xElem, "window_height", WindowHeight);
                xElem.Add(new XAttribute("window_maximized", WindowMaximized));

                if (!string.IsNullOrEmpty(SortColumn))
                {
                    xElem.Add(new XAttribute("sort_column", SortColumn));
                    xElem.Add(new XAttribute("sort_descending", SortDescending));
                }

                if (LastAutoRestart.HasValue)
                {
                    xElem.Add(new XAttribute("last_auto_restart",
                        LastAutoRestart.Value.ToString("o", CultureInfo.InvariantCulture)));
                }

                if (ForceSoftwareRendering)
                {
                    xElem.Add(new XAttribute("force_software_rendering", true));
                }

                if (offlineEntries != null)
                {
                    xElem.Add(offlineEntries);
                }

                xElem.Save(_configPath);
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, "Failed to save config: " + ex.Message);
                if (!fatalOnError) return;
                MessageBox.Show(ex.Message + "\n\nTry extracting the program elsewhere.");
                Environment.Exit(1);
            }
        }

        private static void AddGeometry(XElement element, string name, double? value)
        {
            if (value.HasValue)
            {
                element.Add(new XAttribute(name, value.Value));
            }
        }
    }
}