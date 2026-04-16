using System.Globalization;
using System.Text.Json.Nodes;

namespace NMEA2000Analyzer
{
    internal static class Seatalk1Parser
    {
        private const int RaymarineManufacturerCode = 1851;
        private const byte MarineIndustryCode = 4;
        private const byte Seatalk1ProprietaryId = 0xF0;
        private const byte Seatalk1NmeaCommand = 0x81;
        private const int SeatalkOffset = 4;

        private delegate void Decoder(Datagram datagram, JsonArray fields);

        private static readonly Dictionary<byte, Command> Commands = new()
        {
            [0x00] = new("Depth below transducer", DecodeDepth),
            [0x01] = new("Equipment ID", DecodeRawData),
            [0x05] = new("Engine RPM and PITCH", DecodeEngine),
            [0x10] = new("Apparent Wind Angle", DecodeApparentWindAngle),
            [0x11] = new("Apparent Wind Speed", DecodeApparentWindSpeed),
            [0x20] = new("Speed through water", DecodeSpeedThroughWater),
            [0x21] = new("Trip Mileage", DecodeTripMileage),
            [0x22] = new("Total Mileage", DecodeTotalMileage),
            [0x23] = new("Water temperature (ST50)", DecodeWaterTemperatureSt50),
            [0x24] = new("Display units for Mileage & Speed", DecodeMileageSpeedUnits),
            [0x25] = new("Total & Trip Log", DecodeTotalTripLog),
            [0x26] = new("Speed through water (with average)", DecodeSpeedThroughWaterAverage),
            [0x27] = new("Water temperature", DecodeWaterTemperature),
            [0x30] = new("Set lamp Intensity", DecodeLampIntensity),
            [0x36] = new("Cancel MOB (Man Over Board) condition", null),
            [0x38] = new("Codelock data", null),
            [0x50] = new("LAT position", DecodeLatitude),
            [0x51] = new("LON position", DecodeLongitude),
            [0x52] = new("Speed over Ground", DecodeSpeedOverGround),
            [0x53] = new("Course over Ground (COG)", DecodeCourseOverGround),
            [0x54] = new("GMT-time", DecodeGmtTime),
            [0x55] = new("TRACK keystroke on GPS unit", DecodeKeystroke),
            [0x56] = new("Date", DecodeDate),
            [0x57] = new("Sat Info", DecodeSatelliteInfo),
            [0x58] = new("LAT/LON (raw unfiltered)", DecodeRawLatLon),
            [0x59] = new("Set Count Down Timer", DecodeCountdownTimer),
            [0x61] = new("Issued by E-80 multifunction display at initialization", null),
            [0x65] = new("Select Fathom display units for depth display", null),
            [0x66] = new("Wind alarm", DecodeWindAlarm),
            [0x68] = new("Alarm acknowledgment keystroke", DecodeAlarmAcknowledgment),
            [0x6C] = new("Second equipment-ID datagram", DecodeRawData),
            [0x6E] = new("MOB (Man Over Board)", null),
            [0x70] = new("Keystroke on Raymarine A25006 ST60 Maxiview Remote Control", DecodeRemoteControlKey),
            [0x80] = new("Set Lamp Intensity", DecodeLampIntensity),
            [0x81] = new("Sent by course computer during setup", null),
            [0x82] = new("Target waypoint name", DecodeWaypointName),
            [0x83] = new("Sent by course computer", null),
            [0x84] = new("Compass heading Autopilot course and Rudder position", DecodeCompassAutopilotRudder),
            [0x85] = new("Navigation to waypoint information", DecodeNavigationToWaypoint),
            [0x86] = new("Keystroke", DecodeKeystroke),
            [0x87] = new("Set Response level", DecodeResponseLevel),
            [0x88] = new("Autopilot Parameter", DecodeAutopilotParameter),
            [0x89] = new("Compass heading sent by ST40 compass instrument", DecodeSt40Compass),
            [0x90] = new("Device Identification", DecodeRawData),
            [0x91] = new("Set Rudder gain", DecodeRudderGain),
            [0x92] = new("Set Autopilot Parameter", DecodeSetAutopilotParameter),
            [0x93] = new("Enter AP-Setup", null),
            [0x95] = new("Replaces command 84 while autopilot is in value setting mode", DecodeCompassAutopilotRudder),
            [0x99] = new("Compass variation", DecodeCompassVariation),
            [0x9A] = new("Version String", DecodeRawData),
            [0x9C] = new("Compass heading and Rudder position", DecodeCompassRudder),
            [0x9E] = new("Waypoint definition", DecodeRawData),
            [0xA1] = new("Destination Waypoint Info", DecodeRawData),
            [0xA2] = new("Arrival Info", DecodeArrivalInfo),
            [0xA4] = new("Broadcast query/response to identify devices", DecodeDeviceIdentificationQuery),
            [0xA5] = new("GPS and DGPS Info", DecodeGpsInfo),
            [0xA7] = new("Unknown meaning, sent by Raystar 120 GPS", null),
            [0xA8] = new("Alarm ON/OFF for Guard", DecodeGuardAlarm),
            [0xAB] = new("Alarm ON/OFF for Guard", DecodeGuardAlarm)
        };

