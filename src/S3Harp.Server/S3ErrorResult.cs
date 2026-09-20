using System.Xml.Linq;

namespace S3Harp.Server;

/// <summary>Writes an <see cref="S3Error"/> as an S3-style XML error response.</summary>
internal sealed class S3ErrorResult(S3Error error) : IResult
{
    /// <exception cref="IOException">The response could not be written.</exception>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var requestId = httpContext.TraceIdentifier;
        var response = httpContext.Response;
        response.StatusCode = error.StatusCode;
        response.Headers["x-amz-request-id"] = requestId;

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", standalone: null),
            new XElement(
                "Error",
                new XElement("Code", error.Code),
                new XElement("Message", error.Message),
                new XElement("Resource", httpContext.Request.Path.Value),
                new XElement("RequestId", requestId)
            )
        );
        await S3XmlResult.WriteAsync(httpContext, document).ConfigureAwait(false);
    }
}
