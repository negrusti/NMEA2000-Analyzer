using System.Configuration;
using System.Data;
using System.Windows;

namespace NMEA2000Analyzer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public Canboat.Rootobject CanboatRootNew { get; set; }
    }

}
