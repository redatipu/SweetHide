using System.Xml;

namespace SweetHide.Forensics.Metadata;

/// <summary>
/// Reports an XMP packet as a whole and pulls out its property values and comments. XML parsing is locked down:
/// no DTDs, no external resolution, bounded size. Malformed XML just yields the raw packet.
/// </summary>
internal static class XmpReader
{
    private const int MaxCharacters = 4 * 1024 * 1024;

    public static void Read(string packet, long offset, int length, string source, FieldCollector collector)
    {
        collector.AddText(source, "XMP packet", packet, offset, length, isXmp: true);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 1024,
            CheckCharacters = false,
            IgnoreWhitespace = true,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(packet), settings);
            var path = new Stack<string>();
            while (reader.Read() && !collector.IsFull)
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var element = reader.Name;
                        if (reader.HasAttributes)
                        {
                            ReadAttributes(reader, offset, length, collector);
                        }

                        if (!reader.IsEmptyElement)
                        {
                            path.Push(element);
                        }

                        break;
                    case XmlNodeType.EndElement:
                        if (path.Count > 0)
                        {
                            path.Pop();
                        }

                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        var value = reader.Value.Trim();
                        if (value.Length > 0)
                        {
                            collector.AddText("XMP", PropertyName(path), value, offset, length);
                        }

                        break;
                    case XmlNodeType.Comment:
                        // Nobody reads XML comments in image metadata, which is exactly why they get used.
                        collector.AddText("XMP", "comment", reader.Value.Trim(), offset, length, "XML comment inside the XMP packet", unusual: true);
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            collector.AddText(source, "XMP parse error", ex.Message, offset, length, "the packet is not well-formed XML", unusual: true);
        }
    }

    private static void ReadAttributes(XmlReader reader, long offset, int length, FieldCollector collector)
    {
        while (reader.MoveToNextAttribute())
        {
            var name = reader.Name;
            if (name.StartsWith("xmlns", StringComparison.Ordinal) || name.StartsWith("rdf:", StringComparison.Ordinal)
                || name.StartsWith("xml:", StringComparison.Ordinal) || name.StartsWith("x:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = reader.Value.Trim();
            if (value.Length > 0)
            {
                collector.AddText("XMP", name, value, offset, length);
            }
        }

        reader.MoveToElement();
    }

    /// <summary>The nearest enclosing element that is a property rather than RDF plumbing (rdf:li, rdf:Alt, ...).</summary>
    private static string PropertyName(Stack<string> path)
    {
        foreach (var element in path)
        {
            if (!element.StartsWith("rdf:", StringComparison.Ordinal) && !element.StartsWith("x:", StringComparison.Ordinal))
            {
                return element;
            }
        }

        return "value";
    }
}
