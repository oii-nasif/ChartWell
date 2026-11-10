namespace HealthTracker.Services.Ai;

/// <summary>
/// Tiny builder for the OpenAPI-subset schema objects Gemini accepts in
/// <c>generationConfig.responseSchema</c>. Types are upper-case as the REST enum requires.
/// Everything we ask for is marked <c>required</c> and the optional fields are <c>nullable</c>,
/// so the model returns an explicit null instead of omitting the property.
/// </summary>
public static class GeminiSchema
{
    public static Dictionary<string, object> Object(Dictionary<string, object> properties, params string[] required)
    {
        var o = new Dictionary<string, object>
        {
            ["type"] = "OBJECT",
            ["properties"] = properties,
        };
        var req = required.Length > 0 ? required : properties.Keys.ToArray();
        o["required"] = req;
        // Keeps the output key order stable (helps the model and the logs).
        o["propertyOrdering"] = properties.Keys.ToArray();
        return o;
    }

    public static Dictionary<string, object> String(string description, bool nullable = false)
        => Scalar("STRING", description, nullable);

    public static Dictionary<string, object> Integer(string description, bool nullable = false)
        => Scalar("INTEGER", description, nullable);

    public static Dictionary<string, object> Number(string description, bool nullable = false)
        => Scalar("NUMBER", description, nullable);

    public static Dictionary<string, object> Enum(string description, IEnumerable<string> values, bool nullable = false)
    {
        var s = Scalar("STRING", description, nullable);
        s["enum"] = values.ToArray();
        return s;
    }

    public static Dictionary<string, object> Array(string description, Dictionary<string, object> items)
        => new()
        {
            ["type"] = "ARRAY",
            ["description"] = description,
            ["items"] = items,
        };

    private static Dictionary<string, object> Scalar(string type, string description, bool nullable)
    {
        var s = new Dictionary<string, object>
        {
            ["type"] = type,
            ["description"] = description,
        };
        if (nullable) s["nullable"] = true;
        return s;
    }
}
