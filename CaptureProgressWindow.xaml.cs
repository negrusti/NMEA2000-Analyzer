using System.Windows;
using System.Windows.Threading;

namespace NMEA2000Analyzer
{
    public partial class CaptureProgressWindow : Window
    {
        private readonly DispatcherTimer _timer;

        public CaptureProgressWindow()
        {
            InitializeComponent();
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
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            UpdatePacketCount();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void UpdatePacketCount()
        {
            PacketCountRun.Text = PCAN.GetCapturedCount().ToString("N0");
        }
    }
}
