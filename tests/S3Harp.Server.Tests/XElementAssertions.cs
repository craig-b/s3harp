using System.Xml.Linq;
using Xunit;

namespace S3Harp.Server.Tests;

internal static class XElementAssertions
{
    /// <summary>The child element the response must carry; its absence fails the test.</summary>
    public static XElement Required(this XElement parent, XName name)
    {
        var element = parent.Element(name);
        Assert.NotNull(element);
        return element;
    }
}