        public static string? TryGetEmbeddedSeatalk1Description(byte[] payloadBytes)
        {
            return TryParse(payloadBytes, out var datagram) && Commands.TryGetValue(datagram.Command, out var command)
                ? $"Seatalk1: {command.Description}"
                : null;
        }

        public static JsonObject? TryDecodeEmbeddedSeatalk1(int pgn, byte[] payloadBytes)
        {
            if (pgn != 126720 ||
                !TryParse(payloadBytes, out var datagram) ||
                !Commands.TryGetValue(datagram.Command, out var command))
            {
                return null;
            }

            var fields = BuildHeader(datagram, command.Description);
            command.Decode?.Invoke(datagram, fields);

            var result = new JsonObject
            {
                ["PGN"] = 126720,
                ["Description"] = $"Seatalk1: {command.Description}",
                ["Type"] = "Fast",
                ["Fields"] = fields
            };

            if (datagram.Truncated)
            {
                Warn(result, $"Embedded Seatalk1 command is truncated: {datagram.AvailableLength} bytes available, {datagram.ExpectedLength} expected.");
            }

            if (command.Decode == null)
            {
                Warn(result, "Known Seatalk1 command; detailed field decoding has not been implemented yet.");
            }

            return result;
        }

        public static JsonObject? TryDecodeEmbeddedSeatalk1(string? pgn, byte[] payloadBytes)
        {
            return int.TryParse(pgn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPgn)
                ? TryDecodeEmbeddedSeatalk1(parsedPgn, payloadBytes)
                : null;
        }

        private static bool TryParse(byte[] payloadBytes, out Datagram datagram)
        {
            datagram = default;

            if (payloadBytes.Length <= SeatalkOffset + 1 ||
                payloadBytes[2] != Seatalk1ProprietaryId ||
                payloadBytes[3] != Seatalk1NmeaCommand)
            {
                return false;
            }

            var manufacturerCode = payloadBytes[0] | ((payloadBytes[1] & 0x07) << 8);
            var industryCode = (payloadBytes[1] >> 5) & 0x07;
            if (manufacturerCode != RaymarineManufacturerCode || industryCode != MarineIndustryCode)
            {
                return false;
            }

            var attribute = payloadBytes[SeatalkOffset + 1];
            var expectedLength = 3 + (attribute & 0x0F);
            var availableLength = Math.Min(expectedLength, payloadBytes.Length - SeatalkOffset);
            datagram = new Datagram(
                payloadBytes[SeatalkOffset],
                attribute,
                expectedLength,
                availableLength,
                payloadBytes.Skip(SeatalkOffset).Take(availableLength).ToArray());
            return true;
        }

