using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace novideo_srgb
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // Owned by the primary instance for the whole process lifetime. Kept in a static field
        // so the ownership is never garbage-collected away; disposed only after the pipe server
        // has been stopped (releasing it earlier would open a window where a CLI call sees
        // "not running" while the old server is still shutting down).
        private static Mutex _singleInstanceMutex;
        private static MainWindow _mainWindow;

        // Released early by the restart path: the replacement process creates the same mutex,
        // so holding it until process exit would make the new instance believe another copy is
        // already running and quit immediately.
        internal static void ReleaseSingleInstanceMutex()
        {
            var mutex = Interlocked.Exchange(ref _singleInstanceMutex, null);
            if (mutex == null) return;

            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
            }

            mutex.Dispose();
        }

        [STAThread]
        public static void Main(string[] args)
        {
            var parsed = CliArguments.Parse(args);

            if (parsed.Error != null)
            {
                // No GUI fallback: a misconfigured scheduled task / shortcut must fail with an
                // exit code, not hang on a modal box nobody can see.
                ConsoleOutput.Write("Error: " + parsed.Error + "\r\n\r\n" + CliHelp.Usage,
                    allowGuiFallback: false);
                Environment.Exit(2);
                return;
            }

            // --help/--version are fully local: no mutex, no pipe, no GUI. These two are the
            // only commands a user may reasonably run by double-clicking, so they are also the
            // only ones allowed to show a window when there is no console to print to.
            if (parsed.Command == CliCommandType.Help)
            {
                ConsoleOutput.Write(CliHelp.Usage, allowGuiFallback: true);
                Environment.Exit(0);
                return;
            }

            if (parsed.Command == CliCommandType.Version)
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                ConsoleOutput.Write("novideo_srgb " + version.Major + "." + version.Minor,
                    allowGuiFallback: true);
                Environment.Exit(0);
                return;
            }

            if (parsed.IsControlCommand)
            {
                Environment.Exit(RunControlCommand(parsed));
                return;
            }

            RunNormal(parsed);
        }

        // Control commands only PROBE for a running instance - they never create the mutex
        // (that would take single-instance ownership) and never start the GUI.
        private static int RunControlCommand(CliArguments parsed)
        {
            try
            {
                Mutex existing;
                if (!Mutex.TryOpenExisting(SingleInstance.MutexName, out existing))
                {
                    ConsoleOutput.Write("novideo_srgb is not running.", allowGuiFallback: false);
                    return 1;
                }

                existing.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                // The mutex exists but belongs to an instance with different privileges
                // (elevated vs not) - distinctly not "not running".
                ConsoleOutput.Write(
                    "Access denied - the running instance may have different privileges (elevated vs not)",
                    allowGuiFallback: false);
                return 6;
            }

            var result = CliClient.SendCommand(parsed);
            ConsoleOutput.Write(result.Text, allowGuiFallback: false);
            return result.Code;
        }

        private static void RunNormal(CliArguments parsed)
        {
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, SingleInstance.MutexName, out createdNew);

            // A process restarting itself after a render thread failure starts its replacement
            // before it has fully exited, so the mutex can still be held for a moment. Retry
            // briefly in that case - but only there: an ordinary second launch finds the mutex
            // held by a perfectly healthy instance, and waiting two seconds before activating
            // its window would be a visible regression.
            if (!createdNew && parsed.RestartHandover)
            {
                for (var attempt = 0; !createdNew && attempt < 20; attempt++)
                {
                    _singleInstanceMutex.Dispose();
                    Thread.Sleep(100);
                    _singleInstanceMutex = new Mutex(true, SingleInstance.MutexName, out createdNew);
                }
            }

            if (!createdNew)
            {
                // Another instance owns the mutex: ask it to bring its window to the
                // foreground instead of showing the old "Already running!" popup. The popup
                // remains only as a fallback when the pipe cannot be reached.
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;

                Logger.Log(LogLevel.Warning, "Startup blocked: another instance is already running");
                var result = CliClient.SendActivate();
                if (result.Code != 0)
                {
                    MessageBox.Show("Already running!");
                }

                Logger.Shutdown();
                return;
            }

            // Must happen before the first window exists: the flag is only honoured while the
            // composition partition is still healthy, so switching it later has no effect
            // (verified experimentally - see DEVELOPMENT.md).
            var persistedSoftwareFlag = MainViewModel.ReadForceSoftwareRenderingFlag();
            if (parsed.SoftwareRender || persistedSoftwareFlag)
            {
                try
                {
                    RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                    Logger.Log(LogLevel.Info, "Software rendering enabled for this session");
                }
                catch (Exception e)
                {
                    Logger.Log(LogLevel.Warning, "Could not enable software rendering: " + e.Message);
                }
            }

            if (persistedSoftwareFlag)
            {
                // One-shot, as promised to the user ("the next start will use software
                // rendering"): consume it now so the application returns to GPU rendering
                // afterwards. If the problem is still there, the loop-guard sets it again.
                MainViewModel.ClearForceSoftwareRenderingFlag();
            }

            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                _mainWindow = window;
                CliServer.Start(window.ViewModel, window.Dispatcher, window.RestoreWindowToForeground);
                window.Closed += delegate { CliServer.Stop(); };
                app.Run(window);
            }
            finally
            {
                CliServer.Stop();
                // May already be gone: the restart path releases it early so the replacement
                // process can claim it.
                ReleaseSingleInstanceMutex();
            }
        }

        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Storm suppression happens before Logger.Log on purpose: a zombied composition
            // partition rethrows on every window message, which produced 410 identical entries
            // (81% of the whole log file) during the incident this was built for.
            string aggregate;
            if (RenderThreadFailure.ShouldLog(e.Exception, out aggregate))
            {
                if (aggregate != null) Logger.Log(LogLevel.Error, aggregate);
                Logger.Log(LogLevel.Error, "Unhandled exception: " + e.Exception);
            }
            else if (aggregate != null)
            {
                Logger.Log(LogLevel.Error, aggregate);
            }

            if (RenderThreadFailure.IsCompositionFailure(e.Exception))
            {
                RenderThreadFailure.LogSnapshotOnce(e.Exception);

                if (RenderThreadFailure.RegisterFailure() && _mainWindow != null)
                {
                    // Confirmed zombie: the window will never paint again in this process, so
                    // hand over to a fresh one (or stay degraded if the loop-guard says
                    // restarting is not helping).
                    e.Handled = true;
                    _mainWindow.RestartAfterRenderFailure();
                    return;
                }
            }

            // Keep the tray app running after a UI-thread exception instead of silently
            // disappearing from the tray with no indication anything went wrong.
            e.Handled = true;
        }
    }
}