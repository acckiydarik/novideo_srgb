using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using EDIDParser;
using EDIDParser.Descriptors;
using EDIDParser.Enums;
using NvAPIWrapper.Display;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.Display;

namespace novideo_srgb
{
    public class MonitorData : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private readonly GPUOutput _output;
        private readonly Display _display;
        private bool _clamped;
        private int _bitDepth;
        private Novideo.DitherControl _dither;
        private bool? _monitorWasActive;

        // Deferred-apply state for driver -104 rejections: the desired clamp state is kept as
        // "pending" and retried from the poll timer until the driver accepts (see plan phase 4
        // and DEVELOPMENT.md for the full -104 investigation this design is based on).
        private const int PendingPopupGraceSeconds = 3;
        private const int PendingProgressLogSeconds = 5;
        private const int PendingMaxNon104Failures = 3;
        private bool? _pendingClampTarget;
        private DateTime _pendingSince;
        private DateTime _pendingLastProgressLog;
        private bool _pendingIsManual;
        private bool _pendingPopupShown;
        private bool _pendingPopupOnScreen;
        private int _pendingRetryCount;
        private int _pendingNon104Failures;

        private MainViewModel _viewModel;

        public MonitorData(MainViewModel viewModel, int number, Display display, string path, bool hdrActive, bool clampSdr, bool isActive)
        {
            _viewModel = viewModel;
            Number = number;
            _output = display.Output;
            _display = display;

            _bitDepth = 0;
            try
            {
                var bitDepth = display.DisplayDevice.CurrentColorData.ColorDepth;
                if (bitDepth == ColorDataDepth.BPC6)
                    _bitDepth = 6;
                else if (bitDepth == ColorDataDepth.BPC8)
                    _bitDepth = 8;
                else if (bitDepth == ColorDataDepth.BPC10)
                    _bitDepth = 10;
                else if (bitDepth == ColorDataDepth.BPC12)
                    _bitDepth = 12;
                else if (bitDepth == ColorDataDepth.BPC16)
                    _bitDepth = 16;
            }
            catch (Exception)
            {
            }

            Edid = Novideo.GetEDID(path, display);

            Name = Edid.Descriptors.OfType<StringDescriptor>()
                .FirstOrDefault(x => x.Type == StringDescriptorType.MonitorName)?.Value ?? "<no name>";

            Path = path;
            ClampSdr = clampSdr;
            HdrActive = hdrActive;
            IsActive = isActive;

            var coords = Edid.DisplayParameters.ChromaticityCoordinates;
            EdidColorSpace = new Colorimetry.ColorSpace
            {
                Red = new Colorimetry.Point { X = Math.Round(coords.RedX, 3), Y = Math.Round(coords.RedY, 3) },
                Green = new Colorimetry.Point { X = Math.Round(coords.GreenX, 3), Y = Math.Round(coords.GreenY, 3) },
                Blue = new Colorimetry.Point { X = Math.Round(coords.BlueX, 3), Y = Math.Round(coords.BlueY, 3) },
                White = Colorimetry.D65
            };

            _dither = Novideo.GetDitherControl(_output);
            _clamped = Novideo.IsColorSpaceConversionActive(_output);

            ProfilePath = "";
            CustomGamma = 2.2;
            CustomPercentage = 100;
        }

        public MonitorData(MainViewModel viewModel, int number, Display display, string path, bool hdrActive, bool clampSdr, bool isActive, bool useIcc, string profilePath,
            bool calibrateGamma,
            int selectedGamma, double customGamma, double customPercentage, int target, bool disableOptimization) :
            this(viewModel, number, display, path, hdrActive, clampSdr, isActive)
        {
            UseIcc = useIcc;
            ProfilePath = profilePath ?? "";
            CalibrateGamma = calibrateGamma;
            SelectedGamma = selectedGamma;
            CustomGamma = customGamma;
            CustomPercentage = customPercentage;
            Target = target;
            DisableOptimization = disableOptimization;
        }

        public int Number { get; }
        public string Name { get; }
        public EDID Edid { get; }
        public string Path { get; }
        public bool ClampSdr { get; set; }
        public bool HdrActive { get; }
        public bool IsActive { get; }