        private static JsonArray BuildHeader(Datagram datagram, string description)
        {
            return new JsonArray
            {
                new JsonObject { ["Manufacturer Code"] = "Raymarine" },
                new JsonObject { ["Industry Code"] = "Marine Industry" },
                new JsonObject { ["Proprietary ID"] = "Seatalk 1 Encoded" },
                new JsonObject { ["command"] = "Seatalk1" },
                new JsonObject { ["Seatalk1 Command"] = description },
                new JsonObject { ["Seatalk1 Command Raw"] = $"0x{datagram.Command:X2}" },
                new JsonObject { ["Seatalk1 Attribute Raw"] = $"0x{datagram.Attribute:X2}" },
                new JsonObject { ["Seatalk1 Datagram Length"] = datagram.ExpectedLength },
                new JsonObject { ["Seatalk1 Raw"] = Hex(datagram.Bytes) }
            };
        }

        private static void DecodeDepth(Datagram d, JsonArray f)
        {
            if (!d.Has(5)) return;
            var yz = d.Bytes[2];
            f.Add(new JsonObject { ["Depth Below Transducer"] = Unit(U16(d, 3) / 10.0, "ft", 1) });
            f.Add(new JsonObject { ["Anchor Alarm Active"] = (yz & 0x80) != 0 });
            f.Add(new JsonObject { ["Metric Display Units"] = (yz & 0x40) != 0 });
            f.Add(new JsonObject { ["Depth Transducer Defective"] = (yz & 0x04) != 0 });
            f.Add(new JsonObject { ["Deep Alarm Active"] = (yz & 0x02) != 0 });
            f.Add(new JsonObject { ["Shallow Depth Alarm Active"] = (yz & 0x01) != 0 });
        }

        private static void DecodeRawData(Datagram d, JsonArray f)
        {
            f.Add(new JsonObject { ["Seatalk1 Data Raw"] = Hex(d.Bytes.Skip(2)) });
        }

        private static void DecodeEngine(Datagram d, JsonArray f)
        {
            if (!d.Has(6)) return;
            var selector = d.Bytes[2] & 0x0F;
            f.Add(new JsonObject { ["Engine Selector"] = selector switch { 0 => "RPM & PITCH", 1 => "RPM & PITCH starboard", 2 => "RPM & PITCH port", _ => $"Unknown ({selector})" } });
            f.Add(new JsonObject { ["RPM Raw"] = U16(d, 3) });
            f.Add(new JsonObject { ["Pitch Raw"] = $"0x{d.Bytes[5]:X2}" });
        }

        private static void DecodeApparentWindAngle(Datagram d, JsonArray f)
        {
            if (d.Has(4)) f.Add(new JsonObject { ["Apparent Wind Angle"] = Unit(U16(d, 2) / 2.0, "deg", 1) });
        }

        private static void DecodeApparentWindSpeed(Datagram d, JsonArray f)
        {
            if (!d.Has(4)) return;
            f.Add(new JsonObject { ["Apparent Wind Speed"] = Unit((d.Bytes[2] & 0x7F) + ((d.Bytes[3] & 0x0F) / 10.0), "kn", 1) });
            f.Add(new JsonObject { ["Display Units"] = (d.Bytes[2] & 0x80) == 0 ? "Knots" : "Meters per second" });
        }

        private static void DecodeSpeedThroughWater(Datagram d, JsonArray f)
        {
            if (d.Has(4)) f.Add(new JsonObject { ["Speed Through Water"] = Unit(U16(d, 2) / 10.0, "kn", 1) });
        }

        private static void DecodeTripMileage(Datagram d, JsonArray f)
        {
            if (!d.Has(5)) return;
            var raw = d.Bytes[2] | (d.Bytes[3] << 8) | ((d.Bytes[4] & 0x0F) << 16);
            f.Add(new JsonObject { ["Trip Mileage"] = Unit(raw / 100.0, "nm", 2) });
        }

        private static void DecodeTotalMileage(Datagram d, JsonArray f)
        {
            if (d.Has(5)) f.Add(new JsonObject { ["Total Mileage"] = Unit(U16(d, 2) / 10.0, "nm", 1) });
        }

        private static void DecodeWaterTemperatureSt50(Datagram d, JsonArray f)
        {
            if (!d.Has(4)) return;
            f.Add(new JsonObject { ["Water Temperature Celsius"] = Unit(d.Bytes[2], "deg C", 0) });
            f.Add(new JsonObject { ["Water Temperature Fahrenheit"] = Unit(d.Bytes[3], "deg F", 0) });
            f.Add(new JsonObject { ["Sensor Defective Or Not Connected"] = (d.Attribute & 0x40) != 0 });
        }

