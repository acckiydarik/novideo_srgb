using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace novideo_srgb
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
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