using System.Collections.Generic;
using System.Windows;

namespace NMEA2000Analyzer
{
    public partial class AlarmsWindow : Window
    {
        public AlarmsWindow(IEnumerable<AlarmHistoryEntry> entries)
        {
            InitializeComponent();
            AlarmsDataGrid.ItemsSource = entries;
        }
    }

    public sealed class AlarmHistoryEntry
    {
        public string Timestamp { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string Pgn { get; set; } = string.Empty;
        public string Event { get; set; } = string.Empty;
        public string Alarm { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }
}
