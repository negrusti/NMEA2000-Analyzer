using System.Windows;

namespace NMEA2000Analyzer
{
    public partial class JsonViewerWindow : Window
    {
        public JsonViewerWindow(string json)
        {
            InitializeComponent();
            JsonTextBox.Text = json;
        }
        public void UpdateContent(string jsonString)
        {
            // Assuming you have a TextBox or similar control to display JSON
            JsonTextBox.Text = jsonString;
        }
    }
}