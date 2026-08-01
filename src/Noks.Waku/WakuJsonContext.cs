using System.Text.Json.Serialization;

namespace Noks.Waku;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WakuTransportDiagnostics))]
internal sealed partial class WakuJsonContext : JsonSerializerContext;
