using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class S3ErrorResultTests
{
    [Fact]
    public async Task ExecuteAsync_WritesStatusCodeAndContentType()
    {
        var context = CreateContext();

        await new S3ErrorResult(S3Errors.NotImplemented).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        Assert.Equal("application/xml", context.Response.ContentType);
    }

    [Fact]
    public async Task ExecuteAsync_WritesErrorDocumentDescribingTheRequest()
    {
        var context = CreateContext(path: "/my-bucket/my-key", traceIdentifier: "req-123");

        await new S3ErrorResult(S3Errors.NotImplemented).ExecuteAsync(context);

        var error = ReadBody(context).Root;
        Assert.NotNull(error);
        Assert.Equal("Error", error.Name.LocalName);
        Assert.Equal("NotImplemented", error.Element("Code")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(error.Element("Message")?.Value));
        Assert.Equal("/my-bucket/my-key", error.Element("Resource")?.Value);
        Assert.Equal("req-123", error.Element("RequestId")?.Value);
    }

    [Fact]
    public async Task ExecuteAsync_EchoesTheRequestIdAsAHeader()
    {
        var context = CreateContext(traceIdentifier: "req-456");

        await new S3ErrorResult(S3Errors.NotImplemented).ExecuteAsync(context);

        Assert.Equal("req-456", context.Response.Headers["x-amz-request-id"]);
    }

    private static DefaultHttpContext CreateContext(
        string path = "/",
        string traceIdentifier = "test-request-id"
    )
    {
        var context = new DefaultHttpContext { TraceIdentifier = traceIdentifier };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static XDocument ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return XDocument.Load(context.Response.Body);
    }
}
