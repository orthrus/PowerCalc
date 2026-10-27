using CsvHelper;
using CsvHelper.Configuration;
using System.CommandLine;
using System.Globalization;

namespace PowerCalc
{
    internal static class Ansi
    {
        public static bool Enabled => !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        public static string Reset => Enabled ? "\x1b[0m" : "";
        public static string Bold => Enabled ? "\x1b[1m" : "";
        public static string Dim => Enabled ? "\x1b[2m" : "";
        public static string Red => Enabled ? "\x1b[31m" : "";
        public static string Green => Enabled ? "\x1b[32m" : "";
        public static string Yellow => Enabled ? "\x1b[33m" : "";
        public static string Blue => Enabled ? "\x1b[34m" : "";
        public static string Magenta => Enabled ? "\x1b[35m" : "";
        public static string Cyan => Enabled ? "\x1b[36m" : "";
    }

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
        private record SimulationResult(
            decimal Capacity,
            decimal Import,
            decimal Export,
            decimal Generated,
            decimal BatteryUsage,
            decimal ConversionLoss,
            decimal IdleConsumption,
            decimal SelfPercentage,
            decimal Cost,
            decimal Savings,
            decimal DaysWithoutImport,
            DateTimeOffset FirstDayWithoutImport,
            DateTimeOffset LastDayWithoutImport);

