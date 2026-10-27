# PowerCalc

A command-line tool for simulating home battery storage and extra solar (PV) generation using historical hourly energy data from **Home Assistant**.

PowerCalc can connect directly to your Home Assistant instance via WebSocket (auto-discovering your configured Energy Dashboard sensors) or read an exported `statistics` CSV file.

---

## Features

- **Direct Home Assistant Integration**: Connects to the Home Assistant WebSocket API (`/api/websocket`), queries `energy/get_prefs` to automatically find your grid import, grid export, and solar production sensors, and fetches hourly long-term statistics.
- **CSV Offline Mode**: Parses exported Home Assistant `statistics` CSV files.
- **Realistic Battery Physics**:
  - Configurable usable capacity (`kWh`) and max charge/discharge power (`kW`).
  - Separate charging and discharging conversion efficiencies.
  - Continuous inverter/BMS standby power consumption (`idle`).
  - 50/50 split handling for mixed import/export hours.
- **Multi-Year Degradation**: Simulate 10+ years of operation including annual battery capacity degradation.
- **PV Expansion**: Simulate adding extra solar capacity (`--extra-pv`).
- **Financial Analysis**: Calculates baseline vs. simulated electricity costs, annual savings, cumulative multi-year savings, and zero-grid-import days.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

---

## Building

```bash
dotnet build
```

---

## Usage

```text
Usage:
  PowerCalc [<file>] [options]

Arguments:
  <file>  Path to the Home Assistant statistics export CSV file [default: statistics-export.csv]

Options:
  -c, --capacity <capacity>                      Usable battery capacity in kWh [default: 7.1]
  -d, --degradation <degradation>                Annual battery capacity degradation factor (e.g. 0.02 for 2%/year) [default: 0.02]
  -p, --max-power <max-power>                    Max battery charge/discharge power in kW (kWh per hour) [default: 3.0]
  -i, --idle <idle>                              Hourly BMS/inverter standby consumption in kWh [default: 0.020]
  --charge-efficiency <charge-efficiency>        Battery charging efficiency factor (0-1) [default: 0.91]
  --discharge-efficiency <discharge-efficiency>  Battery discharging efficiency factor (0-1) [default: 0.91]
  --extra-pv <extra-pv>                          Extra PV generation factor (e.g. 0.50 for +50%) [default: 0.00]
  --import-price <import-price>                  Grid import price per kWh [default: 0.29]
  --export-price <export-price>                  Grid export compensation per kWh [default: 0.01]
  -10, --10year, --10years                       Run a 10-year simulation including annual battery degradation
  -y, --years <years>                            Number of years to simulate (overrides --10year when > 1) [default: 1]
  --ha-url <ha-url>                              Home Assistant URL (e.g. http://homeassistant.local:8123 or ENV: HA_URL)
  --ha-token <ha-token>                          Home Assistant Long-Lived Access Token, created in Home Assistant under
                                                 Profile -> Security tab -> Long-lived access tokens (or ENV: HA_TOKEN)
  --start <start>                                Start date/time for Home Assistant query (default: 1 year before --end)
  --end <end>                                    End date/time for Home Assistant query (default: end of yesterday / today 00:00 local time)
  -v, --verbose                                  Print hourly battery full/empty events
  -?, -h, --help                                 Show help and usage information
  --version                                      Show version information
```

---

## Examples

### 1. Connect Directly to Home Assistant
Create a **Long-Lived Access Token** in Home Assistant under **Profile -> Security tab -> Long-lived access tokens**, then run:

```bash
dotnet run --project PowerCalc -- \
  --ha-url http://homeassistant.local:8123 \
  --ha-token "<YOUR_TOKEN>"
```

Or using `HA_URL` and `HA_TOKEN` environment variables:

```bash
export HA_URL="http://homeassistant.local:8123"
export HA_TOKEN="<YOUR_TOKEN>"

dotnet run --project PowerCalc -- --capacity 8.0 -10
```

### 2. Run a 10-Year Degradation Simulation
```bash
dotnet run --project PowerCalc -- \
  --ha-url http://homeassistant.local:8123 \
  --ha-token "<YOUR_TOKEN>" \
  --capacity 7.1 \
  --degradation 0.02 \
  -10
```

### 3. Simulate Extra Solar Panels (+50% PV)
```bash
dotnet run --project PowerCalc -- \
  --ha-url http://homeassistant.local:8123 \
  --ha-token "<YOUR_TOKEN>" \
  --extra-pv 0.50
```

### 4. Run from an Exported CSV File
If you prefer to export data manually from Home Assistant's SQLite database:

```sql
SELECT *
FROM "statistics"
WHERE start_ts >= unixepoch('2025-01-01 00:00:00')
  AND start_ts <= unixepoch('2026-01-01 00:00:00')
  AND metadata_id IN (246, 19, 20, 21, 22)
ORDER BY start_ts
```

Then run:

```bash
dotnet run --project PowerCalc -- /path/to/statistics-export.csv
```

---

## Example Output

```text
Battery Configuration:
  Capacity:   7.10 kWh (Max Power: 3.0 kW, Degradation: 2.0%/yr)
  Efficiency: 91.0% charge / 91.0% discharge (82.8% round-trip), Idle: 20 W
  Pricing:    Import €0.29/kWh, Export €0.01/kWh

           Import   Export   Generated   Battery   Loss   Idle   Self     Cost
Original:    3985     3042        4568                           33.4%   €1125.32
Simulated:   2576     1141        4568      1523   0316   0175   75.0%   € 735.71
Savings:                                                                 € 389.61
Days without import from grid: 50 (First 2026-02-27, Last 2026-10-05)
```