        private void UpdateClamp(bool doClamp)
        {
            if (!IsActive) return;

            if (_clamped)
            {
                Novideo.DisableColorSpaceConversion(_output);
            }

            if (!doClamp) return;

            if (_clamped) Thread.Sleep(100);
            if (UseEdid)
                Novideo.SetColorSpaceConversion(_output, Colorimetry.RGBToRGB(TargetColorSpace, EdidColorSpace));
            else if (UseIcc)
            {
                var profile = ICCMatrixProfile.FromFile(ProfilePath);
                if (CalibrateGamma)
                {
                    var trcBlack = Matrix.FromValues(new[,]
                    {
                        { profile.trcs[0].SampleAt(0) },
                        { profile.trcs[1].SampleAt(0) },
                        { profile.trcs[2].SampleAt(0) }
                    });
                    var black = (profile.matrix * trcBlack)[1];

                    ToneCurve gamma;
                    switch (SelectedGamma)
                    {
                        case 0:
                            gamma = new SrgbEOTF(black);
                            break;
                        case 1:
                            gamma = new GammaToneCurve(2.4, black, 0);
                            break;
                        case 2:
                            gamma = new GammaToneCurve(CustomGamma, black, CustomPercentage / 100);
                            break;
                        case 3:
                            gamma = new GammaToneCurve(CustomGamma, black, CustomPercentage / 100, true);
                            break;
                        case 4:
                            gamma = new LstarEOTF(black);
                            break;
                        default:
                            throw new NotSupportedException("Unsupported gamma type " + SelectedGamma);
                    }

                    Novideo.SetColorSpaceConversion(_output, profile, TargetColorSpace, gamma, DisableOptimization);
                }
                else
                {
                    Novideo.SetColorSpaceConversion(_output, profile, TargetColorSpace);
                }
            }
        }

        private void HandleClampException(Exception e, string sourceSuffix = "")
        {
            Logger.Log(LogLevel.Error, "Failed to apply clamp for " + Name + sourceSuffix + ": " + e);
            // A pending retry loop must not survive this path: intent is being reverted to the
            // actual state below, so a stale pending target would contradict it (and keep
            // EffectiveClampTarget/ToggleAllClamps steering wrong).
            ClearPending();
            _clamped = Novideo.IsColorSpaceConversionActive(_output);
            ClampSdr = _clamped;
            _viewModel.SaveConfig();
            OnPropertyChanged(nameof(Clamped));
            TopMostMessageBox.Show(e.Message);
        }

        public bool IsClampPending => _pendingClampTarget != null;

        // The state toggles should steer by: the not-yet-applied pending intent when one exists,
        // otherwise the actually applied state. Keeps repeated hotkey presses flipping in the
        // expected direction while a -104 retry is still in flight.
        public bool EffectiveClampTarget => _pendingClampTarget ?? _clamped;

        private void BeginPendingClamp(bool target, bool manual, string sourceSuffix)
        {
            var alreadyPending = _pendingClampTarget != null;
            var targetChanged = _pendingClampTarget != target;
            _pendingClampTarget = target;

            if (manual)
            {
                // A fresh manual action restarts the popup grace window and re-arms the popup -
                // unless one is literally on screen right now (a hotkey press can be dispatched
                // while the modal pumps messages), in which case stacking a second popup on top
                // of the first would only add noise.
                _pendingIsManual = true;
                _pendingSince = DateTime.Now;
                _pendingPopupShown = _pendingPopupOnScreen;
            }
            else if (!alreadyPending)
            {
                _pendingIsManual = false;
                _pendingSince = DateTime.Now;
                _pendingPopupShown = false;
            }

            if (!alreadyPending)
            {
                _pendingRetryCount = 0;
                _pendingNon104Failures = 0;
                _pendingLastProgressLog = DateTime.Now;
            }

            // Display-config event storms (e.g. fullscreen transitions) re-enter here several
            // times per second with the same target; log only when something actually changed.
            if (!alreadyPending || targetChanged || manual)
            {
                Logger.Log(LogLevel.Warning,
                    "Driver rejected clamp change for " + Name + sourceSuffix +
                    " (-104), retrying in background until it is accepted");
            }

            OnPropertyChanged(nameof(IsClampPending));
        }

        private void ClearPending()
        {
            if (_pendingClampTarget == null) return;
            _pendingClampTarget = null;
            _pendingIsManual = false;
            _pendingPopupShown = false;
            OnPropertyChanged(nameof(IsClampPending));
        }

