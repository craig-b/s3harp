using System.Xml.Linq;

namespace S3Harp.Server;

/// <summary>Reads the elements of a request body by local name, whichever namespace the client wrote them in.</summary>
internal static class RequestXml
{
    /// <summary>The first child element with the local name, or null when the parent has none.</summary>
    public static XElement? Child(this XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    /// <summary>Every child element with the local name, in document order.</summary>
    public static IEnumerable<XElement> Children(this XElement parent, string localName) =>
        parent.Elements().Where(element => element.Name.LocalName == localName);
}
