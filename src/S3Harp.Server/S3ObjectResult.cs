using System.Buffers;
using System.Globalization;
using S3Harp.Core;
using S3Harp.Server.Authentication;

namespace S3Harp.Server;

/// <summary>
/// Serves an object's headers and, for GET, streams its content — the whole object,
/// or a 206 slice when a range is given. A null content stream produces the HEAD
/// shape: full headers, empty body. A parts count is announced in
/// <c>x-amz-mp-parts-count</c>, as S3 does when a part of a multipart object is
/// requested, and a checksum in its <c>x-amz-checksum-*</c> headers when the read
/// asked for one.
/// </summary>
internal sealed class S3ObjectResult(
    ObjectRecord record,
    Stream? content,
    RangeEvaluation? range = null,
    int? partsCount = null,
    Checksum? checksum = null
) : IResult
{
    private const int BufferSize = 64 * 1024;

    /// <exception cref="IOException">The response could not be written.</exception>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var partial = range is { Outcome: RangeOutcome.Partial } evaluation
            ? evaluation
            : (RangeEvaluation?)null;
        var response = httpContext.Response;
        response.StatusCode = partial is null
            ? StatusCodes.Status200OK
            : StatusCodes.Status206PartialContent;
        response.ContentType = record.ContentType ?? "application/octet-stream";
        response.ContentLength = partial is { } p ? p.To - p.From + 1 : record.Size;
        response.Headers.AcceptRanges = "bytes";
        response.Headers.ETag = $"\"{record.ETag}\"";
        response.Headers.LastModified = HttpDate.Format(record.LastModified);
        if (partial is { } contentRange)
        {
            response.Headers.ContentRange =
                $"bytes {contentRange.From}-{contentRange.To}/{record.Size}";
        }

        if (partsCount is { } count)
        {
            response.Headers["x-amz-mp-parts-count"] = count.ToString(CultureInfo.InvariantCulture);
        }

        if (checksum is not null)
        {
            ChecksumHeaders.Write(response.Headers, checksum);
        }

        WriteContentHeaders(response.Headers, record.ContentHeaders);
        foreach (var (name, value) in record.Metadata)
        {
            response.Headers["x-amz-meta-" + name] = value;
        }

        if (content is null)
        {
            return;
        }

        await using (content.ConfigureAwait(false))
        {
            if (partial is { } slice)
            {
                await CopySliceAsync(
                        content,
                        response.Body,
                        slice.From,
                        slice.To - slice.From + 1,
                        httpContext.RequestAborted
                    )
                    .ConfigureAwait(false);
            }
            else
            {
                await content
                    .CopyToAsync(response.Body, httpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
    }

    private static void WriteContentHeaders(IHeaderDictionary headers, ContentHeaders content)
    {
        if (content.CacheControl is not null)
        {
            headers.CacheControl = content.CacheControl;
        }

        if (content.ContentDisposition is not null)
        {
            headers.ContentDisposition = content.ContentDisposition;
        }

        if (content.ContentEncoding is not null)
        {
            headers.ContentEncoding = content.ContentEncoding;
        }

        if (content.ContentLanguage is not null)
        {
            headers.ContentLanguage = content.ContentLanguage;
        }

        if (content.Expires is not null)
        {
            headers.Expires = content.Expires;
        }
    }

    /// <exception cref="IOException">The response could not be written.</exception>
    private static async Task CopySliceAsync(
        Stream source,
        Stream destination,
        long from,
        long count,
        CancellationToken cancellationToken
    )
    {
        source.Seek(from, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (count > 0)
            {
                var read = await source
                    .ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
