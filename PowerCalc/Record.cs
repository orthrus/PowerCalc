using CsvHelper.Configuration.Attributes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PowerCalc
{
    public enum RecordType
    {
        Generated = 246,
        ImportT1 = 19,
        ImportT2 = 20,
        ExportT1 = 21,
        ExportT2 = 22,
    }

    internal class Record
    {
        //[Index(0)]
        //public long ID { get; set; }
        //[Index(1)]
        //public DateTimeOffset Created { get; set; }
        [Index(2)]
        public RecordType MetadataID { get; set; }
        //[Index(3)]
        //public DateTimeOffset Start { get; set; }
        //[Index(4)]
        //public decimal? Mean { get; set; }
        //[Index(5)]
        //public decimal? Min { get; set; }
        //[Index(6)]
        //public decimal? Max { get; set; }
        //[Index(7)]
        //public DateTimeOffset LastReset { get; set; }
        //[Index(8)]
        //public decimal? State { get; set; }
        [Index(9)]
        public decimal Sum { get; set; }
        //[Index(10)]
        //public double? CreatedTs { get; set; }
        [Index(11)]
        public double StartTs { get; set; }

        [Ignore]
        public DateTimeOffset Start => DateTimeOffset.FromUnixTimeSeconds((long)StartTs);
    }

    [DebuggerDisplay("{Start} <{Import} >{Export} ^{Generated}")]
    public class Entry
    {
        public DateTimeOffset Start { get; set; }
        public decimal Import { get; set; }
        public decimal Export { get; set; }
        public decimal Generated { get; set; }
    }
}
