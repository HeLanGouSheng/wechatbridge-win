using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bridge.Core.Batches;
using Bridge.Core.Config;
using Bridge.Core.LlmSocial;
using Bridge.Core.Records;

namespace Bridge.Core;

/// <summary>One JSON dialect for every file this app writes and every request it sends: camelCase, nulls
/// omitted, Chinese left readable instead of \u-escaped. Source-generated so property order is fixed by
/// declaration and nothing depends on reflection at runtime.</summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: true);

    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        TypeInfoResolver = BridgeJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, typeof(T), Options);

    public static string SerializeCompact<T>(T value) => JsonSerializer.Serialize(value, typeof(T), Compact);

    /// <summary>The exact bytes to put on the wire (and to sign).</summary>
    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, typeof(T), Compact);

    public static T? Deserialize<T>(string json) => (T?)JsonSerializer.Deserialize(json, typeof(T), Options);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BatchManifest))]
[JsonSerializable(typeof(InboundPayload))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(GroupMemoryFile))]
[JsonSerializable(typeof(DeliveryRecord))]
internal partial class BridgeJsonContext : JsonSerializerContext
{
}
