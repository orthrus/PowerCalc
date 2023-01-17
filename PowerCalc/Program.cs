using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

namespace PowerCalc
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var entries = ParseFile();

            decimal batterySoC = 0;
            decimal batteryCapacity = 5.0M;

            var originalUsage = GetUsage(entries);

            decimal totalBatteryUsage = 0;

            DateTimeOffset currentDay = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.FromHours(1));
            bool importedToday = false;
            decimal numberOfDaysWithoutImport = 0;

            foreach (var entry in entries.Values)
            {
                // First charge battery using exported energy
                if (batterySoC < batteryCapacity)
                {
                    if (entry.Export > 0)
                    {
                        var addCharge = batteryCapacity - batterySoC;
                        addCharge = Math.Min(addCharge, entry.Export);

                        batterySoC += addCharge;
                        entry.Export -= addCharge;

                        if (batterySoC == batteryCapacity)
                        {
                            Console.WriteLine($"Battery full at {entry.Start}");
                        }
                        else if (batterySoC > batteryCapacity)
                        {

                        }
                    }
                }

                // Next remove charge from battery where possible
                if (batterySoC > 0)
                {
                    if (entry.Import <= batterySoC)
                    {
                        // Imported equal or less than battery SoC
                        var removeCharge = entry.Import;
                        batterySoC -= removeCharge;
                        entry.Import -= removeCharge;
                        totalBatteryUsage += removeCharge;

                        if (batterySoC == 0)
                        {
                            Console.WriteLine($"Battery empty at {entry.Start}");
                        }
                        else if (batterySoC < 0)
                        {

                        }
                    }
                    else
                    {
                        // Imported more than battery SoC, fully drain battery
                        var removeCharge = batterySoC;
                        entry.Import -= batterySoC;
                        batterySoC = 0;
                    }
                }

                if(entry.Import > 0)
                {
                    importedToday = true;
                }

                if(entry.Start.Date != currentDay.Date)
                {
                    if(!importedToday)
                    {
                        numberOfDaysWithoutImport++;
                    }
                    importedToday = false;

                    currentDay = entry.Start.Date;
                }
            }

            var simulatedUsage = GetUsage(entries);

            Console.WriteLine("");
            Console.WriteLine($"           Import   Export   Generated   Battery   Self");
            Console.WriteLine($"Original:    {originalUsage.Import:0000}     {originalUsage.Export:0000}        {originalUsage.Generated:0000}             {(originalUsage.Generated - originalUsage.Export) * 100 / originalUsage.Generated:00.0}%");
            Console.WriteLine($"Simulated:   {simulatedUsage.Import:0000}     {simulatedUsage.Export:0000}        {simulatedUsage.Generated:0000}      {totalBatteryUsage:0000}   {(simulatedUsage.Generated - simulatedUsage.Export) * 100 / simulatedUsage.Generated:00.0}%");
            Console.WriteLine($"Days without import from grid: {numberOfDaysWithoutImport}");
        }

        private static (decimal Import, decimal Export, decimal Generated, decimal Usage) GetUsage(Dictionary<DateTimeOffset, Entry> entries)
        {
            var tmp = entries.Values.ToArray();
            decimal totalImport = 0, totalExport = 0, totalGenerated = 0;

            for (int i = 0; i < tmp.Length; i++)
            {
                totalImport += tmp[i].Import;
                totalExport += tmp[i].Export;
                totalGenerated += tmp[i].Generated;
            }

            return (totalImport, totalExport, totalGenerated, (totalImport + totalGenerated - totalExport));
        }

        private static Dictionary<DateTimeOffset, Entry> ParseFile()
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                Delimiter = ",",
            };

            using (var reader = new StreamReader("C:\\Temp\\statistics-export.csv"))
            using (var csv = new CsvReader(reader, config))
            {

                var records = csv.GetRecords<Record>();
                var entries = new Dictionary<DateTimeOffset, Entry>();

                DateTimeOffset currentStart = DateTimeOffset.MinValue;
                Entry currentEntry = null, previousEntry = null;

                var previous = new Dictionary<RecordType, decimal>();

                foreach (var record in records)
                {
                    if (record.Start != currentStart)
                    {
                        previousEntry = currentEntry;
                        currentEntry = new Entry
                        {
                            Start = record.Start,
                            Import = 0,
                            Export = 0,
                            Generated = 0,
                        };
                        currentStart = record.Start;
                        entries.Add(record.Start, currentEntry);
                    }

                    if (currentEntry != null)
                    {
                        if (previous.TryGetValue(record.MetadataID, out var prev))
                        {
                            var diff = record.Sum - prev;
                            switch (record.MetadataID)
                            {
                                case RecordType.ImportT1:
                                case RecordType.ImportT2:
                                    currentEntry.Import += diff;
                                    break;

                                case RecordType.ExportT1:
                                case RecordType.ExportT2:
                                    currentEntry.Export += diff;
                                    break;

                                case RecordType.Generated:
                                    currentEntry.Generated += diff;
                                    break;
                            }

                            previous[record.MetadataID] = record.Sum;
                        }
                        else
                        {
                            previous.Add(record.MetadataID, record.Sum);
                        }
                    }
                }

                return entries;
            }

            throw new Exception("Could not parse file");
        }
    }
}