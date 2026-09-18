using System.Globalization;

namespace S3Harp.Server;

/// <summary>The RFC 1123 date form HTTP headers carry.</summary>
internal static class HttpDate
{
    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
}
