namespace NMEA2000Analyzer
{
    public static class Globals
    {
        public static Canboat.Rootobject? CanboatRoot { get; set; }
        public static HashSet<int>? PGNsWithMfgCode { get; set; }

        public static Dictionary<int, Canboat.Pgn>? UniquePGNs { get; set; }

        // (PGN, MfgCode, IndustryCode)
        public static Dictionary<(int, int, int), Canboat.Pgn>? MfgCodeMapper { get; set; }

        public static Dictionary<int, string> Devices { get; set; } = new Dictionary<int, string>();
    }
}
