using global::System.Text.Json;

namespace Drone.Autonomy;

public class BehaviorRule
{
    public string Name { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Action { get; set; } = "";
    public Dictionary<string, JsonElement> ActionParams { get; set; } = new();
    public string? Condition { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Check if this rule matches the given event (trigger type + optional condition).</summary>
    public bool MatchesCondition(DroneEvent evt)
    {
        if (Trigger != "*" && Trigger != evt.Type) return false;
        if (!string.IsNullOrEmpty(Condition))
            return EvaluateCondition(evt.Data);
        return true;
    }

    /// <summary>Simple condition evaluator for JSON event data.
    /// Supports: "field > value", "field &lt; value", "field == value", "field != value".</summary>
    private bool EvaluateCondition(object data)
    {
        if (string.IsNullOrEmpty(Condition)) return true;
        try
        {
            var json = JsonSerializer.Serialize(data);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var operators = new[] { ">=", "<=", "!=", "==", ">", "<" };
            foreach (var op in operators)
            {
                var idx = Condition.IndexOf(op, StringComparison.Ordinal);
                if (idx < 0) continue;

                var field = Condition[..idx].Trim();
                var valueStr = Condition[(idx + op.Length)..].Trim().Trim(' ', '"', (char)39);

                if (!root.TryGetProperty(field, out var prop)) return false;

                var fieldVal = prop.ValueKind == JsonValueKind.Number ? prop.GetDouble() : 0;
                if (double.TryParse(valueStr, out var compareVal))
                {
                    return op switch
                    {
                        ">" => fieldVal > compareVal,
                        "<" => fieldVal < compareVal,
                        ">=" => fieldVal >= compareVal,
                        "<=" => fieldVal <= compareVal,
                        "==" => Math.Abs(fieldVal - compareVal) < 0.001,
                        "!=" => Math.Abs(fieldVal - compareVal) >= 0.001,
                        _ => false
                    };
                }
                var fieldStr = prop.GetString() ?? "";
                return op switch
                {
                    "==" => fieldStr == valueStr,
                    "!=" => fieldStr != valueStr,
                    _ => false
                };
            }
        }
        catch { /* condition evaluation failure is non-fatal */ }
        return false;
    }
}
