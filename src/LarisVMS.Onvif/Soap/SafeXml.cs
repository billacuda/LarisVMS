using System.Xml;
using System.Xml.Linq;

namespace LarisVMS.Onvif.Soap;

/// <summary>Parses XML from devices on the network (SOAP responses, WS-Discovery replies): no DTDs, no
/// external resolution, and a size cap, so a hostile device can't send an entity bomb or a huge
/// document.</summary>
public static class SafeXml
{
    /// <summary>Largest XML document accepted from a device, in characters.</summary>
    public const long MaxCharacters = 16L * 1024 * 1024;

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxCharacters,
        MaxCharactersFromEntities = 0,
    };

    public static XDocument Parse(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, Settings);
        return XDocument.Load(reader);
    }
}