        // Called from the 250ms poll timer while a -104-deferred clamp change is pending.
        public void RetryPendingClamp()
        {
            if (_pendingClampTarget == null) return;
            var target = _pendingClampTarget.Value;

            try
            {
                _pendingRetryCount++;
                UpdateClamp(target);
            }
            catch (NvApiException e) when (e.Status == -104)
            {
                _pendingNon104Failures = 0;

                if ((DateTime.Now - _pendingLastProgressLog).TotalSeconds >= PendingProgressLogSeconds)
                {
                    _pendingLastProgressLog = DateTime.Now;
                    Logger.Log(LogLevel.Warning,
                        "Still retrying clamp change for " + Name + " (" + _pendingRetryCount +
                        " attempts so far)");
                }

                if (_pendingIsManual && !_pendingPopupShown &&
                    (DateTime.Now - _pendingSince).TotalSeconds >= PendingPopupGraceSeconds)
                {
                    // Set before showing: the modal pumps a nested message loop, which can
                    // re-enter this timer tick (same class of bug as the UpdateMonitors
                    // reentrancy fixed earlier).
                    _pendingPopupShown = true;
                    _pendingPopupOnScreen = true;
                    try
                    {
                        TopMostMessageBox.Show(
                            "The NVIDIA driver rejected the clamp change (error -104). " +
                            "Retrying in the background - it will be applied as soon as the driver allows.");
                    }
                    finally
                    {
                        _pendingPopupOnScreen = false;
                    }
                }

                return;
            }
            catch (Exception e)
            {
                // Non--104 failure (e.g. a missing ICC profile file): the driver gate is not the
                // problem, so blind retrying can loop forever. Give it a few attempts in case it
                // is transient, then exit the retry loop through the standard error path (which
                // also clears the pending state and shows the real error).
                _pendingNon104Failures++;
                if (_pendingNon104Failures >= PendingMaxNon104Failures)
                {
                    HandleClampException(e);
                    return;
                }

                if ((DateTime.Now - _pendingLastProgressLog).TotalSeconds >= PendingProgressLogSeconds)
                {
                    _pendingLastProgressLog = DateTime.Now;
                    Logger.Log(LogLevel.Warning,
                        "Clamp retry for " + Name + " failed with a non--104 error: " + e.Message);
                }

                return;
            }

            _clamped = target;
            var attempts = _pendingRetryCount;
            ClearPending();
            Logger.Log(target ? LogLevel.Success : LogLevel.Off,
                (target ? "Clamp enabled for " : "Clamp disabled for ") + Name +
                " after " + attempts + " attempts");
            OnPropertyChanged(nameof(Clamped));
        }

        public bool Clamped
        {
            set => SetClamped(value, "");
            get => _clamped;
        }

        public void SetClampedFromHotkey(bool value)
        {
            SetClamped(value, " via hotkey");
        }

        private void SetClamped(bool value, string sourceSuffix)
        {
            // Persist the intent up-front: if the driver rejects the write, retries (and the
            // startup reapply after a restart) must still know what the user wanted.
            ClampSdr = value;
            _viewModel.SaveConfig();

            try
            {
                UpdateClamp(value);
            }
            catch (NvApiException e) when (e.Status == -104)
            {
                BeginPendingClamp(value, manual: true, sourceSuffix);
                return;
            }
            catch (Exception e)
            {
                HandleClampException(e, sourceSuffix);
                return;
            }

            _clamped = value;
            ClearPending();
            Logger.Log(value ? LogLevel.Success : LogLevel.Off,
                (value ? "Clamp enabled for " : "Clamp disabled for ") + Name + sourceSuffix);
            OnPropertyChanged(nameof(Clamped));
        }

        public void ReapplyClamp(bool manual = false)
        {
            var clamped = CanClamp && ClampSdr;

            try
            {
                var previous = _clamped;
                UpdateClamp(clamped);
                _clamped = clamped;
                ClearPending();
                OnPropertyChanged(nameof(CanClamp));
                OnPropertyChanged(nameof(Clamped));

                if (clamped != previous)
                {
                    Logger.Log(LogLevel.Info,
                        "Reapplied clamp for " + Name + ", now " + (clamped ? "enabled" : "disabled"));
                }
                else if (manual)
                {
                    Logger.Log(clamped ? LogLevel.Success : LogLevel.Off,
                        "Reapplied clamp for " + Name + ", still " + (clamped ? "enabled" : "disabled"));
                }
            }
            catch (NvApiException e) when (e.Status == -104)
            {
                if (manual)
                {
                    // A manual reapply may carry new calibration settings even when the boolean
                    // state is unchanged, so always retry it.
                    BeginPendingClamp(clamped, manual: true, "");
                }
                else if (clamped != _clamped)
                {
                    BeginPendingClamp(clamped, manual: false, "");
                }
                else
                {
                    // Background refresh of an already-matching state (the classic startup /
                    // fullscreen-transition noise): nothing to recover, just record it.
                    Logger.Log(LogLevel.Info,
                        "Driver rejected redundant clamp refresh for " + Name +
                        " (-104); current state already matches");
                }
            }
            catch (Exception e)
            {
                HandleClampException(e);
            }
        }

