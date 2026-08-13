using System.Text;
using System.Text.Json;

namespace Noks.Application;

public static class WakuProfileBackupCodec
{
    public const string Format = "noks-waku-data";
    public const int CurrentVersion = 1;

    public static string Serialize(WakuProfile profile, DateTimeOffset? exportedAt = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string encodedProfile = WakuProfileCodec.Serialize(profile);
        using JsonDocument profileDocument = JsonDocument.Parse(encodedProfile);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteString("exportedAtUtc", (exportedAt ?? DateTimeOffset.UtcNow).ToUniversalTime());
            writer.WritePropertyName("profile");
            profileDocument.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    public static bool TryDeserialize(string? value, out WakuProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("format", out JsonElement format) ||
                format.ValueKind != JsonValueKind.String ||
                !string.Equals(format.GetString(), Format, StringComparison.Ordinal) ||
                !root.TryGetProperty("version", out JsonElement version) ||
                !version.TryGetInt32(out int versionValue) ||
                versionValue != CurrentVersion ||
                !root.TryGetProperty("exportedAtUtc", out JsonElement exportedAt) ||
                exportedAt.ValueKind != JsonValueKind.String ||
                !exportedAt.TryGetDateTimeOffset(out _) ||
                !root.TryGetProperty("profile", out JsonElement encodedProfile) ||
                encodedProfile.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            return WakuProfileCodec.TryDeserialize(encodedProfile.GetRawText(), out profile);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
