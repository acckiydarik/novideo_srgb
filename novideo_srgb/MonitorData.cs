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
            _clamped = Novideo.IsColorSpaceConversionActive(_output);
            ClampSdr = _clamped;
            _viewModel.SaveConfig();
            OnPropertyChanged(nameof(Clamped));
            TopMostMessageBox.Show(e.Message);
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
            try
            {
                UpdateClamp(value);
                ClampSdr = value;
                _viewModel.SaveConfig();
            }
            catch (Exception e)
            {
                HandleClampException(e, sourceSuffix);
                return;
            }

            _clamped = value;
            Logger.Log(value ? LogLevel.Success : LogLevel.Off,
                (value ? "Clamp enabled for " : "Clamp disabled for ") + Name + sourceSuffix);
            OnPropertyChanged(nameof(Clamped));
        }

        public void ReapplyClamp(bool manual = false)
        {
            try
            {
                var clamped = CanClamp && ClampSdr;
                var previous = _clamped;
                UpdateClamp(clamped);
                _clamped = clamped;
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