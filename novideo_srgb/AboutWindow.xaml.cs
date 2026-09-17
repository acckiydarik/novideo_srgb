using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;

namespace novideo_srgb
{
    public partial class AboutWindow : Window
    {
        // "Nothing to do" stays quiet; the other two states have to be noticeable at a glance,
        // otherwise a uniformly grey line reads as decoration and gets ignored.
        private static readonly Brush NeutralBrush = CreateBrush(0x80, 0x80, 0x80);
        private static readonly Brush UpdateBrush = CreateBrush(0x1B, 0x6E, 0x2F);
        private static readonly Brush WarningBrush = CreateBrush(0xB0, 0x5A, 0x00);

        private static Brush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private readonly System.Threading.CancellationTokenSource _cancellation =
            new System.Threading.CancellationTokenSource();
        private bool _isClosed;

        public AboutWindow()
        {
            InitializeComponent();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"novideo_srgb v{version.Major}.{version.Minor}";

            Closed += delegate
            {
                _isClosed = true;
                _cancellation.Cancel();
                // Safe after Cancel(): the exception filter only reads IsCancellationRequested,
                // which stays valid on a disposed source.
                _cancellation.Dispose();
            };

            // Fire and forget: the window must appear immediately, so the network call runs
            // after it is already on screen rather than blocking construction.
            CheckForUpdates();
        }

        // async void because it is started from the constructor rather than awaited. Unlike an
        // async Task, an unhandled exception here would tear down the whole process, so the
        // entire body is wrapped - any failure just becomes "could not check".
        private async void CheckForUpdates()
        {
            UpdateCheckResult result;
            try
            {
                result = await UpdateChecker.CheckAsync(_cancellation.Token);
            }
            catch (System.OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                // The window was closed while the request was in flight - expected, not an error.
                // The filter matters: HttpClient reports its own timeout as TaskCanceledException,
                // which also derives from OperationCanceledException. Without checking whose
                // cancellation this was, a lost connection would be swallowed here and the label
                // would stay on "Checking for updates..." forever.
                return;
            }
            catch (System.Exception)
            {
                result = UpdateCheckResult.Failed();
            }

            // Cancelling the token does not retract a continuation that was already scheduled
            // on the dispatcher, so the window can legitimately be gone by now.
            if (_isClosed) return;

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    // Show the version the way the tag spelled it (4.6.1 stays 4.6.1).
                    UpdateRun.Text = $"Version {result.Version} is available.";
                    UpdateText.Foreground = UpdateBrush;
                    UpdateText.FontWeight = FontWeights.SemiBold;
                    ReleaseLink.NavigateUri = new System.Uri(result.ReleaseUrl);
                    ReleaseLinkText.Visibility = Visibility.Visible;
                    Logger.Log(LogLevel.Info,
                        "Update available: version " + result.Version + " (" + result.ReleaseUrl + ")");
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateRun.Text = "You are using the latest version.";
                    UpdateText.Foreground = NeutralBrush;
                    break;
                default:
                    // A failure is usually transient (no connection yet, API rate limit, a
                    // sleeping laptop that just woke up), so offer a retry in place instead of
                    // making the user close and reopen the window.
                    UpdateRun.Text = "Unable to check for updates.";
                    UpdateText.Foreground = WarningBrush;
                    RetryLinkText.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void OnRetryClick(object sender, RoutedEventArgs e)
        {
            // Hiding the link doubles as a guard against repeated clicks while a check is
            // already running.
            RetryLinkText.Visibility = Visibility.Collapsed;
            UpdateRun.Text = "Checking for updates...";
            UpdateText.Foreground = NeutralBrush;
            CheckForUpdates();
        }

        private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            var processStartInfo = new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(processStartInfo);
        }
    }
}