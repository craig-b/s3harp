using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace S3Harp.Server;

/// <summary>Writes an XML document as an S3 response body.</summary>
internal sealed class S3XmlResult(int statusCode, XDocument document) : IResult
{
    /// <summary>Wraps the root element in a UTF-8 document.</summary>
    public S3XmlResult(int statusCode, XElement root)
        : this(statusCode, new XDocument(new XDeclaration("1.0", "UTF-8", standalone: null), root))
    { }

    /// <exception cref="IOException">The response could not be written.</exception>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = statusCode;
        await WriteAsync(httpContext, document).ConfigureAwait(false);
    }

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Async = true,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    internal static async Task WriteAsync(HttpContext httpContext, XDocument document)
    {
        var response = httpContext.Response;
        response.ContentType = "application/xml";
        var writer = XmlWriter.Create(response.Body, WriterSettings);
        await using (writer.ConfigureAwait(false))
        {
            await document.SaveAsync(writer, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
