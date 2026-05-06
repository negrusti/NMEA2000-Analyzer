using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Windows;

namespace NMEA2000Analyzer
{
    public partial class PgnDefinitionEditorWindow : Window
    {
        public string DefinitionJson => DefinitionTextBox.Text;

        public PgnDefinitionEditorWindow(int pgn, string definitionJson, bool existsInLocal, bool existsInCanboat)
        {
            InitializeComponent();

            Title = $"Edit PGN Definition {pgn}";
            DefinitionTextBox.Text = definitionJson;
            DefinitionSourceTextBlock.Text = existsInLocal
                ? $"Editing PGN {pgn} from local.json. Saving updates this entry only."
                : existsInCanboat
                    ? $"PGN {pgn} exists in canboat.json but not local.json. Saving will add an override to local.json."
                    : $"PGN {pgn} is not defined yet. Saving will create a new entry in local.json.";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var parsed = JToken.Parse(DefinitionTextBox.Text);
                if (parsed is not JObject definitionObject)
                {
                    MessageBox.Show("Definition JSON must be a JSON object.", "Invalid JSON", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DefinitionTextBox.Text = definitionObject.ToString(Formatting.Indented);
                DialogResult = true;
            }
            catch (JsonReaderException ex)
            {
                MessageBox.Show($"Definition JSON is invalid: {ex.Message}", "Invalid JSON", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
