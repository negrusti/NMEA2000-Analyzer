using System.Collections.Generic;
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
    }

    public class PgnStatisticsEntry
    {
        public string PGN { get; set; }
        public string Description { get; set; }
        public int Count { get; set; }
    }
}