        public void CheckForDrift()
        {
            if (!CanClamp) return;

            bool actual;
            try
            {
                actual = Novideo.IsColorSpaceConversionActive(_output);
            }
            catch
            {
                return;
            }

            if (actual == _clamped) return;

            _clamped = actual;
            Logger.Log(LogLevel.Warning,
                "Detected external change: clamp for " + Name + " is now " + (actual ? "on" : "off"));
            OnPropertyChanged(nameof(Clamped));
        }

        // Windows doesn't reliably notify apps when a monitor (not the whole PC) enters
        // standby via its own power-saving timeout, so SystemEvents.PowerModeChanged alone
        // misses this case. Polling DisplayDevice.IsActive to catch the sleep->wake edge is a
        // community-confirmed workaround (see upstream issue #46); the clamp is reapplied only
        // right after a wake transition, not on every poll, to avoid needless reapply/flicker.
        //
        // Display.DisplayDevice returns a brand-new DisplayDevice snapshot (a fresh NVAPI query)
        // on every access, so it must be re-read via `_display.DisplayDevice` on every poll here
        // rather than cached - caching it once (as an earlier version of this code did) would
        // freeze IsActive at whatever value it had when the monitor was first constructed,
        // silently disabling this entire feature.
        public void CheckForMonitorWake()
        {
            bool isActive;
            try
            {
                isActive = _display.DisplayDevice.IsActive;
            }
            catch
            {
                // Querying IsActive can throw while the monitor is mid-transition into standby;
                // treat that as "not active" rather than letting the exception propagate.
                _monitorWasActive = false;
                return;
            }

            var justWokeUp = _monitorWasActive == false && isActive;
            _monitorWasActive = isActive;

            if (justWokeUp && CanClamp && ClampSdr)
            {
                Logger.Log(LogLevel.Info, "Monitor " + Name + " woke from standby, reapplying clamp");
                ReapplyClamp();
            }
        }

        public bool CanClamp => IsActive && !HdrActive && (UseEdid && !EdidColorSpace.Equals(TargetColorSpace) || UseIcc && ProfilePath != "");

        public string ClampOffReason
        {
            get
            {
                if (IsClampPending) return "applying, driver busy";
                if (!IsActive) return "not connected";
                if (HdrActive) return "HDR active";
                if (!ClampSdr) return "not configured";
                if (UseEdid && EdidColorSpace.Equals(TargetColorSpace)) return "already native target color space";
                if (UseIcc && ProfilePath == "") return "no ICC profile selected";
                return "unknown";
            }
        }

        public string GPU => _output.PhysicalGPU.FullName;

        public bool UseEdid
        {
            set => UseIcc = !value;
            get => !UseIcc;
        }

        public bool UseIcc { set; get; }

        public string ProfilePath { set; get; }

        public bool CalibrateGamma { set; get; }

        public int SelectedGamma { set; get; }

        public double CustomGamma { set; get; }

        public double CustomPercentage { set; get; }

        public bool DisableOptimization { set; get; }

        public int Target { set; get; }

        public Colorimetry.ColorSpace EdidColorSpace { get; }

        private Colorimetry.ColorSpace TargetColorSpace => Colorimetry.ColorSpaces[Target];

        public Novideo.DitherControl DitherControl => _dither;

        public string DitherString
        {
            get
            {
                string[] types =
                {
                    "SpatialDynamic",
                    "SpatialStatic",
                    "SpatialDynamic2x2",
                    "SpatialStatic2x2",
                    "Temporal"
                };
                if (_dither.state == 2)
                {
                    return "Disabled (forced)";
                }
                if (_dither.state == 0 & _dither.bits == 0 && _dither.mode == 0)
                {
                    return "Disabled (default)";
                }
                var bits = (6 + 2 * _dither.bits).ToString();
                return bits + " bit " + types[_dither.mode] + " (" + (_dither.state == 0 ? "default" : "forced") + ")";
            }
        }

        public int BitDepth => _bitDepth;

        public void ApplyDither(int state, int bits, int mode)
        {
            try
            {
                Novideo.SetDitherControl(_output, state, bits, mode);
                _dither = Novideo.GetDitherControl(_output);
                OnPropertyChanged(nameof(DitherString));
                Logger.Log(LogLevel.Success, "Dither settings updated for " + Name + ": " + DitherString);
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, "Failed to apply dither settings for " + Name + ": " + e);
                TopMostMessageBox.Show(e.Message);
            }
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}