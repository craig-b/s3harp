using System.Globalization;

namespace S3Harp.Server;

/// <summary>Parses the integers clients send: plain decimal digits, with no sign, whitespace or separators.</summary>
internal static class DecimalDigits
{
    public static bool TryParseInt32(ReadOnlySpan<char> value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    public static bool TryParseInt64(ReadOnlySpan<char> value, out long result) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
}
