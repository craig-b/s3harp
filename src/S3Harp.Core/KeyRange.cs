namespace S3Harp.Core;

/// <summary>Ordinal key-range arithmetic for prefix scans.</summary>
public static class KeyRange
{
    /// <summary>
    /// The smallest string ordering above every key that starts with the prefix,
    /// or null when the prefix's range is unbounded.
    /// </summary>
    public static string? PrefixSuccessor(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        for (var i = prefix.Length - 1; i >= 0; i--)
        {
            if (prefix[i] != char.MaxValue)
            {
                return string.Concat(prefix.AsSpan(0, i), [(char)(prefix[i] + 1)]);
            }
        }

        return null;
    }
}
