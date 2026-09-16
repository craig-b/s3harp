using S3Harp.Core;

namespace S3Harp.Server.Authentication;

/// <summary>Names the checksum algorithms by their <c>x-amz-checksum-*</c> headers.</summary>
public static class ChecksumHeaders
{
    private const string HeaderPrefix = "x-amz-checksum-";

    private static readonly (ChecksumAlgorithm Algorithm, string Suffix)[] Names =
    [
        (ChecksumAlgorithm.Crc32, "crc32"),
        (ChecksumAlgorithm.Crc32C, "crc32c"),
        (ChecksumAlgorithm.Crc64Nvme, "crc64nvme"),
        (ChecksumAlgorithm.Sha1, "sha1"),
        (ChecksumAlgorithm.Sha256, "sha256"),
    ];

    public static string HeaderName(ChecksumAlgorithm algorithm) =>
        HeaderPrefix + Names.First(name => name.Algorithm == algorithm).Suffix;

    public static bool TryParseHeaderName(string headerName, out ChecksumAlgorithm algorithm)
    {
        ArgumentNullException.ThrowIfNull(headerName);
        foreach (var (candidate, suffix) in Names)
        {
            if (headerName.Length == HeaderPrefix.Length + suffix.Length
                && headerName.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase)
                && headerName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                algorithm = candidate;
                return true;
            }
        }

        algorithm = default;
        return false;
    }

    /// <summary>
    /// The checksum a request declares for its body in an <c>x-amz-checksum-*</c> header.
    /// </summary>
    public static bool TryFindDeclared(
        IHeaderDictionary headers, out ChecksumAlgorithm algorithm, out string declared)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var (candidate, _) in Names)
        {
            if (headers.TryGetValue(HeaderName(candidate), out var value) && value.Count > 0)
            {
                algorithm = candidate;
                declared = value.ToString().Trim();
                return true;
            }
        }

        algorithm = default;
        declared = "";
        return false;
    }
}
