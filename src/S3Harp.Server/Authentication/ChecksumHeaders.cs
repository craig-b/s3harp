using S3Harp.Core;

namespace S3Harp.Server.Authentication;

/// <summary>
/// The checksum headers of S3's wire protocol: the <c>x-amz-checksum-*</c> value
/// headers, the algorithm and type headers, and the checksum mode of a read.
/// </summary>
public static class ChecksumHeaders
{
    private const string HeaderPrefix = "x-amz-checksum-";
    private const string TypeHeader = "x-amz-checksum-type";
    private const string AlgorithmHeader = "x-amz-checksum-algorithm";
    private const string SdkAlgorithmHeader = "x-amz-sdk-checksum-algorithm";
    private const string TrailerHeader = "x-amz-trailer";
    private const string ModeHeader = "x-amz-checksum-mode";

    /// <summary>Objects uploaded without any checksum request get this one, as on S3.</summary>
    private const ChecksumAlgorithm DefaultAlgorithm = ChecksumAlgorithm.Crc64Nvme;

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

    /// <summary>
    /// The algorithm an upload's stored checksum uses: the one the client declared a
    /// value for, announced as a trailer, or asked for by name, else the default.
    /// </summary>
    public static ChecksumAlgorithm UploadAlgorithm(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (TryFindDeclared(headers, out var declared, out _))
        {
            return declared;
        }

        string? trailer = headers[TrailerHeader];
        if (trailer is not null && TryParseHeaderName(trailer.Trim(), out var announced))
        {
            return announced;
        }

        return RequestedAlgorithm(headers) ?? DefaultAlgorithm;
    }

    /// <summary>The algorithm a request asks for by name, when it does.</summary>
    public static ChecksumAlgorithm? RequestedAlgorithm(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var header in new[] { SdkAlgorithmHeader, AlgorithmHeader })
        {
            string? name = headers[header];
            if (name is not null && ChecksumAlgorithms.TryParseName(name.Trim(), out var algorithm))
            {
                return algorithm;
            }
        }

        return null;
    }

    /// <summary>True when a read asks for the object's checksum with <c>x-amz-checksum-mode</c>.</summary>
    public static bool ModeEnabled(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return string.Equals(headers[ModeHeader], "ENABLED", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Announces a checksum in its value header and the type header.</summary>
    public static void Write(IHeaderDictionary headers, Checksum checksum)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(checksum);
        headers[HeaderName(checksum.Algorithm)] = checksum.Value;
        headers[TypeHeader] = TypeName(checksum.Type);
    }

    /// <summary>The XML element carrying an algorithm's value, such as <c>ChecksumSHA256</c>.</summary>
    public static string ElementName(ChecksumAlgorithm algorithm) =>
        "Checksum" + ChecksumAlgorithms.Name(algorithm);

    public static string TypeName(ChecksumType type) => type switch
    {
        ChecksumType.FullObject => "FULL_OBJECT",
        ChecksumType.Composite => "COMPOSITE",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