        static int Main(string[] args)
        {
            var fileArgument = new Argument<string>("file")
            {
                Description = "Path to the Home Assistant statistics export CSV file",
                DefaultValueFactory = _ => "statistics-export.csv"
            };

            var capacityOption = new Option<decimal>("--capacity", "-c")
            {
                Description = "Usable battery capacity in kWh",
                DefaultValueFactory = _ => 7.1M
            };

            var degradationOption = new Option<decimal>("--degradation", "-d")
            {
                Description = "Annual battery capacity degradation factor (e.g. 0.02 for 2%/year)",
                DefaultValueFactory = _ => 0.02M
            };

            var maxPowerOption = new Option<decimal>("--max-power", "-p")
            {
                Description = "Max battery charge/discharge power in kW (kWh per hour)",
                DefaultValueFactory = _ => 3.0M
            };

            var idleOption = new Option<decimal>("--idle", "-i")
            {
                Description = "Hourly BMS/inverter standby consumption in kWh",
                DefaultValueFactory = _ => 0.020M
            };

            var chargeEffOption = new Option<decimal>("--charge-efficiency")
            {
                Description = "Battery charging efficiency factor (0-1)",
                DefaultValueFactory = _ => 0.91M
            };

            var dischargeEffOption = new Option<decimal>("--discharge-efficiency")
            {
                Description = "Battery discharging efficiency factor (0-1)",
                DefaultValueFactory = _ => 0.91M
            };

            var extraPvOption = new Option<decimal>("--extra-pv")
            {
                Description = "Extra PV generation factor (e.g. 0.50 for +50%)",
                DefaultValueFactory = _ => 0.00M
            };

            var importPriceOption = new Option<decimal>("--import-price")
            {
                Description = "Grid import price per kWh",
                DefaultValueFactory = _ => 0.29M
            };

            var exportPriceOption = new Option<decimal>("--export-price")
            {
                Description = "Grid export compensation per kWh",
                DefaultValueFactory = _ => 0.01M
            };

            var tenYearOption = new Option<bool>("--10year", "--10years", "-10")
            {
                Description = "Run a 10-year simulation including annual battery degradation"
            };

            var yearsOption = new Option<int>("--years", "-y")
            {
                Description = "Number of years to simulate (overrides --10year when > 1)",
                DefaultValueFactory = _ => 1
            };

            var haUrlOption = new Option<string>("--ha-url")
            {
                Description = "Home Assistant URL (e.g. http://homeassistant.local:8123 or ENV: HA_URL)"
            };

            var haTokenOption = new Option<string>("--ha-token")
            {
                Description = "Home Assistant Long-Lived Access Token, created in Home Assistant under Profile -> Security tab -> Long-lived access tokens (or ENV: HA_TOKEN)"
            };

            var startOption = new Option<DateTimeOffset?>("--start")
            {
                Description = "Start date/time for Home Assistant query (default: 1 year before --end)"
            };

            var endOption = new Option<DateTimeOffset?>("--end")
            {
                Description = "End date/time for Home Assistant query (default: end of yesterday / today 00:00 local time)"
            };

            var verboseOption = new Option<bool>("--verbose", "-v")
            {
                Description = "Print hourly battery full/empty events",
                DefaultValueFactory = _ => false
            };

            var rootCommand = new RootCommand("Home Assistant solar & home battery simulator")
            {
                fileArgument,
                capacityOption,
                degradationOption,
                maxPowerOption,
                idleOption,
                chargeEffOption,
                dischargeEffOption,
                extraPvOption,
                importPriceOption,
                exportPriceOption,
                tenYearOption,
                yearsOption,
                haUrlOption,
                haTokenOption,
                startOption,
                endOption,
                verboseOption
            };

            rootCommand.SetAction(async parseResult =>
            {
                var filePath = parseResult.GetValue(fileArgument);
                var batteryCapacity = parseResult.GetValue(capacityOption);
                var annualDegradation = parseResult.GetValue(degradationOption);
                var maxPowerPerHour = parseResult.GetValue(maxPowerOption);
                var idleConsumptionPerHour = parseResult.GetValue(idleOption);
                var chargeEfficiency = parseResult.GetValue(chargeEffOption);
                var dischargeEfficiency = parseResult.GetValue(dischargeEffOption);
                var extraPvFactor = parseResult.GetValue(extraPvOption);
                var importPrice = parseResult.GetValue(importPriceOption);
                var exportPrice = parseResult.GetValue(exportPriceOption);
                var run10Years = parseResult.GetValue(tenYearOption);
                var years = parseResult.GetValue(yearsOption);
                var haUrl = parseResult.GetValue(haUrlOption) ?? Environment.GetEnvironmentVariable("HA_URL");
                var haToken = parseResult.GetValue(haTokenOption) ?? Environment.GetEnvironmentVariable("HA_TOKEN");
                var startTimeOpt = parseResult.GetValue(startOption);
                var endTimeOpt = parseResult.GetValue(endOption);
                var verbose = parseResult.GetValue(verboseOption);

                if (run10Years && years <= 1)
                {
                    years = 10;
                }

                Dictionary<DateTimeOffset, Entry> entries;
                if (!string.IsNullOrWhiteSpace(haUrl))
                {
                    if (string.IsNullOrWhiteSpace(haToken))
                    {
                        Console.Error.WriteLine($"{Ansi.Red}Error: --ha-token (or HA_TOKEN environment variable) is required when using --ha-url.{Ansi.Reset}");
                        return 1;
                    }

                    var localTodayStart = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
                    var endTime = endTimeOpt ?? localTodayStart;
                    var startTime = startTimeOpt ?? endTime.AddYears(-1);

                    Console.WriteLine($"{Ansi.Dim}Connecting to Home Assistant at {haUrl} ({startTime:yyyy-MM-dd} to {endTime.AddDays(-1):yyyy-MM-dd})...{Ansi.Reset}");
                    entries = await HomeAssistantClient.FetchEntriesAsync(haUrl, haToken, startTime, endTime);
                }
                else
                {
                    if (!File.Exists(filePath))
                    {
                        Console.Error.WriteLine($"{Ansi.Red}File not found: {filePath}{Ansi.Reset}");
                        return 1;
                    }

                    entries = ParseFile(filePath);
                }

                if (entries.Count == 0)
                {
                    Console.Error.WriteLine($"{Ansi.Red}No records found.{Ansi.Reset}");
                    return 1;
                }

                var originalUsage = GetUsage(entries);
                var originalCost = (originalUsage.Import * importPrice) - (originalUsage.Export * exportPrice);
                var originalSelf = originalUsage.Generated > 0
                    ? (originalUsage.Generated - originalUsage.Export) * 100 / originalUsage.Generated
                    : 0;

                var roundTripEfficiency = chargeEfficiency * dischargeEfficiency * 100;
                Console.WriteLine("");
                Console.WriteLine($"{Ansi.Bold}{Ansi.Cyan}Battery Configuration:{Ansi.Reset}");
                Console.WriteLine($"  {Ansi.Dim}Capacity:{Ansi.Reset}   {Ansi.Bold}{batteryCapacity:0.00} kWh{Ansi.Reset} (Max Power: {maxPowerPerHour:0.0} kW, Degradation: {annualDegradation * 100:0.0}%/yr)");
                Console.WriteLine($"  {Ansi.Dim}Efficiency:{Ansi.Reset} {chargeEfficiency * 100:0.0}% charge / {dischargeEfficiency * 100:0.0}% discharge ({Ansi.Yellow}{roundTripEfficiency:0.0}%{Ansi.Reset} round-trip), Idle: {idleConsumptionPerHour * 1000:0} W");
                Console.WriteLine($"  {Ansi.Dim}Pricing:{Ansi.Reset}    Import {Ansi.Yellow}€{importPrice:0.00}/kWh{Ansi.Reset}, Export {Ansi.Cyan}€{exportPrice:0.00}/kWh{Ansi.Reset}{(extraPvFactor != 0 ? $", Extra PV: {Ansi.Green}{extraPvFactor * 100:+0.0;-0.0}%{Ansi.Reset}" : "")}");

                if (years <= 1)
                {
                    var result = RunYearSimulation(
                        entries,
                        batteryCapacity,
                        maxPowerPerHour,
                        idleConsumptionPerHour,
                        chargeEfficiency,
                        dischargeEfficiency,
                        extraPvFactor,
                        importPrice,
                        exportPrice,
                        originalCost,
                        verbose: verbose);

                    Console.WriteLine("");
                    Console.WriteLine($"{Ansi.Bold}           Import   Export   Generated   Battery   Loss   Idle   Self     Cost{Ansi.Reset}");
                    Console.WriteLine($"{Ansi.Dim}Original:{Ansi.Reset}    {Ansi.Yellow}{originalUsage.Import:0000}{Ansi.Reset}     {Ansi.Cyan}{originalUsage.Export:0000}{Ansi.Reset}        {Ansi.Green}{originalUsage.Generated:0000}{Ansi.Reset}                           {originalSelf:00.0}%   {Ansi.Yellow}€{originalCost,7:0.00}{Ansi.Reset}");
                    Console.WriteLine($"{Ansi.Bold}Simulated:{Ansi.Reset}   {Ansi.Yellow}{result.Import:0000}{Ansi.Reset}     {Ansi.Cyan}{result.Export:0000}{Ansi.Reset}        {Ansi.Green}{result.Generated:0000}{Ansi.Reset}      {Ansi.Green}{result.BatteryUsage:0000}{Ansi.Reset}   {Ansi.Red}{result.ConversionLoss:0000}{Ansi.Reset}   {Ansi.Red}{result.IdleConsumption:0000}{Ansi.Reset}   {Ansi.Bold}{result.SelfPercentage:00.0}%{Ansi.Reset}   {Ansi.Yellow}€{result.Cost,7:0.00}{Ansi.Reset}");
                    Console.WriteLine($"{Ansi.Bold}Savings:{Ansi.Reset}                                                                 {Ansi.Bold}{Ansi.Green}€{result.Savings,7:0.00}{Ansi.Reset}");
                    Console.WriteLine($"Days without import from grid: {Ansi.Bold}{Ansi.Green}{result.DaysWithoutImport}{Ansi.Reset} {Ansi.Dim}(First {result.FirstDayWithoutImport:yyyy-MM-dd}, Last {result.LastDayWithoutImport:yyyy-MM-dd}){Ansi.Reset}");
                }
                else
                {
                    Console.WriteLine("");
                    Console.WriteLine($"{Ansi.Bold}{Ansi.Cyan}{years}-Year Simulation{Ansi.Reset} {Ansi.Dim}(Initial capacity: {batteryCapacity:0.00} kWh, Degradation: {annualDegradation * 100:0.0}%/year){Ansi.Reset}");
                    Console.WriteLine($"{Ansi.Dim}Original yearly baseline:{Ansi.Reset} Import {Ansi.Yellow}{originalUsage.Import:0} kWh{Ansi.Reset}, Export {Ansi.Cyan}{originalUsage.Export:0} kWh{Ansi.Reset}, Generated {Ansi.Green}{originalUsage.Generated:0} kWh{Ansi.Reset}, Self {originalSelf:00.0}%, Cost {Ansi.Yellow}€{originalCost:0.00}{Ansi.Reset}");
                    Console.WriteLine("");
                    Console.WriteLine($"{Ansi.Bold}Year   Cap(kWh)   Import   Export   Battery   Loss   Idle   Self    ZeroDays   Cost       Savings    Cumulative{Ansi.Reset}");

                    decimal cumulativeSavings = 0;
                    decimal totalMultiYearBattery = 0;

                    for (int year = 1; year <= years; year++)
                    {
                        var effectiveCapacity = Math.Max(0, batteryCapacity * (1.0M - (annualDegradation * (year - 1))));
                        var result = RunYearSimulation(
                            entries,
                            effectiveCapacity,
                            maxPowerPerHour,
                            idleConsumptionPerHour,
                            chargeEfficiency,
                            dischargeEfficiency,
                            extraPvFactor,
                            importPrice,
                            exportPrice,
                            originalCost,
                            verbose: verbose);

                        cumulativeSavings += result.Savings;
                        totalMultiYearBattery += result.BatteryUsage;

                        Console.WriteLine($"  {Ansi.Bold}{year,2}{Ansi.Reset}      {result.Capacity,5:0.00}     {Ansi.Yellow}{result.Import:0000}{Ansi.Reset}     {Ansi.Cyan}{result.Export:0000}{Ansi.Reset}      {Ansi.Green}{result.BatteryUsage:0000}{Ansi.Reset}   {Ansi.Red}{result.ConversionLoss:0000}{Ansi.Reset}   {Ansi.Red}{result.IdleConsumption:0000}{Ansi.Reset}   {result.SelfPercentage:00.0}%        {Ansi.Green}{result.DaysWithoutImport,3}{Ansi.Reset}   {Ansi.Yellow}€{result.Cost,7:0.00}{Ansi.Reset}   {Ansi.Green}€{result.Savings,7:0.00}{Ansi.Reset}     {Ansi.Bold}{Ansi.Green}€{cumulativeSavings,8:0.00}{Ansi.Reset}");
                    }

                    Console.WriteLine("");
                    Console.WriteLine($"Total {years}-year battery energy delivered: {Ansi.Bold}{Ansi.Green}{totalMultiYearBattery:0} kWh{Ansi.Reset}");
                    Console.WriteLine($"Total {years}-year savings:                  {Ansi.Bold}{Ansi.Green}€{cumulativeSavings:0.00}{Ansi.Reset} {Ansi.Dim}(avg €{cumulativeSavings / years:0.00}/year){Ansi.Reset}");
                }

                return 0;
            });

            return rootCommand.Parse(args).Invoke();
        }

