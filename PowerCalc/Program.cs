using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

namespace PowerCalc
{
    /**
     * 
     * Export CSV from Home Assistant (SQLite):
     * 
        SELECT *
        FROM "statistics"
        WHERE start_ts >= unixepoch('2025-01-01 00:00:00') AND start_ts <= unixepoch('2026-01-01 00:00:00')
        AND metadata_id IN (246,19,20,21,22)
        ORDER BY start_ts
     *
     */


    internal class Program
    {
        static void Main(string[] args)
        {
            var filePath = args.Length > 0 ? args[0] : "statistics-export.csv";
            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"Usage: PowerCalc <path-to-statistics-export.csv>");
                Console.Error.WriteLine($"File not found: {filePath}");
                return;
            }

            var entries = ParseFile(filePath);

            decimal batterySoC = 0;
            decimal batteryCapacity = 8.0M;
            decimal maxPowerPerHour = 3.0M; // 3 kW over 1 hour = 3 kWh max per entry
            decimal chargeEfficiency = 0.90M;
            decimal dischargeEfficiency = 0.90M;
            decimal extraPvFactor = 0.00M; // +50% extra PV generation
            decimal importPrice = 0.29M;
            decimal exportPrice = 0.01M;

            var originalUsage = GetUsage(entries);

            decimal totalBatteryUsage = 0;
            decimal totalConversionLoss = 0;

            DateTimeOffset currentDay = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(1));
            bool importedToday = false;
            decimal numberOfDaysWithoutImport = 0;

            DateTimeOffset firstDayWithoutImport = DateTimeOffset.MaxValue, lastDayWithoutImport = DateTimeOffset.MinValue;

            foreach (var entry in entries.Values)
            {
                // Simulate extra PV generation (+50%): first offset import within the same hour, then export the remainder
                if (entry.Generated > 0 && extraPvFactor > 0)
                {
                    var extraGenerated = entry.Generated * extraPvFactor;
                    entry.Generated += extraGenerated;

                    var directSelfUse = Math.Min(entry.Import, extraGenerated);
                    entry.Import -= directSelfUse;
                    entry.Export += extraGenerated - directSelfUse;
                }

                // First charge battery using exported energy (90% charging efficiency, max 3 kWh/h)
                if (batterySoC < batteryCapacity && entry.Export > 0)
                {
                    var neededCharge = batteryCapacity - batterySoC;
                    var maxExportUsed = Math.Min(neededCharge / chargeEfficiency, maxPowerPerHour);
                    var exportUsed = Math.Min(entry.Export, maxExportUsed);
                    var addCharge = exportUsed * chargeEfficiency;

                    batterySoC += addCharge;
                    entry.Export -= exportUsed;
                    totalConversionLoss += exportUsed - addCharge;

                    if (batterySoC >= batteryCapacity)
                    {
                        batterySoC = batteryCapacity;
                        Console.WriteLine($"Battery full at {entry.Start}");
                    }
                }

                // Next remove charge from battery where possible (90% discharging efficiency, max 3 kWh/h)
                if (batterySoC > 0 && entry.Import > 0)
                {
                    var maxDeliverable = Math.Min(batterySoC * dischargeEfficiency, maxPowerPerHour);
                    var delivered = Math.Min(entry.Import, maxDeliverable);
                    var removeCharge = delivered / dischargeEfficiency;

                    batterySoC -= removeCharge;
                    entry.Import -= delivered;
                    totalBatteryUsage += delivered;
                    totalConversionLoss += removeCharge - delivered;

                    if (batterySoC <= 0)
                    {
                        batterySoC = 0;
                        Console.WriteLine($"Battery empty at {entry.Start}");
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
                        if(currentDay.Date < firstDayWithoutImport)
                        {
                            firstDayWithoutImport = currentDay.Date;
                        }
                        if(currentDay.Date > lastDayWithoutImport)
                        {
                            lastDayWithoutImport = currentDay.Date;
                        }

                        numberOfDaysWithoutImport++;
                    }
                    importedToday = false;

                    currentDay = entry.Start.Date;
                }
            }

            var simulatedUsage = GetUsage(entries);

            var originalCost = (originalUsage.Import * importPrice) - (originalUsage.Export * exportPrice);
            var simulatedCost = (simulatedUsage.Import * importPrice) - (simulatedUsage.Export * exportPrice);
            var savings = originalCost - simulatedCost;

            Console.WriteLine("");
            Console.WriteLine($"           Import   Export   Generated   Battery   Loss   Self     Cost");
            Console.WriteLine($"Original:    {originalUsage.Import:0000}     {originalUsage.Export:0000}        {originalUsage.Generated:0000}                    {(originalUsage.Generated - originalUsage.Export) * 100 / originalUsage.Generated:00.0}%   {originalCost,7:0.00}");
            Console.WriteLine($"Simulated:   {simulatedUsage.Import:0000}     {simulatedUsage.Export:0000}        {simulatedUsage.Generated:0000}      {totalBatteryUsage:0000}   {totalConversionLoss:0000}   {(simulatedUsage.Generated - simulatedUsage.Export) * 100 / simulatedUsage.Generated:00.0}%   {simulatedCost,7:0.00}");
            Console.WriteLine($"Savings:                                                          {savings,7:0.00}");
            Console.WriteLine($"Days without import from grid: {numberOfDaysWithoutImport} (First {firstDayWithoutImport:yyyy-MM-dd}, Last {lastDayWithoutImport:yyyy-MM-dd})");
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

        private static Dictionary<DateTimeOffset, Entry> ParseFile(string filePath)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                Delimiter = ",",
            };

            using (var reader = new StreamReader(filePath))
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