        private static void DecodeMileageSpeedUnits(Datagram d, JsonArray f)
        {
            if (d.Has(5)) f.Add(new JsonObject { ["Mileage Speed Units"] = d.Bytes[4] switch { 0x00 => "nm/knots", 0x06 => "sm/mph", 0x86 => "km/kmh", _ => $"Unknown (0x{d.Bytes[4]:X2})" } });
        }

        private static void DecodeTotalTripLog(Datagram d, JsonArray f)
        {
            if (!d.Has(7)) return;
            var totalRaw = d.Bytes[2] | (d.Bytes[3] << 8) | ((d.Attribute >> 4) << 12);
            var tripRaw = d.Bytes[4] | (d.Bytes[5] << 8) | ((d.Bytes[6] & 0x0F) << 16);
            f.Add(new JsonObject { ["Total Mileage"] = Unit(totalRaw / 10.0, "nm", 1) });
            f.Add(new JsonObject { ["Trip Mileage"] = Unit(tripRaw / 100.0, "nm", 2) });
        }

        private static void DecodeSpeedThroughWaterAverage(Datagram d, JsonArray f)
        {
            if (!d.Has(7)) return;
            f.Add(new JsonObject { ["Current Speed Through Water"] = Unit(U16(d, 2) / 100.0, "kn", 2) });
            f.Add(new JsonObject { ["Average Speed Through Water"] = Unit(U16(d, 4) / 100.0, "kn", 2) });
            f.Add(new JsonObject { ["Current Speed Valid"] = (d.Bytes[6] & 0x40) != 0 });
            f.Add(new JsonObject { ["Average Speed Valid"] = (d.Bytes[6] & 0x04) != 0 });
        }

        private static void DecodeWaterTemperature(Datagram d, JsonArray f)
        {
            if (d.Has(4)) f.Add(new JsonObject { ["Water Temperature"] = Unit((U16(d, 2) - 100) / 10.0, "deg C", 1) });
        }

        private static void DecodeLampIntensity(Datagram d, JsonArray f)
        {
            if (d.Has(3)) f.Add(new JsonObject { ["Lamp Intensity"] = (d.Bytes[2] & 0x0F) / 4 });
        }

        private static void DecodeLatitude(Datagram d, JsonArray f)
        {
            if (d.Has(5)) DecodePosition(f, "Latitude", d.Bytes[2], U16(d, 3), "North", "South");
        }

        private static void DecodeLongitude(Datagram d, JsonArray f)
        {
            if (d.Has(5)) DecodePosition(f, "Longitude", d.Bytes[2], U16(d, 3), "West", "East");
        }

        private static void DecodePosition(JsonArray f, string name, int degrees, int encodedMinutes, string positive, string negative)
        {
            var direction = (encodedMinutes & 0x8000) != 0 ? negative : positive;
            var minutes = (encodedMinutes & 0x7FFF) / 100.0;
            f.Add(new JsonObject { [name] = $"{degrees} deg {minutes:0.00} min {direction}" });
        }

        private static void DecodeSpeedOverGround(Datagram d, JsonArray f)
        {
            if (d.Has(4)) f.Add(new JsonObject { ["Speed Over Ground"] = Unit(U16(d, 2) / 10.0, "kn", 1) });
        }

        private static void DecodeCourseOverGround(Datagram d, JsonArray f)
        {
            if (d.Has(3)) f.Add(new JsonObject { ["Course Over Ground"] = Unit(CompassHeading(d.Attribute, d.Bytes[2]), "deg", 1) });
        }

        private static void DecodeGmtTime(Datagram d, JsonArray f)
        {
            if (!d.Has(4)) return;
            var hour = d.Bytes[3];
            var minute = (d.Bytes[2] & 0xFC) / 4;
            var second = ((d.Bytes[2] & 0x03) << 4) | (d.Attribute >> 4);
            f.Add(new JsonObject { ["GMT Time"] = $"{hour:D2}:{minute:D2}:{second:D2}" });
        }

