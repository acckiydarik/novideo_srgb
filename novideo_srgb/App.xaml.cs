using System;
using System.Reflection;
using System.Threading;
using System.Windows;
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

            RunNormal();
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

        private static void RunNormal()
        {
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, SingleInstance.MutexName, out createdNew);

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

            try
            {
                var app = new App();
                app.InitializeComponent();
                var window = new MainWindow();
                CliServer.Start(window.ViewModel, window.Dispatcher, window.RestoreWindowToForeground);
                window.Closed += delegate { CliServer.Stop(); };
                app.Run(window);
            }
            finally
            {
                CliServer.Stop();
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
        }

        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Log(LogLevel.Error, "Unhandled exception: " + e.Exception);
            // Keep the tray app running after a UI-thread exception instead of silently
            // disappearing from the tray with no indication anything went wrong.
            e.Handled = true;
        }
    }
}