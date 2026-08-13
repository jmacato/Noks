using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noks.Cryptography;
using Noks.Waku;
using Noks.Dct3.Sim;

namespace Noks.Application;

public static class WakuProfileCodec
{
    public static string Serialize(WakuProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        byte[] entropy = profile.CopyEntropy();
        try
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", WakuProfile.CurrentVersion);
                writer.WriteString("entropy", Convert.ToBase64String(entropy));
                writer.WriteString("userName", profile.UserName);
                writer.WriteString("phoneNumber", profile.PhoneNumber);
                writer.WriteNumber("phoneNumberGeneration", profile.PhoneNumberGeneration);
                writer.WriteStartArray("contacts");
                foreach (WakuProfileContact contact in profile.Contacts.OrderBy(
                             value => value.StableContactId,
                             StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("stableContactId", contact.StableContactId);
                    writer.WriteString("userName", contact.UserName);
                    writer.WriteString("currentNumber", contact.CurrentNumber);
                    writer.WriteNumber("keyGeneration", contact.KeyGeneration);
                    writer.WriteString("contactCardPublicKey", Convert.ToBase64String(contact.ContactCardPublicKey.AsSpan()));
                    writer.WriteString("envelopePublicKey", Convert.ToBase64String(contact.EnvelopePublicKey.AsSpan()));
                    writer.WriteString("mailboxPublicKey", Convert.ToBase64String(contact.MailboxPublicKey.AsSpan()));
                    if (contact.PqcMailboxPublicKey.Length != 0)
                    {
                        writer.WriteString(
                            "pqcMailboxPublicKey",
                            Convert.ToBase64String(contact.PqcMailboxPublicKey.AsSpan()));
                    }
                    if (contact.PqcSigningPublicKey.Length != 0)
                    {
                        writer.WriteString(
                            "pqcSigningPublicKey",
                            Convert.ToBase64String(contact.PqcSigningPublicKey.AsSpan()));
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("bindings");
                foreach (WakuNumberBinding binding in profile.NumberBindings)
                {
                    writer.WriteStartObject();
                    writer.WriteString("localNumber", binding.LocalNumber);
                    writer.WriteString("stableContactId", binding.StableContactId);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("rememberedEvents");
                foreach (WakuRememberedEvent remembered in profile.RememberedEvents)
                {
                    writer.WriteStartObject();
                    writer.WriteString("eventId", remembered.EventId);
                    writer.WriteNumber("expiresAtUnixMilliseconds", remembered.ExpiresAtUnixMilliseconds);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartObject("durableSimFiles");
                if (profile.DurableAdnFile.HasValue)
                    writer.WriteString("adn", Convert.ToBase64String(profile.DurableAdnFile.Value.Span));
                if (profile.DurableSmsFile.HasValue)
                    writer.WriteString("sms", Convert.ToBase64String(profile.DurableSmsFile.Value.Span));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public static bool TryDeserialize(string? value, out WakuProfile? profile)
        => TryDeserialize(value, out profile, out _);

    internal static bool TryDeserialize(
        string? value,
        out WakuProfile? profile,
        out bool requiresSave)
    {
        profile = null;
        requiresSave = false;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        byte[]? entropy = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            int version = root.GetProperty("version").GetInt32();
            if (version is not (1 or 2 or 3 or 4) && version != WakuProfile.CurrentVersion)
                return false;
            entropy = Convert.FromBase64String(root.GetProperty("entropy").GetString() ?? "");
            List<WakuRememberedEvent> rememberedEvents = DecodeRememberedEvents(root);
            if (version == 1)
            {
                int previousGeneration = root.GetProperty("phoneNumberGeneration").GetInt32();
                if (previousGeneration < 1 || previousGeneration == int.MaxValue)
                    return false;
                profile = new WakuProfile(
                    entropy,
                    SimPhonebookCodec.CreateAlphaIdentifierAlias(
                        root.GetProperty("userName").GetString() ?? ""),
                    NoksTemporaryNumber.Generate(),
                    previousGeneration + 1,
                    rememberedEvents: rememberedEvents);
                requiresSave = true;
                return true;
            }
            List<WakuProfileContact> contacts = [];
            foreach (JsonElement item in root.GetProperty("contacts").EnumerateArray())
            {
                contacts.Add(new WakuProfileContact(
                    item.GetProperty("stableContactId").GetString() ?? "",
                    item.GetProperty("userName").GetString() ?? "",
                    item.GetProperty("currentNumber").GetString() ?? "",
                    item.GetProperty("keyGeneration").GetInt32(),
                    Convert.FromBase64String(item.GetProperty("contactCardPublicKey").GetString() ?? ""),
                    Convert.FromBase64String(item.GetProperty("envelopePublicKey").GetString() ?? ""),
                    Convert.FromBase64String(item.GetProperty("mailboxPublicKey").GetString() ?? ""),
                    item.TryGetProperty("pqcMailboxPublicKey", out JsonElement pqcMailboxPublicKey)
                        ? Convert.FromBase64String(pqcMailboxPublicKey.GetString() ?? "")
                        : [],
                    item.TryGetProperty("pqcSigningPublicKey", out JsonElement pqcSigningPublicKey)
                        ? Convert.FromBase64String(pqcSigningPublicKey.GetString() ?? "")
                        : []));
            }
            List<WakuNumberBinding> bindings = [];
            foreach (JsonElement item in root.GetProperty("bindings").EnumerateArray())
            {
                bindings.Add(new WakuNumberBinding(
                    item.GetProperty("localNumber").GetString() ?? "",
                    item.GetProperty("stableContactId").GetString() ?? ""));
            }
            ReadOnlyMemory<byte>? durableAdnFile = null;
            ReadOnlyMemory<byte>? durableSmsFile = null;
            if (version >= 3 &&
                root.TryGetProperty("durableSimFiles", out JsonElement durableFiles) &&
                durableFiles.ValueKind == JsonValueKind.Object)
            {
                durableAdnFile = DecodeOptionalBytes(durableFiles, "adn");
                durableSmsFile = DecodeOptionalBytes(durableFiles, "sms");
            }
            profile = new WakuProfile(
                entropy,
                root.GetProperty("userName").GetString() ?? "",
                root.GetProperty("phoneNumber").GetString() ?? "",
                root.GetProperty("phoneNumberGeneration").GetInt32(),
                contacts,
                bindings,
                rememberedEvents,
                durableAdnFile,
                durableSmsFile);
            requiresSave = version != WakuProfile.CurrentVersion;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            profile?.Dispose();
            profile = null;
            return false;
        }
        finally
        {
            if (entropy is not null)
                CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static ReadOnlyMemory<byte>? DecodeOptionalBytes(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement encoded) ||
            encoded.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        byte[] value = Convert.FromBase64String(encoded.GetString() ?? "");
        return value.Length == 0 ? null : value;
    }

    private static List<WakuRememberedEvent> DecodeRememberedEvents(JsonElement root)
    {
        List<WakuRememberedEvent> rememberedEvents = [];
        if (!root.TryGetProperty("rememberedEvents", out JsonElement rememberedItems) ||
            rememberedItems.ValueKind != JsonValueKind.Array)
        {
            return rememberedEvents;
        }
        foreach (JsonElement item in rememberedItems.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("eventId", out JsonElement eventIdElement) &&
                eventIdElement.TryGetGuid(out Guid eventId) &&
                item.TryGetProperty("expiresAtUnixMilliseconds", out JsonElement expiresElement) &&
                expiresElement.TryGetInt64(out long expiresAtUnixMilliseconds))
            {
                rememberedEvents.Add(new WakuRememberedEvent(eventId, expiresAtUnixMilliseconds));
            }
        }
        return rememberedEvents;
    }
}