        private static void DecodeDate(Datagram d, JsonArray f)
        {
            if (d.Has(4)) f.Add(new JsonObject { ["Date"] = $"{d.Bytes[3]:D2}-{d.Attribute >> 4:D2}-{d.Bytes[2]:D2}" });
        }

        private static void DecodeSatelliteInfo(Datagram d, JsonArray f)
        {
            if (!d.Has(3)) return;
            f.Add(new JsonObject { ["Satellites"] = d.Attribute >> 4 });
            f.Add(new JsonObject { ["Horizontal Dilution Of Position Raw"] = $"0x{d.Bytes[2]:X2}" });
        }

        private static void DecodeRawLatLon(Datagram d, JsonArray f)
        {
            if (!d.Has(8)) return;
            f.Add(new JsonObject { ["Latitude"] = $"{d.Bytes[2]} deg {(d.Bytes[3] * 256 + d.Bytes[4]) / 1000.0:0.000} min" });
            f.Add(new JsonObject { ["Longitude"] = $"{d.Bytes[5]} deg {(d.Bytes[6] * 256 + d.Bytes[7]) / 1000.0:0.000} min" });
        }

        private static void DecodeCountdownTimer(Datagram d, JsonArray f)
        {
            if (!d.Has(5)) return;
            f.Add(new JsonObject { ["Countdown Seconds"] = d.Bytes[2] });
            f.Add(new JsonObject { ["Countdown Minutes"] = d.Bytes[3] & 0x3F });
            f.Add(new JsonObject { ["Count Up Start Flag"] = (d.Bytes[3] & 0x80) != 0 });
            f.Add(new JsonObject { ["Countdown Hours"] = d.Bytes[4] & 0x0F });
        }

        private static void DecodeWindAlarm(Datagram d, JsonArray f)
        {
            if (!d.Has(3)) return;
            var xy = d.Bytes[2];
            f.Add(new JsonObject { ["Apparent Wind Angle Low Alarm"] = (xy & 0x80) != 0 });
            f.Add(new JsonObject { ["Apparent Wind Angle High Alarm"] = (xy & 0x40) != 0 });
            f.Add(new JsonObject { ["Apparent Wind Speed Low Alarm"] = (xy & 0x20) != 0 });
            f.Add(new JsonObject { ["Apparent Wind Speed High Alarm"] = (xy & 0x10) != 0 });
            f.Add(new JsonObject { ["True Wind Angle Low Alarm"] = (xy & 0x08) != 0 });
            f.Add(new JsonObject { ["True Wind Angle High Alarm"] = (xy & 0x04) != 0 });
            f.Add(new JsonObject { ["True Wind Speed Low Alarm"] = (xy & 0x02) != 0 });
            f.Add(new JsonObject { ["True Wind Speed High Alarm"] = (xy & 0x01) != 0 });
        }

        private static void DecodeAlarmAcknowledgment(Datagram d, JsonArray f)
        {
            f.Add(new JsonObject { ["Alarm Acknowledgment"] = AlarmName(d.Attribute >> 4) });
            f.Add(new JsonObject { ["Alarm Acknowledgment Raw"] = $"0x{d.Attribute >> 4:X1}" });
        }

        private static void DecodeRemoteControlKey(Datagram d, JsonArray f)
        {
            if (!d.Has(3)) return;
            f.Add(new JsonObject { ["Remote Control Key Mode"] = (d.Bytes[2] >> 4) switch { 0 => "Single keypress", 2 => "Two keys pressed", _ => $"Unknown ({d.Bytes[2] >> 4})" } });
            f.Add(new JsonObject { ["Remote Control Key Raw"] = $"0x{d.Bytes[2]:X2}" });
        }

        private static void DecodeWaypointName(Datagram d, JsonArray f)
        {
            if (!d.Has(8)) return;
            f.Add(new JsonObject { ["Waypoint Name Raw"] = Hex(d.Bytes.Skip(2)) });
            f.Add(new JsonObject { ["Waypoint Name Checksum Valid"] =
                ((d.Bytes[2] + d.Bytes[3]) & 0xFF) == 0xFF &&
                ((d.Bytes[4] + d.Bytes[5]) & 0xFF) == 0xFF &&
                ((d.Bytes[6] + d.Bytes[7]) & 0xFF) == 0xFF });
        }

