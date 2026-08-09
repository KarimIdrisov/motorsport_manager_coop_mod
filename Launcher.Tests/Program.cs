using MotorsportManagerCoopLauncher;

const string payload = "{\"type\":\"telemetry\",\"session\":\"Practice\",\"vehicles\":[{\"id\":10}]}";
var telemetry = TelemetryJson.Parse(payload) ?? throw new Exception("Telemetry packet was rejected");
GC.Collect();
if (telemetry.GetProperty("session").GetString() != "Practice") throw new Exception("Cloned JSON is unavailable after parser disposal");
if (TelemetryJson.Parse("{\"type\":\"welcome\"}") != null) throw new Exception("Non-telemetry packet was accepted");
Console.WriteLine("CONTROLLER PAYLOAD TEST PASS: cloned telemetry survives JsonDocument disposal");
