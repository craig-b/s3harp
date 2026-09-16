namespace S3Harp.Core;

/// <summary>Validates bucket names against the S3 naming rules.</summary>
public static class BucketName
{
    public static bool IsValid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length is >= 3 and <= 63
            && IsAlphanumeric(name[0])
            && IsAlphanumeric(name[^1])
            && name.All(c => IsAlphanumeric(c) || c is '-' or '.')
            && !name.Contains("..", StringComparison.Ordinal)
            && !LooksLikeIpAddress(name);
    }

    private static bool IsAlphanumeric(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    /// <summary>Four dot-separated numeric labels, which S3 reserves out of the bucket namespace.</summary>
    private static bool LooksLikeIpAddress(string name)
    {
        var labels = name.Split('.');
        return labels.Length == 4 && labels.All(label => label.All(char.IsAsciiDigit));
    }
}