        private static void DecodeCompassAutopilotRudder(Datagram d, JsonArray f)
        {
            if (!d.Has(9)) return;
            var z = d.Bytes[4] & 0x0F;
            var m = d.Bytes[5] & 0x0F;
            f.Add(new JsonObject { ["Compass Heading"] = Unit(CompassHeading(d.Bytes[1], d.Bytes[2]), "deg", 1) });
            f.Add(new JsonObject { ["Autopilot Course"] = Unit(AutopilotCourse(d.Bytes[2], d.Bytes[3]), "deg", 1) });
            f.Add(new JsonObject { ["Pilot Mode"] = PilotMode(z) });
            f.Add(new JsonObject { ["Vane Mode"] = (z & 0x04) != 0 });
            f.Add(new JsonObject { ["Track Mode"] = (z & 0x08) != 0 });
            f.Add(new JsonObject { ["Off Course Alarm"] = (m & 0x04) != 0 });
            f.Add(new JsonObject { ["Wind Shift Alarm"] = (m & 0x02) != 0 });
            f.Add(new JsonObject { ["Rudder Position"] = Unit(SignedByte(d.Bytes[6]), "deg", 0) });
            f.Add(new JsonObject { ["Rudder Position Raw"] = $"0x{d.Bytes[6]:X2}" });
            f.Add(new JsonObject { ["SS"] = $"0x{d.Bytes[7]:X2}" });
            f.Add(new JsonObject { ["TT"] = $"0x{d.Bytes[8]:X2}" });
        }

        private static void DecodeNavigationToWaypoint(Datagram d, JsonArray f)
        {
            if (!d.Has(9)) return;
            f.Add(new JsonObject { ["Cross Track Error Raw"] = d.Bytes[2] | ((d.Attribute >> 4) << 8) });
            f.Add(new JsonObject { ["Navigation Flags Raw"] = $"0x{d.Bytes[8]:X2}" });
        }

        private static void DecodeKeystroke(Datagram d, JsonArray f)
        {
            if (!d.Has(4)) return;
            f.Add(new JsonObject { ["Keystroke Source"] = (d.Attribute >> 4) switch { 0 => "Autopilot", 1 => "Z101 remote control", 2 => "ST4000+ or ST600R", _ => $"Unknown ({d.Attribute >> 4})" } });
            f.Add(new JsonObject { ["Keystroke"] = KeyName(d.Bytes[2]) });
            f.Add(new JsonObject { ["Keystroke Raw"] = $"0x{d.Bytes[2]:X2}" });
        }

        private static void DecodeResponseLevel(Datagram d, JsonArray f)
        {
            if (d.Has(3)) f.Add(new JsonObject { ["Response Level"] = d.Bytes[2] & 0x0F });
        }

        private static void DecodeAutopilotParameter(Datagram d, JsonArray f)
        {
            if (!d.Has(6)) return;
            f.Add(new JsonObject { ["Autopilot Parameter"] = AutopilotParameterName(d.Bytes[2]) });
            f.Add(new JsonObject { ["Autopilot Parameter Raw"] = $"0x{d.Bytes[2]:X2}" });
            f.Add(new JsonObject { ["Autopilot Parameter Value Raw"] = Hex(d.Bytes.Skip(3).Take(3)) });
        }

        private static void DecodeSt40Compass(Datagram d, JsonArray f)
        {
            if (!d.Has(5)) return;
            f.Add(new JsonObject { ["Compass Heading"] = Unit(CompassHeading(d.Attribute, d.Bytes[2]), "deg", 1) });
            f.Add(new JsonObject { ["Locked Steer Reference"] = Unit(AutopilotCourse(d.Bytes[2], d.Bytes[3]), "deg", 1) });
        }

