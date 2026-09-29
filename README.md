## [Download latest release](https://github.com/acckiydarik/novideo_srgb/releases/latest/download/release.zip)

# About this fork
This is a fork of the original [novideo_srgb](https://github.com/ledoge/novideo_srgb) by ledoge, which has seen no commits or maintainer activity since March 2024. It keeps the same undocumented-NVAPI-based clamping approach and adds automatic retrying of clamp changes that newer NVIDIA drivers reject, command line control, a global hotkey, and a number of smaller interface improvements — see "Fork-specific features" below for details.

Licensed under GPLv3, same as the original project — see [LICENSE](LICENSE).

# About
This tool uses an undocumented NVIDIA API, supported on Fermi and later, to convert colors before sending them to a wide gamut monitor to effectively clamp it to sRGB (alternatively: Display P3, Adobe RGB or BT.2020), based on the chromaticities provided in its EDID. AMD supports this as a hidden setting in their drivers, but NVIDIA doesn't because ???.

ICC profiles are also supported and can be used in two different ways. By default, only the primary coordinates from the ICC profile will be used in place of the values reported in the EDID. This is useful if you want to use a profile created by someone else without taking their gamma/grayscale balance data into account, as that can vary a lot between units. If you enable the `Calibrate gamma to` checkbox, a full LUT-Matrix-LUT calibration will be applied. This is similar to the hardware calibration supported by some monitors and can be used to achieve great color and grayscale accuracy on well-behaved displays.

# Usage
Extract `release.zip` somewhere under your user directory and run `novideo_srgb.exe`. To enable/disable the sRGB clamp for a monitor, simply toggle the "Clamped" checkbox. For using ICC profiles and configuring dithering, click the "Advanced" button.

Generally, the clamp should persist through reboots and driver updates, but it can break sometimes. You can choose to leave the application running minimized in the background to have it automatically reapply the clamp and also handle HDR toggling – see the section "HDR and automatic reapplying" below. 

# Notes for use with EDID data
* If the checkbox for a monitor is locked, it means that the EDID is reporting the sRGB primaries as the monitor's primaries, so the monitor is either natively sRGB or uses an sRGB emulation mode by default. If this is not the case, complain to the manufacturer about the EDID being wrong, and try to find an ICC profile for your monitor to use instead of the EDID data.

* The reported white point is not taken into account when calculating the color space conversion matrix. Instead, the monitor is always assumed to be calibrated to D65 white.

# Notes for use with ICC profiles

* For the gamma options to work properly, the profile must report the display's black point accurately. DisplayCAL's default settings, e.g. with the sRGB preset, work fine.
* Since the color space conversion is done on the GPU side, the ICC profile must not be selected/loaded in Windows or any other application. If you want, you can do another profiling run on top of the active calibration and then use this profile in applications that support color management to achieve even better color accuracy.
* To achieve optimal results, consider creating a custom testchart in DisplayCAL with a high number of neutral (grayscale) patches, such as 256. With that, a grayscale calibration (setting "Tone curve" to anything other than "As measured") should be unnecessary unless your display lacks RGB gain controls, but can lead to better accuracy on some poorly behaved displays. The number of colored patches should not matter much. Additionally, configuring DisplayCAL to generate a "Curves + matrix" profile with "Black point compensation" disabled should also result in a lower average error than using an XYZ LUT profile. Having dithering enabled during profiling also seems to have a positive impact, see [here](https://github.com/ledoge/novideo_srgb/issues/79#issuecomment-1817220136). This advice is based on what worked well for a handful of users, so if you have anything else to add, please let me know.
* The option "Disable 8-bit color optimization" can be used to get better color accuracy in true 10-bit workflows at the cost of 8-bit accuracy. Only enable this if you really know you're working with 10-bit color.
* Only the VCGT (if present), TRC and PCS matrix parts of an ICC profile are used. If present, the A2B1 data is used to calculate (hopefully) higher quality TRC and PCS matrix values.

# HDR and automatic reapplying

Any change in the display setup (such as a monitor being added/removed) will cause the clamp to be reapplied on all monitors, as long as the application is running in the background. The main purpose of this is to handle HDR being toggled in Windows, as the clamp will automatically be disabled for monitors for which HDR is enabled (since colors would get messed up otherwise). Additionally, you can use the "Reapply" button to manually reapply the clamp in case something breaks (e.g. due to a driver bug).

If you want to run it on boot, you can enable the "Run at startup" checkbox, which will use the `-minimize` command line argument to make it start hidden in the tray directly (see "Close to tray" below for how closing/minimizing the window behaves).

# Fork-specific features

* **Command line control** — enable, disable, toggle or query the clamp from scripts; see "Command line" below.
* **Global hotkey** — toggle the clamp on all monitors from anywhere (`Hotkey` button, or the tray menu).
* **Tray icon state** — colored while the clamp is active, grayscale when it isn't.
* **Clamped row highlight** — active clamps are highlighted green in the monitor list.
* **Pending state in the UI** — a change still waiting for the driver shows an orange row and a dimmed checkbox.
* **Reason tooltips** — hovering the "Clamped" checkbox explains why it is unavailable or still being applied.
* **Log window** — history of clamp changes, startup checks and NVAPI errors, with export (`Logs` button, or the tray menu). Log files rotate daily and are kept for 30 days; the window shows the most recent 6000 entries and has an `Open folder` button for the full history.
* **Error indicator** — the `Logs` button shows a red count when errors have been logged since you last opened the log window, and returns to normal once you close it.
* **Resizable window with saved layout** — resize, maximize and sort columns; size, position, maximized state and column sorting are restored on the next start.
* **Close to tray** — the X button hides to the tray instead of quitting; minimize keeps standard taskbar behavior.
* **Single instance activation** — launching the app again brings the existing window to the front.
* **Tray tip on first hide** — a one-time notification points out where the tray icon is (repeatable via the `Tip` button).
* **Auto-reapply after monitor standby** — the clamp is restored when a sleeping monitor wakes up ([upstream issue #46](https://github.com/ledoge/novideo_srgb/issues/46)).
* **Settings preserved for disconnected monitors** — settings of a temporarily unplugged display are kept in the config.
* **Automatic retry on driver rejection** — rejected clamp changes are retried in the background instead of raising an error; see "Known issues".
* **Recovery from display driver failures** — if the WPF render thread dies (typically when a fullscreen game releases the GPU), the application restarts itself and restores its state; see "Known issues".
* **Update check** — the About window checks GitHub for a newer release when opened. Nothing is sent or downloaded automatically.

# Command line

Control an already running instance without opening the window:

```
novideo_srgb.exe --help             Show usage and exit codes
novideo_srgb.exe --version          Print the application version
novideo_srgb.exe --enable [index]   Enable the sRGB clamp
novideo_srgb.exe --disable [index]  Disable the sRGB clamp
novideo_srgb.exe --toggle [index]   Toggle the sRGB clamp
novideo_srgb.exe --status [index]   Print clamp state without changing it
```

`index` is the 1-based monitor number shown in the `#` column. Without it, the command applies to all monitors, matching the global hotkey. Control commands require a running instance and never start one; run `--help` for the full list of exit codes.

Two options control how the application starts instead of controlling a running one. They do not use the exit codes above, and neither can be combined with a control command:

```
novideo_srgb.exe -minimize          Start hidden in the tray
novideo_srgb.exe --software-render  Start with GPU rendering disabled
```

`--software-render` draws the window on the CPU. Use it if the window stays blank or never redraws after a display driver problem — see "Known issues". The clamp itself is unaffected, since it is applied by the driver rather than drawn by the application.

This is a GUI executable, so an interactive `cmd`/PowerShell prompt returns immediately without waiting for it. Batch files and Task Scheduler work as expected; for interactive use run it via `start /wait novideo_srgb.exe --status` or `Start-Process -Wait`.

# Known issues

* Since version 531.79, the NVIDIA driver rejects any attempt to set a color space conversion while HDR is enabled with error -104 (`NVAPI_NOT_SUPPORTED`). This means that the HDR handling mentioned above does not work anymore. I don't know whether this is a driver bug or an intentional change, but I don't think I can do anything to fix it.

* Some users have also reported error -104 without HDR ever being involved, on certain driver versions (see [upstream issue #131](https://github.com/ledoge/novideo_srgb/issues/131) and [#138](https://github.com/ledoge/novideo_srgb/issues/138)). On newer drivers the write is rejected depending on the display's presentation state, while the previously applied clamp keeps working. This fork handles those rejections automatically: the desired state is kept as pending and quietly retried in the background until the driver accepts it. Progress is visible as an orange row in the main window, an "(applying...)" suffix on the tray tooltip, and entries in the log window. A dialog is shown only for a manually triggered change that could not be applied within a few seconds — retries continue after it.

* The color space transform does not get applied properly to the mouse cursor, which results in it having wrong gamma and colors. This should be hardly noticeable with the default Windows cursor. Workaround: Force software rendering of the cursor, e.g. using [SoftCursor](https://www.monitortests.com/forum/Thread-SoftCursor).

* Windows can tear down the graphics stack under a running application — most often when a fullscreen game exits or a display driver resets. When that happens, WPF marks its composition channel dead and every window operation from then on throws, so the window stops redrawing while the process keeps running. This cannot be repaired from inside the process: switching to software rendering, recreating the window and waiting it out were all measured and none of them restore the channel (the same conclusion the WPF team documents in [dotnet/wpf#10192](https://github.com/dotnet/wpf/issues/10192)). This fork detects the failure and restarts itself, preserving the clamp state, the tray icon, the hotkey and the window layout; the replacement process starts with GPU rendering disabled, since the GPU path is what just failed. If a second failure follows within ten minutes, restarting is clearly not helping, so the application stays up in a degraded state instead of looping — the window will not redraw, but the clamp, the tray icon, the hotkey and the command line keep working, and the next start uses software rendering automatically.
* Windows HDR is handled properly, but NVAPI HDR, which some applications use to output HDR even though Windows HDR is off, will result in wrong colors while the clamp is active. To work around this, you can either enable Windows HDR or disable the clamp manually before launching such applications.

# Dithering

Applying any kind of calibration on the GPU-level usually results in banding unless dithering is used. By default, NVIDIA GPUs do not apply dithering to full range RGB output. Therefore, it is recommended that you use the dither controls to enable and configure dithering. "Bits" should be set to match the bit depth of your GPU output, and "Mode" can be set to whatever looks best to you. Note that "Temporal" works by rapidly switching between colors, which some people's eyes are sensitive to.
