using System.Buffers;

namespace S3Harp.Core;

/// <summary>Validates bucket names against the S3 naming rules.</summary>
public static class BucketName
{
    private static readonly SearchValues<char> NameCharacters = SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyz0123456789-."
    );

    private static readonly SearchValues<char> Digits = SearchValues.Create("0123456789");

    public static bool IsValid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var span = name.AsSpan();
        return span.Length is >= 3 and <= 63
            && IsAlphanumeric(span[0])
            && IsAlphanumeric(span[^1])
            && !span.ContainsAnyExcept(NameCharacters)
            && !span.Contains("..", StringComparison.Ordinal)
            && !LooksLikeIpAddress(span);
    }

    private static bool IsAlphanumeric(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    /// <summary>Four dot-separated numeric labels, which S3 reserves out of the bucket namespace.</summary>
    private static bool LooksLikeIpAddress(ReadOnlySpan<char> name)
    {
        var labels = 0;
        foreach (var range in name.Split('.'))
        {
            if (name[range].ContainsAnyExcept(Digits))
            {
                return false;
            }

            labels++;
        }

        return labels == 4;
    }
}
