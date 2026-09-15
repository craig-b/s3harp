using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace S3Harp.Server;

/// <summary>Writes an XML document as an S3 response body.</summary>
public sealed class S3XmlResult(int statusCode, XDocument document) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = statusCode;
        await WriteAsync(httpContext, document).ConfigureAwait(false);
    }

    internal static async Task WriteAsync(HttpContext httpContext, XDocument document)
    {
        var response = httpContext.Response;
        response.ContentType = "application/xml";
        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        var writer = XmlWriter.Create(response.Body, settings);
        await using (writer.ConfigureAwait(false))
        {
            await document.SaveAsync(writer, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
