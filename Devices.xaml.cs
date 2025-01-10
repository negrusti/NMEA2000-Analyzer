using System.Windows;
namespace NMEA2000Analyzer
{
    /// <summary>
    /// Interaction logic for Devices.xaml
    /// </summary>
    public partial class Devices : Window
    {
        public Devices()
        {
            InitializeComponent();
            DevicesDataGrid.ItemsSource = Globals.Devices;
        }
    }
}
