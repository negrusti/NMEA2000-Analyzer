using System;
using System.Globalization;

namespace NMEA2000Analyzer
{
    internal static class CanBusUtilities
    {
        public static uint BuildCanId(MainWindow.Nmea2000Record record, byte? sourceOverride = null, byte? destinationOverride = null)
        {
            var priority = int.TryParse(record.Priority, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPriority)
                ? parsedPriority
                : 0;
            var pgn = int.TryParse(record.PGN, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPgn)
                ? parsedPgn
                : 0;
            var source = sourceOverride ?? ParseAddressOrDefault(record.Source, 0);
            var destination = destinationOverride ?? ParseAddressOrDefault(record.Destination, 255);

            return BuildCanId((uint)pgn, destination, source, (byte)Math.Clamp(parsedPriority, 0, 7));
        }

        public static uint BuildCanId(uint pgn, byte destination, byte source, byte priority)
        {
            var pgnField = pgn;
            var pf = (pgnField >> 8) & 0xFF;
            if (pf < 0xF0)
            {
                pgnField |= destination;
            }

            return ((uint)priority << 26) | (pgnField << 8) | source;
        }

        public static bool IsDestinationSpecificPgn(string? pgnText)
        {
            return uint.TryParse(pgnText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgn) &&
                IsDestinationSpecificPgn(pgn);
        }

        public static bool IsDestinationSpecificPgn(uint pgn)
        {
            var pf = (pgn >> 8) & 0xFF;
            return pf < 0xF0;
        }

        public static byte ParseAddressOrDefault(string? value, byte fallback)
        {
            return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }
    }
}
