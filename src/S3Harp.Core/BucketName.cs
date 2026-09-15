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
            && name.All(c => IsAlphanumeric(c) || c is '-' or '.');
    }

    private static bool IsAlphanumeric(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
