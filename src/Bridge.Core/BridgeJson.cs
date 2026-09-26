using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bridge.Core.Batches;

namespace Bridge.Core;

/// <summary>One JSON dialect for every file this app writes: camelCase, indented, nulls omitted, and
/// Chinese left readable instead of \u-escaped. Source-generated so property order is fixed by
/// declaration and nothing depends on reflection at runtime.</summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = BridgeJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, typeof(T), Options);

    public static T? Deserialize<T>(string json) => (T?)JsonSerializer.Deserialize(json, typeof(T), Options);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BatchManifest))]
internal partial class BridgeJsonContext : JsonSerializerContext
{
}