        private static void DecodeRudderGain(Datagram d, JsonArray f)
        {
            if (d.Has(3)) f.Add(new JsonObject { ["Rudder Gain"] = d.Bytes[2] & 0x0F });
        }

        private static void DecodeSetAutopilotParameter(Datagram d, JsonArray f)
        {
            if (!d.Has(5)) return;
            f.Add(new JsonObject { ["Autopilot Parameter"] = AutopilotParameterName(d.Bytes[2]) });
            f.Add(new JsonObject { ["Autopilot Parameter Value"] = d.Bytes[3] });
        }

        private static void DecodeCompassVariation(Datagram d, JsonArray f)
        {
            if (d.Has(3)) f.Add(new JsonObject { ["Compass Variation"] = Unit(-SignedByte(d.Bytes[2]), "deg", 0) });
        }

        private static void DecodeCompassRudder(Datagram d, JsonArray f)
        {
            if (!d.Has(4)) return;
            f.Add(new JsonObject { ["Compass Heading"] = Unit(CompassHeading(d.Bytes[1], d.Bytes[2]), "deg", 1) });
            f.Add(new JsonObject { ["Rudder Position"] = Unit(SignedByte(d.Bytes[3]), "deg", 0) });
            f.Add(new JsonObject { ["Rudder Position Raw"] = $"0x{d.Bytes[3]:X2}" });
        }

        private static void DecodeArrivalInfo(Datagram d, JsonArray f)
        {
            f.Add(new JsonObject { ["Arrival Perpendicular Passed"] = ((d.Attribute >> 4) & 0x02) != 0 });
            f.Add(new JsonObject { ["Arrival Circle Entered"] = ((d.Attribute >> 4) & 0x04) != 0 });
            f.Add(new JsonObject { ["Arrival Info Raw"] = Hex(d.Bytes.Skip(2)) });
        }

        private static void DecodeDeviceIdentificationQuery(Datagram d, JsonArray f)
        {
            f.Add(new JsonObject { ["Device Identification Query Raw"] = Hex(d.Bytes.Skip(2)) });
            if (d.Has(5)) f.Add(new JsonObject { ["Unit ID"] = UnitIdName(d.Bytes[2]) });
        }

        private static void DecodeGpsInfo(Datagram d, JsonArray f)
        {
            f.Add(new JsonObject { ["GPS Info Subtype Raw"] = $"0x{d.Attribute:X2}" });
            if (d.Attribute == 0x57 && d.Has(4))
            {
                f.Add(new JsonObject { ["Signal Quality"] = d.Bytes[2] & 0x0F });
                f.Add(new JsonObject { ["Signal Quality Available"] = (d.Bytes[2] & 0x10) != 0 });
            }
            else if (d.Attribute == 0x74)
            {
                f.Add(new JsonObject { ["Satellite IDs"] = Hex(d.Bytes.Skip(2)) });
            }
            else
            {
                f.Add(new JsonObject { ["GPS Info Raw"] = Hex(d.Bytes.Skip(2)) });
            }
        }

        private static void DecodeGuardAlarm(Datagram d, JsonArray f)
        {
            f.Add(new JsonObject { ["Guard Alarm"] = (d.Attribute & 0x10) != 0 ? "ON" : "OFF" });
        }

        private static int U16(Datagram d, int offset) => d.Bytes[offset] | (d.Bytes[offset + 1] << 8);

        private static double CompassHeading(byte uByte, byte vw)
        {
            var u = uByte >> 4;
            return ((u & 0x03) * 90) + ((vw & 0x3F) * 2) + ((u & 0x0C) / 8.0);
        }

        private static double AutopilotCourse(byte vw, byte xy) => ((vw >> 6) * 90) + (xy / 2.0);

        private static string PilotMode(int z)
        {
            if ((z & 0x08) != 0) return "Track";
            if ((z & 0x04) != 0) return "Vane";
            return (z & 0x02) != 0 ? "Auto" : "Standby";
        }

        private static int SignedByte(byte value) => unchecked((sbyte)value);

