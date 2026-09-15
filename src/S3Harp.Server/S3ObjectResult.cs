using System.Globalization;
using S3Harp.Core;

namespace S3Harp.Server;

/// <summary>
/// Serves an object's headers and, for GET, streams its content. A null content
/// stream produces the HEAD shape: full headers, empty body.
/// </summary>
public sealed class S3ObjectResult(ObjectRecord record, Stream? content) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = record.ContentType ?? "application/octet-stream";
        response.ContentLength = record.Size;
        response.Headers.ETag = $"\"{record.ETag}\"";
        response.Headers.LastModified =
            record.LastModified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        foreach (var (name, value) in record.Metadata)
        {
            response.Headers["x-amz-meta-" + name] = value;
        }

        if (content is not null)
        {
            await using (content.ConfigureAwait(false))
            {
                await content.CopyToAsync(response.Body, httpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
    }
}
