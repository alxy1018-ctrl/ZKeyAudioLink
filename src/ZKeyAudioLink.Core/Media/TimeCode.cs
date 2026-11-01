using System.Globalization;

namespace ZKeyAudioLink.Core.Media;

/// <summary>Parses and formats the mm:ss.fff start-time notation used by the UI.</summary>
public static class TimeCode
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().Replace('：', ':');
        if (s.StartsWith('-') || s.Contains("NaN", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = s.Split(':');
        if (parts.Length is 1 or 2 or 3)
        {
            double total = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var part) ||
                    part < 0 || double.IsNaN(part) || double.IsInfinity(part))
                {
                    return false;
                }

                total = total * 60 + part;
            }

            if (total < 0 || total > 24 * 60 * 60)
            {
                return false;
            }

            value = TimeSpan.FromSeconds(Math.Round(total, 3, MidpointRounding.AwayFromZero));
            return true;
        }

        return false;
    }

    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }
}

