using System.Xml.Linq;

namespace S3Harp.Server;

/// <summary>Builds the elements of an S3 response body, all in S3's document namespace.</summary>
internal static class S3Xml
{
    public static readonly XNamespace Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>An element in the S3 namespace with the given content, as <see cref="XElement"/> takes it.</summary>
    public static XElement Element(string name, params object?[] content) =>
        new(Namespace + name, content);
}