        private static SimulationResult RunYearSimulation(
            Dictionary<DateTimeOffset, Entry> sourceEntries,
            decimal batteryCapacity,
            decimal maxPowerPerHour,
            decimal idleConsumptionPerHour,
            decimal chargeEfficiency,
            decimal dischargeEfficiency,
            decimal extraPvFactor,
            decimal importPrice,
            decimal exportPrice,
            decimal originalCost,
            bool verbose)
        {
            var entries = sourceEntries.ToDictionary(
                kvp => kvp.Key,
                kvp => new Entry
                {
                    Start = kvp.Value.Start,
                    Import = kvp.Value.Import,
                    Export = kvp.Value.Export,
                    Generated = kvp.Value.Generated,
                });

            decimal batterySoC = 0;
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
                        if (verbose)
                        {
                            Console.WriteLine($"{Ansi.Green}Battery full at {entry.Start}{Ansi.Reset}");
                        }
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
                        if (verbose)
                        {
                            Console.WriteLine($"{Ansi.Yellow}Battery empty at {entry.Start}{Ansi.Reset}");
                        }
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
                        if (verbose)
                        {
                            Console.WriteLine($"{Ansi.Green}Battery full at {entry.Start}{Ansi.Reset}");
                        }
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
            var simulatedCost = (simulatedUsage.Import * importPrice) - (simulatedUsage.Export * exportPrice);
            var savings = originalCost - simulatedCost;
            var simulatedSelf = simulatedUsage.Generated > 0
                ? (simulatedUsage.Generated - simulatedUsage.Export) * 100 / simulatedUsage.Generated
                : 0;

            return new SimulationResult(
                batteryCapacity,
                simulatedUsage.Import,
                simulatedUsage.Export,
                simulatedUsage.Generated,
                totalBatteryUsage,
                totalConversionLoss,
                totalIdleConsumption,
                simulatedSelf,
                simulatedCost,
                savings,
                numberOfDaysWithoutImport,
                firstDayWithoutImport,
                lastDayWithoutImport);
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