using System.Text.Json;

namespace MotorsportManagerCoopLauncher;

internal static class TelemetryJson
{
    internal static JsonElement? Parse(string line)
    {
        using JsonDocument json = JsonDocument.Parse(line);
        if (!json.RootElement.TryGetProperty("type", out JsonElement type) || type.GetString() != "telemetry") return null;
        return json.RootElement.Clone();
    }
}
