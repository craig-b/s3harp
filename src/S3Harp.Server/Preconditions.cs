using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace S3Harp.Server;

public enum PreconditionOutcome
{
    Proceed,
    NotModified,
    PreconditionFailed,
}

/// <summary>The conditional request headers a client sent, as raw header values.</summary>
public sealed record ConditionalHeaders(
    string? IfMatch = null,
    string? IfNoneMatch = null,
    string? IfModifiedSince = null,
    string? IfUnmodifiedSince = null)
{
    /// <summary>The standard conditional headers of a request.</summary>
    public static ConditionalHeaders FromRequest(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return new ConditionalHeaders(
            Value(headers.IfMatch),
            Value(headers.IfNoneMatch),
            Value(headers.IfModifiedSince),
            Value(headers.IfUnmodifiedSince));
    }

    /// <summary>The <c>x-amz-copy-source-if-*</c> headers of a CopyObject request.</summary>
    public static ConditionalHeaders FromCopySource(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return new ConditionalHeaders(
            Value(headers["x-amz-copy-source-if-match"]),
            Value(headers["x-amz-copy-source-if-none-match"]),
            Value(headers["x-amz-copy-source-if-modified-since"]),
            Value(headers["x-amz-copy-source-if-unmodified-since"]));
    }

    private static string? Value(StringValues header) =>
        header.Count > 0 ? header.ToString() : null;
}

/// <summary>
/// Evaluates conditional headers against an object's ETag and modification time
/// with S3's precedence: <c>If-Match</c> outranks <c>If-Unmodified-Since</c>,
/// <c>If-None-Match</c> outranks <c>If-Modified-Since</c>, and a failed precondition
/// outranks a not-modified result. Dates compare at HTTP's second precision, and
/// an unparseable date leaves its header ignored.
/// </summary>
public static class Preconditions
{
    public static PreconditionOutcome Evaluate(
        ConditionalHeaders headers, string etag, DateTimeOffset lastModified)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(etag);

        var modified = TruncateToSeconds(lastModified);
        if (headers.IfMatch is not null)
        {
            if (!Matches(headers.IfMatch, etag))
            {
                return PreconditionOutcome.PreconditionFailed;
            }
        }
        else if (TryParseHttpDate(headers.IfUnmodifiedSince, out var unmodifiedSince)
            && modified > unmodifiedSince)
        {
            return PreconditionOutcome.PreconditionFailed;
        }

        if (headers.IfNoneMatch is not null)
        {
            if (Matches(headers.IfNoneMatch, etag))
            {
                return PreconditionOutcome.NotModified;
            }
        }
        else if (TryParseHttpDate(headers.IfModifiedSince, out var modifiedSince)
            && modified <= modifiedSince)
        {
            return PreconditionOutcome.NotModified;
        }

        return PreconditionOutcome.Proceed;
    }

    /// <summary>Whether an entity-tag list (<c>*</c>, or comma-separated, optionally quoted tags) names the ETag.</summary>
    private static bool Matches(string header, string etag)
    {
        var trimmed = header.Trim();
        if (trimmed == "*")
        {
            return true;
        }

        return trimmed.Split(',')
            .Select(tag => tag.Trim().Trim('"'))
            .Any(tag => string.Equals(tag, etag, StringComparison.Ordinal));
    }

    private static bool TryParseHttpDate(string? header, out DateTimeOffset date) =>
        DateTimeOffset.TryParseExact(
            header, "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Offset);
}
