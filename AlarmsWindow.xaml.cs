using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NMEA2000Analyzer
{
    public partial class AlarmsWindow : Window
    {
        private readonly Action<AlarmHistoryEntry>? _packetRequested;

        public AlarmsWindow(
            IEnumerable<AlarmHistoryEntry> entries,
            Action<AlarmHistoryEntry>? packetRequested = null,
            string? titleSuffix = null)
        {
            _packetRequested = packetRequested;
            InitializeComponent();
            if (!string.IsNullOrWhiteSpace(titleSuffix))
            {
                Title = $"Alarms - {titleSuffix}";
            }

            AlarmsDataGrid.ItemsSource = entries;
        }

        private void AlarmsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid dataGrid ||
                dataGrid.SelectedItem is not AlarmHistoryEntry entry)
            {
                return;
            }

            _packetRequested?.Invoke(entry);
        }
    }

    public sealed class AlarmHistoryEntry
    {
        public int SequenceNumber { get; set; }
        public string Timestamp { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string Pgn { get; set; } = string.Empty;
        public string Event { get; set; } = string.Empty;
        public string Alarm { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }
}
