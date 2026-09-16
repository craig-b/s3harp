using Microsoft.AspNetCore.Http;
using S3Harp.Core;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class ResponseHeaderOverridesTests
{
    private static readonly ObjectRecord Stored = new(
        "key", "blob", Size: 3, ETag: "etag", Parts: [], Checksum: null, ContentType: "text/plain",
        new ContentHeaders(CacheControl: "max-age=60", ContentLanguage: "en"),
        new Dictionary<string, string>(), DateTimeOffset.UnixEpoch);

    [Fact]
    public void ReplacesEachHeaderNamedInTheQuery()
    {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["response-content-type"] = "foo/bar",
            ["response-cache-control"] = "no-cache",
            ["response-content-disposition"] = "attachment",
            ["response-content-encoding"] = "aaa",
            ["response-content-language"] = "esperanto",
            ["response-expires"] = "123",
        });

        var served = ResponseHeaderOverrides.Apply(query, Stored);

        Assert.Equal("foo/bar", served.ContentType);
        Assert.Equal(
            new ContentHeaders("no-cache", "attachment", "aaa", "esperanto", "123"),
            served.ContentHeaders);
    }

    [Fact]
    public void KeepsTheStoredHeadersTheQueryDoesNotName()
    {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["response-content-language"] = "esperanto",
        });

        var served = ResponseHeaderOverrides.Apply(query, Stored);

        Assert.Equal("text/plain", served.ContentType);
        Assert.Equal(
            new ContentHeaders(CacheControl: "max-age=60", ContentLanguage: "esperanto"),
            served.ContentHeaders);
    }

    [Fact]
    public void LeavesTheRecordAloneWithoutOverrides()
    {
        Assert.Same(Stored, ResponseHeaderOverrides.Apply(new QueryCollection(), Stored));
    }
}
