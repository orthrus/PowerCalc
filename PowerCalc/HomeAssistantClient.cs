using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PowerCalc
{
    internal static class HomeAssistantClient
    {
        public static async Task<Dictionary<DateTimeOffset, Entry>> FetchEntriesAsync(
            string haUrl,
            string accessToken,
            DateTimeOffset startTime,
            DateTimeOffset endTime)
        {
            var wsUri = BuildWebSocketUri(haUrl);

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(wsUri, CancellationToken.None);

            // 1. Receive auth_required
            var authReq = await ReceiveJsonAsync(ws);
            var msgType = authReq.RootElement.GetProperty("type").GetString();
            if (msgType != "auth_required")
            {
                throw new InvalidOperationException($"Unexpected initial WebSocket message type: {msgType}");
            }

            // 2. Send auth
            await SendJsonAsync(ws, new
            {
                type = "auth",
                access_token = accessToken
            });

            var authResp = await ReceiveJsonAsync(ws);
            var authRespType = authResp.RootElement.GetProperty("type").GetString();
            if (authRespType != "auth_ok")
            {
                var errMsg = authResp.RootElement.TryGetProperty("message", out var m) ? m.GetString() : authRespType;
                throw new InvalidOperationException($"Home Assistant authentication failed: {errMsg}");
            }

            // 3. Query energy/get_prefs to discover configured sensors
            await SendJsonAsync(ws, new
            {
                id = 1,
                type = "energy/get_prefs"
            });

            var prefsResp = await ReceiveJsonAsync(ws);
            if (!prefsResp.RootElement.GetProperty("success").GetBoolean())
            {
                var err = prefsResp.RootElement.GetProperty("error").GetProperty("message").GetString();
                throw new InvalidOperationException($"Failed to query energy/get_prefs: {err}");
            }

            var importSensors = new HashSet<string>();
            var exportSensors = new HashSet<string>();
            var solarSensors = new HashSet<string>();

            if (prefsResp.RootElement.GetProperty("result").TryGetProperty("energy_sources", out var sources))
            {
                foreach (var source in sources.EnumerateArray())
                {
                    var sourceType = source.GetProperty("type").GetString();
                    if (sourceType == "grid")
                    {
                        if (source.TryGetProperty("flow_from", out var flowFrom))
                        {
                            foreach (var flow in flowFrom.EnumerateArray())
                            {
                                if (flow.TryGetProperty("stat_energy_from", out var stat) && stat.GetString() is { Length: > 0 } s)
                                {
                                    importSensors.Add(s);
                                }
                            }
                        }
                        if (source.TryGetProperty("stat_energy_from", out var directFrom) && directFrom.ValueKind == JsonValueKind.String && directFrom.GetString() is { Length: > 0 } df)
                        {
                            importSensors.Add(df);
                        }

                        if (source.TryGetProperty("flow_to", out var flowTo))
                        {
                            foreach (var flow in flowTo.EnumerateArray())
                            {
                                if (flow.TryGetProperty("stat_energy_to", out var stat) && stat.GetString() is { Length: > 0 } s)
                                {
                                    exportSensors.Add(s);
                                }
                            }
                        }
                        if (source.TryGetProperty("stat_energy_to", out var directTo) && directTo.ValueKind == JsonValueKind.String && directTo.GetString() is { Length: > 0 } dt)
                        {
                            exportSensors.Add(dt);
                        }
                    }
                    else if (sourceType == "solar")
                    {
                        if (source.TryGetProperty("stat_energy_from", out var stat) && stat.GetString() is { Length: > 0 } s)
                        {
                            solarSensors.Add(s);
                        }
                    }
                }
            }

            var allSensors = importSensors.Concat(exportSensors).Concat(solarSensors).Distinct().ToArray();
            if (allSensors.Length == 0)
            {
                throw new InvalidOperationException("No grid or solar energy sensors found in Home Assistant Energy Dashboard preferences.");
            }

            Console.WriteLine($"{Ansi.Bold}{Ansi.Cyan}Discovered Energy Dashboard sensors:{Ansi.Reset}");
            Console.WriteLine($"  {Ansi.Dim}Import:{Ansi.Reset} {Ansi.Yellow}{string.Join(", ", importSensors)}{Ansi.Reset}");
            Console.WriteLine($"  {Ansi.Dim}Export:{Ansi.Reset} {Ansi.Cyan}{string.Join(", ", exportSensors)}{Ansi.Reset}");
            Console.WriteLine($"  {Ansi.Dim}Solar:{Ansi.Reset}  {Ansi.Green}{string.Join(", ", solarSensors)}{Ansi.Reset}");

            // 4. Query recorder/statistics_during_period
            await SendJsonAsync(ws, new
            {
                id = 2,
                type = "recorder/statistics_during_period",
                start_time = startTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                end_time = endTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                statistic_ids = allSensors,
                period = "hour",
                types = new[] { "sum", "change" },
                units = new { energy = "kWh" }
            });

            var statsResp = await ReceiveJsonAsync(ws);
            if (!statsResp.RootElement.GetProperty("success").GetBoolean())
            {
                var err = statsResp.RootElement.GetProperty("error").GetProperty("message").GetString();
                throw new InvalidOperationException($"Failed to query recorder/statistics_during_period: {err}");
            }

            var sortedEntries = new SortedDictionary<DateTimeOffset, Entry>();
            var resultElement = statsResp.RootElement.GetProperty("result");

            foreach (var sensorProperty in resultElement.EnumerateObject())
            {
                var sensorId = sensorProperty.Name;
                bool isImport = importSensors.Contains(sensorId);
                bool isExport = exportSensors.Contains(sensorId);
                bool isSolar = solarSensors.Contains(sensorId);

                decimal? previousSum = null;

                foreach (var point in sensorProperty.Value.EnumerateArray())
                {
                    var start = ParseTimestamp(point.GetProperty("start"));

                    decimal delta = 0;
                    if (point.TryGetProperty("sum", out var sumProp) && sumProp.ValueKind == JsonValueKind.Number)
                    {
                        var currentSum = sumProp.GetDecimal();
                        if (previousSum.HasValue)
                        {
                            delta = currentSum - previousSum.Value;
                        }
                        previousSum = currentSum;
                    }
                    else if (point.TryGetProperty("change", out var changeProp) && changeProp.ValueKind == JsonValueKind.Number)
                    {
                        delta = changeProp.GetDecimal();
                    }

                    if (!sortedEntries.TryGetValue(start, out var entry))
                    {
                        entry = new Entry
                        {
                            Start = start,
                            Import = 0,
                            Export = 0,
                            Generated = 0
                        };
                        sortedEntries.Add(start, entry);
                    }

                    if (isImport)
                    {
                        entry.Import += delta;
                    }
                    else if (isExport)
                    {
                        entry.Export += delta;
                    }
                    else if (isSolar)
                    {
                        entry.Generated += delta;
                    }
                }
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);

            return new Dictionary<DateTimeOffset, Entry>(sortedEntries);
        }

        private static Uri BuildWebSocketUri(string haUrl)
        {
            var trimmed = haUrl.Trim().TrimEnd('/');
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "ws://" + trimmed["http://".Length..];
            }
            else if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "wss://" + trimmed["https://".Length..];
            }
            else if (!trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                     !trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "ws://" + trimmed;
            }

            if (!trimmed.EndsWith("/api/websocket", StringComparison.OrdinalIgnoreCase))
            {
                trimmed += "/api/websocket";
            }

            return new Uri(trimmed);
        }

        private static DateTimeOffset ParseTimestamp(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                var val = element.GetDouble();
                // Home Assistant WebSocket returns timestamps in milliseconds when > 1e11, else seconds
                if (val > 1e11)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)val);
                }
                return DateTimeOffset.FromUnixTimeSeconds((long)val);
            }

            return DateTimeOffset.Parse(element.GetString()!, CultureInfo.InvariantCulture);
        }

        private static async Task SendJsonAsync<T>(ClientWebSocket ws, T payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("WebSocket closed unexpectedly by Home Assistant.");
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            ms.Position = 0;
            return await JsonDocument.ParseAsync(ms);
        }
    }
}
