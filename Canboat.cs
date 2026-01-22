using System.Text.Json.Serialization;

namespace NMEA2000Analyzer
{
    public class Canboat
    {

        public class Rootobject
        {
            public string SchemaVersion { get; set; }
            public string Comment { get; set; }
            public string CreatorCode { get; set; }
            public string License { get; set; }
            public string Version { get; set; }
            public string Copyright { get; set; }
            public Physicalquantity[] PhysicalQuantities { get; set; }
            public Fieldtype[] FieldTypes { get; set; }
            public Missingenumeration[] MissingEnumerations { get; set; }
            public Lookupenumeration[] LookupEnumerations { get; set; }
            public Lookupindirectenumeration[] LookupIndirectEnumerations { get; set; }
            public Lookupbitenumeration[] LookupBitEnumerations { get; set; }
            public Lookupfieldtypeenumeration[] LookupFieldTypeEnumerations { get; set; }
            public List<Pgn> PGNs { get; set; }
        }

        public class Physicalquantity
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string URL { get; set; }
            public string UnitDescription { get; set; }
            public string Unit { get; set; }
            public string Comment { get; set; }
        }

        public class Fieldtype
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string EncodingDescription { get; set; }
            public string URL { get; set; }
            public int Bits { get; set; }
            public bool Signed { get; set; }
            public string Comment { get; set; }
            public string Unit { get; set; }
            public bool VariableSize { get; set; }
        }

        public class Missingenumeration
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }

        public class Lookupenumeration
        {
            public string Name { get; set; }
            public uint MaxValue { get; set; }
            public Enumvalue[] EnumValues { get; set; }
        }

        public class Enumvalue
        {
            public string Name { get; set; }
            public uint Value { get; set; }
        }

        public class Lookupindirectenumeration
        {
            public string Name { get; set; }
            public uint MaxValue { get; set; }
            public Enumvalue1[] EnumValues { get; set; }
        }

        public class Enumvalue1
        {
            public string Name { get; set; }
            public uint Value1 { get; set; }
            public uint Value2 { get; set; }
        }

        public class Lookupbitenumeration
        {
            public string Name { get; set; }
            public int MaxValue { get; set; }
            public Enumbitvalue[] EnumBitValues { get; set; }
        }

        public class Enumbitvalue
        {
            public string Name { get; set; }
            public int Bit { get; set; }
        }

        public class Lookupfieldtypeenumeration
        {
            public string Name { get; set; }
            public int MaxValue { get; set; }
            public Enumfieldtypevalue[] EnumFieldTypeValues { get; set; }
        }

        public class Enumfieldtypevalue
        {
            public string name { get; set; }
            public int value { get; set; }
            public string FieldType { get; set; }
            public float Resolution { get; set; }
            public string Unit { get; set; }
            public string Bits { get; set; }
            public string LookupEnumeration { get; set; }
            public string LookupBitEnumeration { get; set; }
        }

        public class Pgn
        {
            public int PGN { get; set; }
            public string Id { get; set; }
            public string Description { get; set; }
            public int Priority { get; set; }
            public string Explanation { get; set; }
            public string Type { get; set; }
            public bool Complete { get; set; }
            public int FieldCount { get; set; }
            public int Length { get; set; }
            public bool TransmissionIrregular { get; set; }
            public Field[] Fields { get; set; }
            public string URL { get; set; }
            public string[] Missing { get; set; }
            public int TransmissionInterval { get; set; }
            public int MinLength { get; set; }
            public int RepeatingFieldSet1Size { get; set; }
            public int RepeatingFieldSet1StartField { get; set; }
            public int RepeatingFieldSet1CountField { get; set; }
            public int RepeatingFieldSet2Size { get; set; }
            public int RepeatingFieldSet2StartField { get; set; }
            public int RepeatingFieldSet2CountField { get; set; }
        }

        public class Field
        {
            public int Order { get; set; }
            public string Id { get; set; }
            public string Name { get; set; }
            public int BitLength { get; set; }
            public int BitOffset { get; set; }
            public int BitStart { get; set; }
            public float Resolution { get; set; }
            public bool Signed { get; set; }
            public float? RangeMin { get; set; }
            public float? RangeMax { get; set; }
            public string FieldType { get; set; }
            public string LookupEnumeration { get; set; }
            public string Description { get; set; }
            public int? Match { get; set; }
            public string LookupIndirectEnumeration { get; set; }
            public int LookupIndirectEnumerationFieldOrder { get; set; }
            public string Unit { get; set; }
            public string PhysicalQuantity { get; set; }
            public int Offset { get; set; }
            public string LookupBitEnumeration { get; set; }
            public bool BitLengthVariable { get; set; }
            public string Condition { get; set; }
            public int BitLengthField { get; set; }
            public string LookupFieldTypeEnumeration { get; set; }
        }

    }
}
