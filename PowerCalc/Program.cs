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
            if (entries.Count == 0)
            {
                Console.Error.WriteLine($"No records found in {filePath}.");
                return;
            }

            decimal batterySoC = 0;
            decimal batteryCapacity = 7M;
            decimal maxPowerPerHour = 3.0M; // 3 kW over 1 hour = 3 kWh max per entry
            decimal idleConsumptionPerHour = 0.020M; // 20W BMS/inverter standby consumption (0.02 kWh/h)
            decimal chargeEfficiency = 0.91M;
            decimal dischargeEfficiency = 0.91M;
            decimal extraPvFactor = 0.00M; // +50% extra PV generation
            decimal importPrice = 0.29M;
            decimal exportPrice = 0.01M;

            var originalUsage = GetUsage(entries);

            decimal totalBatteryUsage = 0;
            decimal totalConversionLoss = 0;
            decimal totalIdleConsumption = 0;

            DateTimeOffset? currentDay = null;
            bool importedToday = false;
            decimal numberOfDaysWithoutImport = 0;

            DateTimeOffset firstDayWithoutImport = DateTimeOffset.MaxValue, lastDayWithoutImport = DateTimeOffset.MinValue;

            foreach (var entry in entries.Values)
            {
                var entryDay = entry.Start.ToLocalTime().Date;
                if (currentDay == null)
                {
                    currentDay = entryDay;
                }
                else if (entryDay != currentDay.Value)
                {
                    if (!importedToday)
                    {
                        if (currentDay.Value < firstDayWithoutImport)
                        {
                            firstDayWithoutImport = currentDay.Value;
                        }
                        if (currentDay.Value > lastDayWithoutImport)
                        {
                            lastDayWithoutImport = currentDay.Value;
                        }

                        numberOfDaysWithoutImport++;
                    }
                    importedToday = false;
                    currentDay = entryDay;
                }

                // Simulate extra PV generation (+50%): first offset import within the same hour, then export the remainder
                if (entry.Generated > 0 && extraPvFactor > 0)
                {
                    var extraGenerated = entry.Generated * extraPvFactor;
                    entry.Generated += extraGenerated;

                    var directSelfUse = Math.Min(entry.Import, extraGenerated);
                    entry.Import -= directSelfUse;
                    entry.Export += extraGenerated - directSelfUse;
                }

                // Battery/inverter standby consumption: covered by solar export first, otherwise added to import
                if (idleConsumptionPerHour > 0)
                {
                    totalIdleConsumption += idleConsumptionPerHour;
                    var idleFromExport = Math.Min(entry.Export, idleConsumptionPerHour);
                    entry.Export -= idleFromExport;
                    entry.Import += idleConsumptionPerHour - idleFromExport;
                }

                // If an hour has both import and export, only 50% of the hour's export can be assumed to occur
                // before the import within that same hour; the other 50% happens after the import.
                bool mixedHour = entry.Export > 0 && entry.Import > 0;
                var exportBeforeImport = mixedHour ? entry.Export * 0.5M : entry.Export;
                var exportAfterImport = entry.Export - exportBeforeImport;

                // Track remaining power budget in the hour (shared 3 kW limit across charge + discharge)
                var remainingHourPower = maxPowerPerHour;

                // 1. Charge from export occurring before import
                if (batterySoC < batteryCapacity && exportBeforeImport > 0 && remainingHourPower > 0)
                {
                    var neededCharge = batteryCapacity - batterySoC;
                    var maxExportUsed = Math.Min(neededCharge / chargeEfficiency, remainingHourPower);
                    var exportUsed = Math.Min(exportBeforeImport, maxExportUsed);
                    var addCharge = exportUsed * chargeEfficiency;

                    batterySoC += addCharge;
                    entry.Export -= exportUsed;
                    remainingHourPower -= exportUsed;
                    totalConversionLoss += exportUsed - addCharge;

                    if (batterySoC >= batteryCapacity)
                    {
                        batterySoC = batteryCapacity;
                        Console.WriteLine($"Battery full at {entry.Start}");
                    }
                }

                // 2. Discharge battery to cover import
                if (batterySoC > 0 && entry.Import > 0 && remainingHourPower > 0)
                {
                    var maxDeliverable = Math.Min(batterySoC * dischargeEfficiency, remainingHourPower);
                    var delivered = Math.Min(entry.Import, maxDeliverable);
                    var removeCharge = delivered / dischargeEfficiency;

                    batterySoC -= removeCharge;
                    entry.Import -= delivered;
                    remainingHourPower -= delivered;
                    totalBatteryUsage += delivered;
                    totalConversionLoss += removeCharge - delivered;

                    if (batterySoC <= 0)
                    {
                        batterySoC = 0;
                        Console.WriteLine($"Battery empty at {entry.Start}");
                    }
                }

                // 3. Charge from export occurring after import (in mixed import/export hours)
                if (batterySoC < batteryCapacity && exportAfterImport > 0 && remainingHourPower > 0)
                {
                    var neededCharge = batteryCapacity - batterySoC;
                    var maxExportUsed = Math.Min(neededCharge / chargeEfficiency, remainingHourPower);
                    var exportUsed = Math.Min(exportAfterImport, maxExportUsed);
                    var addCharge = exportUsed * chargeEfficiency;

                    batterySoC += addCharge;
                    entry.Export -= exportUsed;
                    remainingHourPower -= exportUsed;
                    totalConversionLoss += exportUsed - addCharge;

                    if (batterySoC >= batteryCapacity)
                    {
                        batterySoC = batteryCapacity;
                        Console.WriteLine($"Battery full at {entry.Start}");
                    }
                }

                if (entry.Import > 0)
                {
                    importedToday = true;
                }
            }

            // Evaluate the final day in the dataset
            if (currentDay != null && !importedToday)
            {
                if (currentDay.Value < firstDayWithoutImport)
                {
                    firstDayWithoutImport = currentDay.Value;
                }
                if (currentDay.Value > lastDayWithoutImport)
                {
                    lastDayWithoutImport = currentDay.Value;
                }

                numberOfDaysWithoutImport++;
            }

            var simulatedUsage = GetUsage(entries);

            var originalCost = (originalUsage.Import * importPrice) - (originalUsage.Export * exportPrice);
            var simulatedCost = (simulatedUsage.Import * importPrice) - (simulatedUsage.Export * exportPrice);
            var savings = originalCost - simulatedCost;

            var originalSelf = originalUsage.Generated > 0
                ? (originalUsage.Generated - originalUsage.Export) * 100 / originalUsage.Generated
                : 0;
            var simulatedSelf = simulatedUsage.Generated > 0
                ? (simulatedUsage.Generated - simulatedUsage.Export) * 100 / simulatedUsage.Generated
                : 0;

            Console.WriteLine("");
            Console.WriteLine($"           Import   Export   Generated   Battery   Loss   Idle   Self     Cost");
            Console.WriteLine($"Original:    {originalUsage.Import:0000}     {originalUsage.Export:0000}        {originalUsage.Generated:0000}                           {originalSelf:00.0}%   €{originalCost,7:0.00}");
            Console.WriteLine($"Simulated:   {simulatedUsage.Import:0000}     {simulatedUsage.Export:0000}        {simulatedUsage.Generated:0000}      {totalBatteryUsage:0000}   {totalConversionLoss:0000}   {totalIdleConsumption:0000}   {simulatedSelf:00.0}%   €{simulatedCost,7:0.00}");
            Console.WriteLine($"Savings:                                                                 €{savings,7:0.00}");
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