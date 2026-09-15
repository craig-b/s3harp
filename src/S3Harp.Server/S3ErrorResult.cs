using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace S3Harp.Server;

/// <summary>Writes an <see cref="S3Error"/> as an S3-style XML error response.</summary>
public sealed class S3ErrorResult(S3Error error) : IResult
{
    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Async = true,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var requestId = httpContext.TraceIdentifier;
        var response = httpContext.Response;
        response.StatusCode = error.StatusCode;
        response.ContentType = "application/xml";
        response.Headers["x-amz-request-id"] = requestId;

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", standalone: null),
            new XElement("Error",
                new XElement("Code", error.Code),
                new XElement("Message", error.Message),
                new XElement("Resource", httpContext.Request.Path.Value),
                new XElement("RequestId", requestId)));

        var writer = XmlWriter.Create(response.Body, WriterSettings);
        await using (writer.ConfigureAwait(false))
        {
            await document.SaveAsync(writer, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
