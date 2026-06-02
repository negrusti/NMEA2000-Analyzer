using System.Windows;
using System.Windows.Threading;

namespace NMEA2000Analyzer
{
    public partial class CaptureProgressWindow : Window
    {
        public delegate bool RequestDeviceInfoHandler(out string errorMessage);

        private readonly DispatcherTimer _timer;
        private readonly Func<int> _getCapturedCount;
        private readonly RequestDeviceInfoHandler? _requestDeviceInfo;
        private readonly string _requestDeviceInfoTitle;

        public CaptureProgressWindow()
            : this(() => PCAN.GetCapturedCount(), requestDeviceInfo: PCAN.RequestProductInformationBroadcast)
        {
        }

        public CaptureProgressWindow(
            Func<int> getCapturedCount,
            string? sourceLabel = null,
            bool canRequestDeviceInfo = true,
            RequestDeviceInfoHandler? requestDeviceInfo = null,
            string requestDeviceInfoTitle = "PCAN Capture")
        {
            InitializeComponent();
            _getCapturedCount = getCapturedCount;
            _requestDeviceInfo = requestDeviceInfo ?? (canRequestDeviceInfo ? PCAN.RequestProductInformationBroadcast : null);
            _requestDeviceInfoTitle = requestDeviceInfoTitle;
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _timer.Tick += Timer_Tick;
            Loaded += (_, _) =>
            {
                UpdatePacketCount();
                _timer.Start();
            };
            Closed += (_, _) => _timer.Stop();

            if (!string.IsNullOrWhiteSpace(sourceLabel))
            {
                SourceTextBlock.Text = sourceLabel;
                SourceTextBlock.Visibility = Visibility.Visible;
            }

            RequestDeviceInfoButton.IsEnabled = canRequestDeviceInfo;
            RequestDeviceInfoButton.Visibility = canRequestDeviceInfo ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            UpdatePacketCount();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void RequestDeviceInfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_requestDeviceInfo == null)
            {
                return;
            }

            if (!_requestDeviceInfo(out var errorMessage))
            {
                MessageBox.Show(
                    this,
                    errorMessage,
                    _requestDeviceInfoTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            RequestDeviceInfoButton.IsEnabled = false;
        }

        private void UpdatePacketCount()
        {
            PacketCountRun.Text = _getCapturedCount().ToString("N0");
        }
    }
}
