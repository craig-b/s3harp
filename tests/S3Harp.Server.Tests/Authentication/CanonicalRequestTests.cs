using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests.Authentication;

public sealed class CanonicalRequestTests
{
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Build_ReproducesTheDocumentedAwsCanonicalRequest()
    {
        var request = CreateRequest("GET", "/test.txt", new()
        {
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["Range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = EmptyPayloadHash,
            ["x-amz-date"] = "20130524T000000Z",
        });

        var canonical = CanonicalRequest.Build(
            request,
            ["host", "range", "x-amz-content-sha256", "x-amz-date"],
            EmptyPayloadHash);

        Assert.Equal(
            "GET\n" +
            "/test.txt\n" +
            "\n" +
            "host:examplebucket.s3.amazonaws.com\n" +
            "range:bytes=0-9\n" +
            $"x-amz-content-sha256:{EmptyPayloadHash}\n" +
            "x-amz-date:20130524T000000Z\n" +
            "\n" +
            "host;range;x-amz-content-sha256;x-amz-date\n" +
            EmptyPayloadHash,
            canonical);
    }

    [Fact]
    public void Build_SortsQueryParametersOrdinally()
    {
        var request = CreateRequest("GET", "/key?b=2&a=1", new() { ["Host"] = "localhost" });

        var canonical = CanonicalRequest.Build(request, ["host"], EmptyPayloadHash);

        Assert.Equal("a=1&b=2", canonical.Split('\n')[2]);
    }

    [Fact]
    public void Build_CanonicalizesValuelessQueryParametersWithAnEqualsSign()
    {
        var request = CreateRequest("GET", "/bucket?acl", new() { ["Host"] = "localhost" });

        var canonical = CanonicalRequest.Build(request, ["host"], EmptyPayloadHash);

        Assert.Equal("acl=", canonical.Split('\n')[2]);
    }

    [Fact]
    public void Build_TrimsAndCollapsesWhitespaceInHeaderValues()
    {
        var request = CreateRequest("GET", "/", new()
        {
            ["Host"] = "localhost",
            ["x-amz-meta-note"] = "  spaced   out  value  ",
        });

        var canonical = CanonicalRequest.Build(request, ["host", "x-amz-meta-note"], EmptyPayloadHash);

        Assert.Contains("\nx-amz-meta-note:spaced out value\n", canonical, StringComparison.Ordinal);
    }

    private static HttpRequest CreateRequest(
        string method, string rawTarget, Dictionary<string, string> headers)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget = rawTarget;
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return context.Request;
    }
}
