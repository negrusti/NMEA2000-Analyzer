using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    public static class Globals
    {
        public static Canboat.Rootobject? CanboatRoot { get; set; }
        public static Dictionary<int, int>? PGNsWithMfgCode { get; set; }

        public static Dictionary<int, Canboat.Pgn>? UniquePGNs { get; set; }

        // (MatchingValue, Mask, Index of PGNList)
        public static ILookup<int, (ulong, ulong, int)> PGNListLookup { get; private set; }

        public static void InitializePGNLookup(List<(int PGN, ulong MatchValue, ulong Mask, int PGNIndex)> tupleList)
        {
            PGNListLookup = tupleList.ToLookup(tuple => tuple.PGN, tuple => (tuple.MatchValue, tuple.Mask, tuple.PGNIndex));
        }

        public static Dictionary<int, Device> Devices { get; set; } = new Dictionary<int, Device>();
    }
}
