using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Windows;

namespace NMEA2000Analyzer
{
    public partial class PgnDefinitionEditorWindow : Window
    {
        private readonly MatchSuggestion? _matchSuggestion;
        private readonly bool _existsInLocal;

        public string DefinitionJson => DefinitionTextBox.Text;
        public bool ClearRequested { get; private set; }

        public sealed class MatchSuggestion
        {
            public required int ManufacturerCode { get; init; }
            public required string ManufacturerDescription { get; init; }
            public required int IndustryCode { get; init; }
            public required string IndustryDescription { get; init; }
        }

        public PgnDefinitionEditorWindow(
            int pgn,
            string definitionJson,
            bool existsInLocal,
            bool existsInCanboat,
            MatchSuggestion? matchSuggestion)
        {
            InitializeComponent();
            _matchSuggestion = matchSuggestion;
            _existsInLocal = existsInLocal;

            Title = $"Edit PGN Definition {pgn}";
            DefinitionTextBox.Text = definitionJson;
            DefinitionSourceTextBlock.Text = existsInLocal
                ? $"Editing PGN {pgn} from local.json. Saving updates this entry only."
                : existsInCanboat
                    ? $"PGN {pgn} exists in canboat.json but not local.json. Saving will add an override to local.json."
                    : $"PGN {pgn} is not defined yet. Saving will create a new entry in local.json.";

            ClearDefinitionButton.IsEnabled = existsInLocal;

            if (_matchSuggestion == null)
            {
                AddManufacturerMatchButton.IsEnabled = false;
                MatchHintTextBlock.Text = "Selected packet does not contain enough data to derive manufacturer matching.";
            }
            else
            {
                MatchHintTextBlock.Text =
                    $"Uses selected packet values: mfg {_matchSuggestion.ManufacturerCode} ({_matchSuggestion.ManufacturerDescription}), " +
                    $"industry {_matchSuggestion.IndustryCode} ({_matchSuggestion.IndustryDescription}).";
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ClearRequested = false;

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

        private void ClearDefinitionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_existsInLocal)
            {
                return;
            }

            var result = MessageBox.Show(
                "Remove this PGN definition from local.json?",
                "Clear Definition",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            ClearRequested = true;
            DialogResult = true;
        }

        private void AddManufacturerMatchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_matchSuggestion == null)
            {
                return;
            }

            try
            {
                var parsed = JToken.Parse(DefinitionTextBox.Text);
                if (parsed is not JObject definitionObject)
                {
                    MessageBox.Show("Definition JSON must be a JSON object.", "Invalid JSON", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var fields = definitionObject["Fields"] as JArray;
                if (fields == null)
                {
                    fields = new JArray();
                    definitionObject["Fields"] = fields;
                }

                UpsertField(fields, "manufacturerCode", CreateManufacturerCodeField(_matchSuggestion));
                UpsertField(fields, "reserved", CreateReservedField());
                UpsertField(fields, "industryCode", CreateIndustryCodeField(_matchSuggestion));
                NormalizeFieldOrder(fields);
                definitionObject["FieldCount"] = fields.Count;

                DefinitionTextBox.Text = definitionObject.ToString(Formatting.Indented);
            }
            catch (JsonReaderException ex)
            {
                MessageBox.Show($"Definition JSON is invalid: {ex.Message}", "Invalid JSON", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetFastPacketButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var parsed = JToken.Parse(DefinitionTextBox.Text);
                if (parsed is not JObject definitionObject)
                {
                    MessageBox.Show("Definition JSON must be a JSON object.", "Invalid JSON", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                definitionObject["Type"] = "Fast";
                definitionObject["Complete"] = false;
                definitionObject["FieldCount"] = 0;
                definitionObject["Fields"] = new JArray();
                definitionObject.Remove("Length");
                definitionObject.Remove("MinLength");
                definitionObject.Remove("RepeatingFieldSet1Size");
                definitionObject.Remove("RepeatingFieldSet1StartField");
                definitionObject.Remove("RepeatingFieldSet1CountField");
                definitionObject.Remove("RepeatingFieldSet2Size");
                definitionObject.Remove("RepeatingFieldSet2StartField");
                definitionObject.Remove("RepeatingFieldSet2CountField");

                DefinitionTextBox.Text = definitionObject.ToString(Formatting.Indented);
            }
            catch (JsonReaderException ex)
            {
                MessageBox.Show($"Definition JSON is invalid: {ex.Message}", "Invalid JSON", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void UpsertField(JArray fields, string fieldId, JObject template)
        {
            var existingField = fields
                .OfType<JObject>()
                .FirstOrDefault(field => string.Equals(field["Id"]?.Value<string>(), fieldId, StringComparison.Ordinal));

            if (existingField == null)
            {
                var insertIndex = fieldId switch
                {
                    "manufacturerCode" => 0,
                    "reserved" => Math.Min(fields.Count, 1),
                    "industryCode" => Math.Min(fields.Count, 2),
                    _ => fields.Count
                };

                fields.Insert(insertIndex, template);
                return;
            }

            foreach (var property in template.Properties())
            {
                existingField[property.Name] = property.Value.DeepClone();
            }
        }

        private static void NormalizeFieldOrder(JArray fields)
        {
            for (var index = 0; index < fields.Count; index++)
            {
                if (fields[index] is JObject fieldObject)
                {
                    fieldObject["Order"] = index + 1;
                }
            }
        }

        private static JObject CreateManufacturerCodeField(MatchSuggestion suggestion)
        {
            return new JObject
            {
                ["Order"] = 1,
                ["Id"] = "manufacturerCode",
                ["Name"] = "Manufacturer Code",
                ["BitLength"] = 11,
                ["BitOffset"] = 0,
                ["BitStart"] = 0,
                ["Resolution"] = 1,
                ["Signed"] = false,
                ["FieldType"] = "LOOKUP",
                ["RangeMin"] = 0,
                ["RangeMax"] = 2044,
                ["LookupEnumeration"] = "MANUFACTURER_CODE",
                ["Match"] = suggestion.ManufacturerCode,
                ["PartOfPrimaryKey"] = true,
                ["Description"] = suggestion.ManufacturerDescription
            };
        }

        private static JObject CreateReservedField()
        {
            return new JObject
            {
                ["Order"] = 2,
                ["Id"] = "reserved",
                ["Name"] = "Reserved",
                ["BitLength"] = 2,
                ["BitOffset"] = 11,
                ["BitStart"] = 3,
                ["Resolution"] = 1,
                ["Signed"] = false,
                ["FieldType"] = "RESERVED"
            };
        }

        private static JObject CreateIndustryCodeField(MatchSuggestion suggestion)
        {
            return new JObject
            {
                ["Order"] = 3,
                ["Id"] = "industryCode",
                ["Name"] = "Industry Code",
                ["BitLength"] = 3,
                ["BitOffset"] = 13,
                ["BitStart"] = 5,
                ["Resolution"] = 1,
                ["Signed"] = false,
                ["FieldType"] = "LOOKUP",
                ["RangeMin"] = 0,
                ["RangeMax"] = 6,
                ["LookupEnumeration"] = "INDUSTRY_CODE",
                ["Match"] = suggestion.IndustryCode,
                ["PartOfPrimaryKey"] = true,
                ["Description"] = suggestion.IndustryDescription
            };
        }
    }
}
