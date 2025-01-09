using System.Windows;

namespace NMEA2000Analyzer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public Canboat.Rootobject? CanboatRoot { get; set; }
    }

}
