using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NMEA2000Analyzer
{
    public class DeviceInfo
    {
        public string Manufacturer { get; set; }
        public int UniqueNumber { get; set; }
        public int DeviceInstance { get; set; }
        public int DeviceFunction { get; set; }
        public int DeviceClass { get; set; }
        public int SystemInstance { get; set; }
    }
}
