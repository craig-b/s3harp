using S3Harp.Core;

namespace S3Harp.Server;

/// <summary>
/// The <c>response-*</c> query parameters of a GET or HEAD, which name the content
/// headers to serve the object with for that response alone: the stored headers
/// stay as they are.
/// </summary>
public static class ResponseHeaderOverrides
{
    private static readonly string[] ParameterNames =
    [
        "response-content-type", "response-cache-control", "response-content-disposition",
        "response-content-encoding", "response-content-language", "response-expires",
    ];

    /// <summary>The record as the response should describe it.</summary>
    public static ObjectRecord Apply(IQueryCollection query, ObjectRecord record)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(record);
        if (!ParameterNames.Any(query.ContainsKey))
        {
            return record;
        }

        var stored = record.ContentHeaders;
        return record with
        {
            ContentType = Override(query, "response-content-type", record.ContentType),
            ContentHeaders = new ContentHeaders(
                Override(query, "response-cache-control", stored.CacheControl),
                Override(query, "response-content-disposition", stored.ContentDisposition),
                Override(query, "response-content-encoding", stored.ContentEncoding),
                Override(query, "response-content-language", stored.ContentLanguage),
                Override(query, "response-expires", stored.Expires)),
        };
    }

    private static string? Override(IQueryCollection query, string name, string? stored) =>
        query.TryGetValue(name, out var value) ? value.ToString() : stored;
}
