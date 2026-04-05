using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NMEA2000Analyzer
{
    public partial class PgnStatistics : Window
    {
        private readonly Action<PgnStatisticsEntry>? _graphRequested;

        public PgnStatistics(List<PgnStatisticsEntry> statistics, Action<PgnStatisticsEntry>? graphRequested = null)
        {
            _graphRequested = graphRequested;
            InitializeComponent();
            PgnStatisticsDataGrid.ItemsSource = statistics;
        }

        private void IncludeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedPgns = GetSelectedPgns();

            if (Owner is MainWindow mainWindow)
            {
                mainWindow.IncludePgns(selectedPgns);
            }
        }

        private void ExcludeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedPgns = GetSelectedPgns();

            if (Owner is MainWindow mainWindow)
            {
                mainWindow.ExcludePgns(selectedPgns);
            }
        }

        private List<string> GetSelectedPgns()
        {
            return PgnStatisticsDataGrid.SelectedItems
                .OfType<PgnStatisticsEntry>()
                .Select(entry => entry.PGN)
                .Where(pgn => !string.IsNullOrWhiteSpace(pgn))
                .Distinct()
                .ToList();
        }

        private void PgnRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGridRow row ||
                row.Item is not PgnStatisticsEntry entry)
            {
                return;
            }

            row.IsSelected = true;
            row.Focus();
            _graphRequested?.Invoke(entry);
        }
    }

    public class PgnStatisticsEntry
    {
        public string PGN { get; set; }
        public string Description { get; set; }
        public string SourceAddresses { get; set; }
        public int Count { get; set; }
    }
}
