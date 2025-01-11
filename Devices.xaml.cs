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

        public ObservableCollection<KeyValuePair<int, Device>> BindableDevices { get; }
            = new ObservableCollection<KeyValuePair<int, Device>>();

        public Devices()
        {
            InitializeComponent();
            DataContext = this;
            BindableDevices.Clear();
            foreach (var kvp in Globals.Devices)
            {
                BindableDevices.Add(kvp);
            }
        }
    }
}
