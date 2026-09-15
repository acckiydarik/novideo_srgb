using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms;
using System.Xml.Linq;
using MessageBox = System.Windows.Forms.MessageBox;

namespace novideo_srgb
{
    public partial class LogWindow : Window
    {
        private bool _autoScroll = true;
        private DateTime _lastViewedCutoff;
        private System.Collections.Specialized.NotifyCollectionChangedEventHandler _entriesChangedHandler;

        private static readonly string StatePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log_window.xml");

        public LogWindow()
        {
            InitializeComponent();
            ((CollectionViewSource)Resources["GroupedLogs"]).Source = Logger.Entries;

            _lastViewedCutoff = DateTime.Now;
            RestoreWindowState();

            ((CollectionViewSource)Resources["GroupedLogs"]).GroupDescriptions.Add(
                new PropertyGroupDescription("Timestamp", new NewEntryGroupKeyConverter(_lastViewedCutoff)));

            UpdateStatsText();
            _entriesChangedHandler = delegate { UpdateStatsText(); };
            Logger.Entries.CollectionChanged += _entriesChangedHandler;
            Logger.Flushed += OnLoggerFlushed;

            Closing += delegate
            {
                Logger.Entries.CollectionChanged -= _entriesChangedHandler;
                Logger.Flushed -= OnLoggerFlushed;
                SaveWindowState();
            };
        }

        private void OnLoggerFlushed()
        {
            Dispatcher.Invoke(UpdateStatsText);
        }

        private void UpdateStatsText()
        {
            var count = Logger.Entries.Count;
            var sizeKb = Logger.GetLogFileSizeBytes() / 1024.0;
            StatsText.Text = count + " entries, " + sizeKb.ToString("F1") + " KB";
        }

        private class NewEntryGroupKeyConverter : IValueConverter
        {
            private readonly DateTime _cutoff;

            public NewEntryGroupKeyConverter(DateTime cutoff)
            {
                _cutoff = cutoff;
            }

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return (DateTime)value > _cutoff ? "new" : "old";
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                throw new NotSupportedException();
            }
        }

        private void RestoreWindowState()
        {
            if (!File.Exists(StatePath)) return;

            try
            {
                var xElem = XElement.Load(StatePath);
                var bounds = new Rect(
                    (double)xElem.Attribute("left"),
                    (double)xElem.Attribute("top"),
                    (double)xElem.Attribute("width"),
                    (double)xElem.Attribute("height"));

                var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

                if (virtualScreen.IntersectsWith(bounds))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = bounds.Left;
                    Top = bounds.Top;
                    Width = bounds.Width;
                    Height = bounds.Height;

                    if ((bool)xElem.Attribute("maximized"))
                    {
                        WindowState = WindowState.Maximized;
                    }
                }

                var lastViewedAttr = xElem.Attribute("lastViewed");
                if (lastViewedAttr != null)
                {
                    _lastViewedCutoff = (DateTime)lastViewedAttr;
                }
            }
            catch
            {
            }
        }

        private void SaveWindowState()
        {
            try
            {
                var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                var xElem = new XElement("logWindow",
                    new XAttribute("left", bounds.Left),
                    new XAttribute("top", bounds.Top),
                    new XAttribute("width", bounds.Width),
                    new XAttribute("height", bounds.Height),
                    new XAttribute("maximized", WindowState == WindowState.Maximized),
                    new XAttribute("lastViewed", DateTime.Now));
                xElem.Save(StatePath);
            }
            catch
            {
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Clear all logs?", "Clear logs", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == System.Windows.Forms.DialogResult.Yes)
            {
                Logger.Clear();
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt",
                FileName = "novideo_srgb_log_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                var lines = new string[Logger.Entries.Count];
                for (var i = 0; i < Logger.Entries.Count; i++)
                {
                    var entry = Logger.Entries[i];
                    lines[i] = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") + " [" +
                               entry.Level.ToString().ToUpper() + "] " + entry.Message;
                }

                File.WriteAllLines(dialog.FileName, lines);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to export logs: " + ex.Message);
            }
        }

        private void LogListView_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0)
            {
                _autoScroll = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1;
            }

            if (_autoScroll && e.ExtentHeightChange > 0 && e.OriginalSource is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToEnd();
            }
        }
    }
}
