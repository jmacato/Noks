using System.Globalization;
using System.Reflection;

namespace Noks.AvaloniaApp.Startup;

internal static class PackageExpiration
{
    private const string BuildUtcKey = "Noks.PackageBuildUtc";
    private const string TimeLimitMonthsKey = "Noks.PackageTimeLimitMonths";

    public static bool TryGetBlockMessage(out string message)
    {
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);

        foreach (AssemblyMetadataAttribute attribute in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            metadata[attribute.Key] = attribute.Value ?? string.Empty;
        }

        if (!metadata.TryGetValue(BuildUtcKey, out string? buildUtcText))
        {
            message = string.Empty;
            return false;
        }

        if (!DateTimeOffset.TryParse(
            buildUtcText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset buildUtc))
        {
            message = "Invalid package timestamp";
            return true;
        }

        int months = 1;

        if (metadata.TryGetValue(TimeLimitMonthsKey, out string? monthsText)
            && !int.TryParse(monthsText, NumberStyles.None, CultureInfo.InvariantCulture, out months))
        {
            message = "Invalid package time limit";
            return true;
        }

        if (months <= 0)
        {
            message = "Invalid package time limit";
            return true;
        }

        DateTimeOffset expiresAt = buildUtc.AddMonths(months);

        if (DateTimeOffset.UtcNow < expiresAt)
        {
            message = string.Empty;
            return false;
        }

        message = $"Package expired {expiresAt:yyyy-MM-dd} UTC";
        return true;
    }
}
