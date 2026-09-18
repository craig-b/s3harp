using S3Harp.Core;

namespace S3Harp.Server;

/// <summary>
/// Reads the attributes a request assigns to an object: its content type, the
/// standard content headers, and the <c>x-amz-meta-*</c> user metadata.
/// </summary>
internal static class RequestAttributes
{
    private const string MetadataHeaderPrefix = "x-amz-meta-";

    /// <summary>The transfer encoding SDKs add for signed chunked uploads; it describes the request, not the object.</summary>
    private const string AwsChunked = "aws-chunked";

    public static ObjectAttributes Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ObjectAttributes(
            request.ContentType,
            ReadContentHeaders(request.Headers),
            ReadMetadata(request.Headers)
        );
    }

    private static ContentHeaders ReadContentHeaders(IHeaderDictionary headers) =>
        new(
            Value(headers.CacheControl),
            Value(headers.ContentDisposition),
            StoredContentEncoding(headers.ContentEncoding),
            Value(headers.ContentLanguage),
            Value(headers.Expires)
        );

    private static string? StoredContentEncoding(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        var encodings = header
            .Split(',')
            .Select(encoding => encoding.Trim())
            .Where(encoding =>
                encoding.Length > 0
                && !string.Equals(encoding, AwsChunked, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        return encodings.Count > 0 ? string.Join(", ", encodings) : null;
    }

    private static string? Value(string? header) => string.IsNullOrEmpty(header) ? null : header;

    private static Dictionary<string, string> ReadMetadata(IHeaderDictionary headers)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            if (header.Key.StartsWith(MetadataHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                metadata[header.Key[MetadataHeaderPrefix.Length..].ToLowerInvariant()] =
                    header.Value.ToString();
            }
        }

        return metadata;
    }
}
