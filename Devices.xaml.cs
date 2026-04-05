using System.Collections.ObjectModel;
using System.Windows;
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

        public Devices(IEnumerable<DeviceStatisticsEntry> statistics)
        {
            InitializeComponent();
            DataContext = this;
            BindableDevices.Clear();
            foreach (var device in statistics)
            {
                BindableDevices.Add(device);
            }
        }
    }

    public class DeviceStatisticsEntry : Device
    {
        public int UnassembledCount { get; set; }
        public int AssembledCount { get; set; }
    }
}
