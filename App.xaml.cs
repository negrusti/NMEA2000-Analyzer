using System.IO;
using System.Windows;

namespace NMEA2000Analyzer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public Canboat.Rootobject? CanboatRoot { get; set; }

        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();

            if (e.Args.Length == 0)
            {
                return;
            }

            var filePath = e.Args[0];
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"File not found: {filePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await mainWindow.LoadFileFromCommandLineAsync(filePath);
        }
    }

}