        private static string AlarmName(int value) => value switch
        {
            1 => "Shallow Shallow Water Alarm",
            2 => "Deep Water Alarm",
            3 => "Anchor Alarm",
            4 => "True Wind High Alarm",
            5 => "True Wind Low Alarm",
            6 => "True Wind Angle High Alarm",
            7 => "True Wind Angle Low Alarm",
            8 => "Apparent Wind High Alarm",
            9 => "Apparent Wind Low Alarm",
            10 => "Apparent Wind Angle High Alarm",
            11 => "Apparent Wind Angle Low Alarm",
            _ => $"Unknown ({value})"
        };

        private static string KeyName(byte value) => value switch
        {
            0x05 => "-1",
            0x06 => "-10",
            0x07 => "+1",
            0x08 => "+10",
            0x09 => "-1 in response/rudder gain mode",
            0x0A => "+1 in response/rudder gain mode",
            0x20 => "+1 & -1",
            0x21 => "-1 & -10",
            0x22 => "+1 & +10",
            0x23 => "Standby & Auto (wind mode)",
            0x28 => "+10 & -10",
            0x2E => "+1 & -1 (Response Display)",
            0x41 => "Auto pressed longer",
            0x42 => "Standby pressed longer",
            0x45 => "-1 pressed longer",
            0x46 => "-10 pressed longer",
            0x47 => "+1 pressed longer",
            0x48 => "+10 pressed longer",
            0x63 => "+1 & +10 pressed longer",
            0x68 => "+10 & -10 pressed longer",
            0x6E => "+1 & -1 pressed longer (Rudder Gain Display)",
            0x80 => "-1 pressed repeated",
            0x81 => "+1 pressed repeated",
            0x82 => "-10 pressed repeated",
            0x83 => "+10 pressed repeated",
            0x84 => "+1, -1, +10 or -10 released",
            _ => $"Unknown (0x{value:X2})"
        };

        private static string AutopilotParameterName(byte value) => value switch
        {
            1 => "rudder gain",
            2 => "counter rudder",
            3 => "rudder limit",
            4 => "turn rate limit",
            5 => "speed",
            6 => "off course angle",
            7 => "auto trim",
            9 => "response level",
            10 => "drive type",
            11 => "rudder damping",
            12 => "variation",
            14 => "auto adapt",
            15 => "latitude",
            16 => "rudder alignment",
            17 => "wind trim",
            18 => "response level 2",
            _ => $"Unknown (0x{value:X2})"
        };

        private static string UnitIdName(byte value) => value switch
        {
            0x01 => "Depth",
            0x02 => "Speed",
            0x03 => "Multi",
            0x04 => "Tridata",
            0x05 => "Tridata repeater",
            0x06 => "Wind",
            0x07 => "WMG",
            0x08 => "Navdata GPS",
            0x09 => "Maxview",
            0x0A => "Steering compass",
            0x0F => "Rudder angle indicator",
            0x10 => "ST30 wind",
            0x11 => "ST30 bidata",
            0x12 => "ST30 speed",
            0x13 => "ST30 depth",
            0x14 => "LCD navcenter",
            0x15 => "Apelco LCD chartplotter",
            0x16 => "Analog speedtrim",
            0x17 => "Analog depth",
            0x18 => "ST30 compass",
            0x19 => "ST50 NMEA bridge",
            0xA8 => "ST80 Masterview",
            _ => $"Unknown (0x{value:X2})"
        };

        private static string Unit(double value, string unit, int decimals)
        {
            return $"{value.ToString($"F{decimals}", CultureInfo.InvariantCulture)} {unit}";
        }

        private static string Hex(IEnumerable<byte> values) => string.Join(" ", values.Select(value => $"0x{value:X2}"));

        private static void Warn(JsonObject result, string warning)
        {
            if (result["Warnings"] is not JsonArray warnings)
            {
                warnings = new JsonArray();
                result["Warnings"] = warnings;
            }

            warnings.Add(warning);
        }

        private readonly record struct Command(string Description, Decoder? Decode);

        private readonly record struct Datagram(byte Command, byte Attribute, int ExpectedLength, int AvailableLength, byte[] Bytes)
        {
            public bool Truncated => AvailableLength < ExpectedLength;
            public bool Has(int count) => Bytes.Length >= count;
        }
    }
}
