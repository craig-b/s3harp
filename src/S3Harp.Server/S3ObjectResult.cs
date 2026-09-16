using System.Buffers;
using S3Harp.Core;

namespace S3Harp.Server;

/// <summary>
/// Serves an object's headers and, for GET, streams its content — the whole object,
/// or a 206 slice when a range is given. A null content stream produces the HEAD
/// shape: full headers, empty body.
/// </summary>
public sealed class S3ObjectResult(
    ObjectRecord record, Stream? content, RangeEvaluation? range = null) : IResult
{
    private const int BufferSize = 64 * 1024;

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
                    content, response.Body, slice.From, slice.To - slice.From + 1,
                    httpContext.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await content.CopyToAsync(response.Body, httpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task CopySliceAsync(
        Stream source, Stream destination, long from, long count,
        CancellationToken cancellationToken)
    {
        source.Seek(from, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (count > 0)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
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
