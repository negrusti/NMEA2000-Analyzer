using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    /// <summary>
    /// Interaction logic for Devices.xaml
    /// </summary>
    public partial class Devices : Window
    {
        public ObservableCollection<DeviceStatisticsEntry> BindableDevices { get; }
            = new ObservableCollection<DeviceStatisticsEntry>();
        public int TotalUnassembledCount { get; }
        public int TotalAssembledCount { get; }
        public double TotalAvgBpsValue { get; }
        public double TotalPeakBpsValue { get; }
        public string TotalAvgBps { get; }
        public string TotalPeakBps { get; }

        public Devices(IEnumerable<DeviceStatisticsEntry> statistics)
        {
            var entries = statistics.ToList();
            TotalUnassembledCount = entries.Sum(device => device.UnassembledCount);
            TotalAssembledCount = entries.Sum(device => device.AssembledCount);
            TotalAvgBpsValue = entries.Sum(device => device.AvgBpsValue);
            TotalPeakBpsValue = entries.Sum(device => device.PeakBpsValue);
            TotalAvgBps = FormatBps(TotalAvgBpsValue);
            TotalPeakBps = FormatBps(TotalPeakBpsValue);

            InitializeComponent();
            DataContext = this;
            BindableDevices.Clear();
            foreach (var device in entries)
            {
                BindableDevices.Add(device);
            }
        }

        private static string FormatBps(double bytesPerSecond)
        {
            return bytesPerSecond.ToString("0.##");
        }
    }

    public class DeviceStatisticsEntry : Device
    {
        public int UnassembledCount { get; set; }
        public int AssembledCount { get; set; }
        public double AvgBpsValue { get; set; }
        public double PeakBpsValue { get; set; }
        public string AvgBps { get; set; } = string.Empty;
        public string PeakBps { get; set; } = string.Empty;
    }

    public sealed class BpsHighlightConverter : IValueConverter
    {
        private static readonly Brush AlertBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x00, 0x00));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is double numericValue && numericValue > 18000
                ? AlertBrush
                : Brushes.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
