using System.Collections.ObjectModel;
using System.Windows;

namespace NMEA2000Analyzer
{
    public partial class SupportedPgnsWindow : Window
    {
        public ObservableCollection<string> TransmitPgns { get; } = new();
        public ObservableCollection<string> ReceivePgns { get; } = new();
        public string TransmitHeader { get; }
        public string ReceiveHeader { get; }

        public SupportedPgnsWindow(string deviceLabel, IEnumerable<string> transmitPgns, IEnumerable<string> receivePgns)
        {
            InitializeComponent();

            foreach (var pgn in transmitPgns)
            {
                TransmitPgns.Add(pgn);
            }

            foreach (var pgn in receivePgns)
            {
                ReceivePgns.Add(pgn);
            }

            TransmitHeader = $"Transmit PGNs ({TransmitPgns.Count})";
            ReceiveHeader = $"Receive PGNs ({ReceivePgns.Count})";
            HeaderTextBlock.Text = deviceLabel;
            DataContext = this;
        }
    }
}
