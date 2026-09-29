using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace novideo_srgb
{
    // Restarts the process after the WPF composition partition has been confirmed dead.
    //
    // This is the only cure that exists: the zombie HRESULT is latched inside the channel with
    // no way to reset it, so every window message keeps rethrowing until the process is gone.
    // All in-process repairs were tried and measured as useless (render mode switch, window
    // recreation, a window on a fresh UI thread, and simply waiting for the resource pressure
    // to pass) - see DEVELOPMENT.md.
    public static class RestartGuard
    {
        // A second failure this soon after a restart means restarting is not fixing anything -
        // most likely the machine itself is in a bad state - so the app stays up instead of
        // cycling through restarts.
        private const int LoopGuardMinutes = 10;

        private const uint MbIconWarning = 0x30;
        private const uint MbSystemModal = 0x1000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private static int _restartStarted;

        // Set once the loop-guard has decided restarting is pointless. A zombied partition
        // rethrows on every window message, so without this the next exception - arriving
        // milliseconds later - would walk straight back in here and restart after all, which
        // is exactly the loop the guard exists to prevent.
        private static bool _degraded;

        /// <summary>
        /// Performs the restart, or stays degraded if the loop-guard says restarting is not
        /// working. Returns only in the degraded case - otherwise the process exits.
        /// </summary>
        /// <param name="viewModel">Owner of the configuration file.</param>
        /// <param name="prepareShutdown">
        /// Releases everything that must not leak into the new instance: the tray icon, the
        /// global hotkey, the CLI pipe server, the poll timer and the single-instance mutex.
        /// </param>
        public static void RestartAfterRenderFailure(MainViewModel viewModel, Action prepareShutdown)
        {
            // Already decided to stay degraded: every further exception from the dead
            // partition must be ignored here.
            if (_degraded) return;

            // Re-entrancy guard: the exception handler can fire again while this method is
            // running (the dispatcher keeps pumping messages during Process.Start).
            if (Interlocked.Exchange(ref _restartStarted, 1) == 1) return;

            var now = DateTime.Now;
            var last = viewModel.LastAutoRestart;
            var withinGuard = last.HasValue && (now - last.Value).TotalMinutes < LoopGuardMinutes;

            if (withinGuard)
            {
                Logger.Log(LogLevel.Error,
                    "Render thread failed again " + (int)(now - last.Value).TotalMinutes +
                    " min after the previous automatic restart - not restarting again. " +
                    "The window will stay unresponsive, but the clamp, the tray icon, the hotkey " +
                    "and the command line keep working. Software rendering will be used on the next start.");

                // Software rendering cannot repair the current process, but it avoids the GPU
                // path on the next start, which is where it does help.
                viewModel.ForceSoftwareRendering = true;

                // LastAutoRestart is deliberately left in place: clearing it would make the
                // very next exception look like a first failure and trigger a restart.
                viewModel.SaveConfig(fatalOnError: false);

                // Latch before showing anything: the message box pumps messages, which can
                // deliver another composition exception straight back into this method.
                _degraded = true;

                ShowNativeMessage(
                    "Novideo sRGB lost its connection to the display driver twice in a row.\n\n" +
                    "The window will not redraw until you restart the application, but the sRGB clamp, " +
                    "the tray icon, the hotkey and the command line keep working.\n\n" +
                    "The next start will use software rendering.");

                // Deliberately NOT exiting: the tray icon, the global hotkey and the CLI server
                // all keep working (none of them go through WPF rendering), so quitting would
                // take clamp control away from the user for no benefit. _restartStarted stays
                // latched as well - _degraded is the authoritative stop, this is belt and
                // braces.
                return;
            }

            Logger.Log(LogLevel.Error,
                "WPF render thread failed - the window cannot recover in this process. Restarting.");

            viewModel.LastAutoRestart = now;
            viewModel.SaveConfig(fatalOnError: false);

            string executablePath;
            try
            {
                executablePath = Process.GetCurrentProcess().MainModule.FileName;
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, "Cannot determine the executable path, not restarting: " + e.Message);
                _restartStarted = 0;
                return;
            }

            try
            {
                // Releases the single-instance mutex among other things - it must happen before
                // the new process starts, otherwise the new instance finds the mutex taken and
                // exits with "Already running!" instead of taking over.
                prepareShutdown();
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, "Shutdown preparation failed before restart: " + e);
            }

            // Close the log before spawning the replacement: the writer holds the day's file
            // with FileShare.Read, so a new process starting while it is open would silently
            // run with logging disabled for the rest of its life.
            Logger.Shutdown();

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    // Software rendering for the replacement: the GPU path is what just failed,
                    // and a fresh process going straight back to it can crash outright inside
                    // the display driver (observed on the harness: an access violation in
                    // nvd3dumx.dll). This is a one-session measure passed on the command line,
                    // not a persisted setting - a later manual start returns to hardware
                    // rendering on its own.
                    Arguments = "-minimize --software-render --restart-handover",
                    UseShellExecute = false,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                });
            }
            catch
            {
                // Shown synchronously: this is the one path where the application disappears
                // for good, so the process must not exit until the user has actually read the
                // message. A background box would be destroyed by Environment.Exit below,
                // leaving the app to vanish from the tray without a word.
                ShowNativeMessage(
                    "Novideo sRGB lost its connection to the display driver and could not restart itself.\n\n" +
                    "Please start it again manually.",
                    waitForDismissal: true);

                Environment.Exit(1);
            }

            // Application.Shutdown() is not an option here: it closes windows, which issues
            // more UpdateWindowSettings calls into the dead composition channel and throws
            // again. Environment.Exit leaves immediately instead.
            Environment.Exit(0);
        }

        // WPF cannot draw anything at this point, so any dialog has to be a native one, and it
        // has to run off the UI thread - the dispatcher is busy rethrowing composition errors.
        private static void ShowNativeMessage(string text, bool waitForDismissal = false)
        {
            var thread = new Thread(() => MessageBoxW(IntPtr.Zero, text, "Novideo sRGB",
                MbIconWarning | MbSystemModal))
            {
                IsBackground = true
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (waitForDismissal)
            {
                // Bounded: a modal box nobody is at the keyboard for must not keep a broken
                // process alive indefinitely.
                thread.Join(TimeSpan.FromMinutes(2));
            }
        }
    }
}
