using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace NMEA2000Analyzer
{
    public partial class PgnStatistics : Window
    {
        public PgnStatistics(List<PgnStatisticsEntry> statistics)
        {
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
    }

    public class PgnStatisticsEntry
    {
        public string PGN { get; set; }
        public string Description { get; set; }
        public int Count { get; set; }
    }
}
