using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NMEA2000Analyzer
{
    public partial class AnomaliesWindow : Window
    {
        private readonly Action<AnomalyEntry>? _packetRequested;

        public AnomaliesWindow(
            IEnumerable<AnomalyEntry> entries,
            Action<AnomalyEntry>? packetRequested = null,
            string? titleSuffix = null)
        {
            _packetRequested = packetRequested;
            InitializeComponent();
            if (!string.IsNullOrWhiteSpace(titleSuffix))
            {
                Title = $"Anomalies - {titleSuffix}";
            }

            AnomaliesDataGrid.ItemsSource = entries;
        }

        private void AnomaliesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid dataGrid ||
                dataGrid.SelectedItem is not AnomalyEntry entry)
            {
                return;
            }

            _packetRequested?.Invoke(entry);
        }
    }

    public sealed class AnomalyEntry
    {
        public int SequenceNumber { get; set; }
        public int PreviousSequenceNumber { get; set; }
        public string Timestamp { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string Pgn { get; set; } = string.Empty;
        public string Anomaly { get; set; } = string.Empty;
        public string PreviousValue { get; set; } = string.Empty;
        public string CurrentValue { get; set; } = string.Empty;
        public string Delta { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }
}
