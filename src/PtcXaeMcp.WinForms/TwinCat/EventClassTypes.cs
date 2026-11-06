using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One event of an EventLogger event class: id, IEC name (TC_EVENTS.&lt;class&gt;.&lt;name&gt;), severity and display text.</summary>
internal sealed record EventClassEvent(uint Id, string Name, string Severity, string DisplayText);

/// <summary>One event as a tool call passes it (checked by the coordinator before anything changes).</summary>
public sealed record EventClassEventInput(
    [property: System.ComponentModel.Description("Event id, 0..4294967295, unique in the class (TcEventEntry.nEventId).")] long Id,
    [property: System.ComponentModel.Description("Event name, an IEC identifier unique in the class: the PLC uses TC_EVENTS.<class>.<name>.")] string Name,
    [property: System.ComponentModel.Description("Verbose, Info, Warning, Error or Critical. Default Info.")] string? Severity = null,
    [property: System.ComponentModel.Description("Display text; {0}, {1} … take the arguments of the event. Default: the event name.")] string? Text = null);

/// <summary>
/// An event class as the type system holds it. <c>Persistent</c>: <c>PersistentType="true"</c>, kept in the .tsproj even when
/// nothing references it (how this server creates them). <c>BuiltIn</c>: <c>TcBaseType="true"</c>, shipped with TwinCAT.
/// Neither: it comes from a TMC file of the project (AdditionalTmcFile, a C++ module, a library).
/// </summary>
internal sealed record EventClassInfo(
    string Name, string Guid, string DisplayText, bool Persistent, bool BuiltIn, IReadOnlyList<EventClassEvent> Events);

/// <summary>
/// One PLC project and its pinned global data types ("Add Global Data Type"): the .plcproj path from the PLC node, the
/// children of its <c>ExternalTypes.tmc</c> node (ItemType 651, e.g. <c>Global.ST_MyEvents</c>) and the text of
/// <c>ExternalTypes.tmc</c> next to the .plcproj (null when the file does not exist).
/// </summary>
internal sealed record PlcExternalTypesInfo(
    string PlcName, string PlcprojPath, IReadOnlyList<string> PinnedNodes, string? ExternalTypesXml)
{
    public string ExternalTypesPath => Path.Combine(Path.GetDirectoryName(PlcprojPath) ?? string.Empty, EventClassXml.ExternalTypesFile);
}

/// <summary>A file the session rewrites while the solution is closed: <c>Transform</c> gets the current text (null = no file).</summary>
internal sealed record SolutionFileEdit(string Path, Func<string?, string> Transform);

/// <summary>
/// The XML of event classes (TcModuleClass schema, <c>DataTypeType</c> with <c>EventId</c> children — the shape of
/// TcStandardEvents.tmc) and of the PLC project's <c>ExternalTypes.tmc</c> / .plcproj entry that "Add Global Data Type"
/// writes (learned live 2026-11-06 from the XAE command on 4026.22).
/// </summary>
internal static class EventClassXml
{
    public const string ExternalTypesFile = "ExternalTypes.tmc";

    /// <summary>TcModuleBase.xsd SeverityType.</summary>
    public static readonly IReadOnlyList<string> Severities = ["Verbose", "Info", "Warning", "Error", "Critical"];

    private static readonly XNamespace MsBuild = "http://schemas.microsoft.com/developer/msbuild/2003";

    /// <summary>The canonical severity for <paramref name="severity"/> (case-insensitive), "Info" when empty, null when unknown.</summary>
    public static string? NormalizeSeverity(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity)) return "Info";
        return Severities.FirstOrDefault(s => string.Equals(s, severity.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The DataType element for <c>ITcTypeSystem.CreateType</c>. <c>PersistentType="true"</c> ("Persistent (even if unused)")
    /// is what makes the .tsproj keep a type nothing references — without it CreateType's type is gone after save + reopen.
    /// Without <paramref name="guid"/> the type system generates one.
    /// </summary>
    public static string BuildDataType(string name, string? guid, string displayText, IReadOnlyList<EventClassEvent> events)
    {
        var nameElement = new XElement("Name", name);
        if (!string.IsNullOrWhiteSpace(guid)) nameElement.Add(new XAttribute("GUID", FormatGuid(guid)));
        nameElement.Add(new XAttribute("PersistentType", "true"));

        var type = new XElement("DataType", nameElement, Text("DisplayName", displayText));
        foreach (var e in events)
            type.Add(new XElement("EventId",
                new XElement("Name", new XAttribute("Id", e.Id.ToString(CultureInfo.InvariantCulture)), e.Name),
                Text("DisplayName", e.DisplayText),
                new XElement("Severity", e.Severity)));
        return type.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>"{XXXXXXXX-…}" upper case, the form the XAE writes.</summary>
    public static string FormatGuid(string guid) =>
        Guid.TryParse(guid, out var g) ? "{" + g.ToString().ToUpperInvariant() + "}" : guid;

    private static XElement Text(string element, string text) =>
        new(element, new XAttribute("TxtId", ""), new XCData(text));

    /// <summary>
    /// The event class in <paramref name="xml"/> (a DataType element, or ExportXml's <c>&lt;DataTypes&gt;</c> wrapper); null
    /// when it is no event class (no EventId) or not readable.
    /// </summary>
    public static EventClassInfo? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XElement? type;
        try
        {
            var root = XDocument.Parse(xml).Root;
            type = root?.Name.LocalName == "DataType" ? root : root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "DataType");
        }
        catch (XmlException) { return null; }
        return type is null ? null : ParseDataType(type);
    }

    private static EventClassInfo? ParseDataType(XElement type)
    {
        var eventIds = type.Elements().Where(e => e.Name.LocalName == "EventId").ToList();
        if (eventIds.Count == 0) return null;
        var name = type.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
        if (name is null) return null;

        var events = new List<EventClassEvent>();
        foreach (var e in eventIds)
        {
            var en = e.Elements().FirstOrDefault(x => x.Name.LocalName == "Name");
            events.Add(new EventClassEvent(
                ParseId((string?)en?.Attribute("Id")),
                en?.Value.Trim() ?? string.Empty,
                e.Elements().FirstOrDefault(x => x.Name.LocalName == "Severity")?.Value.Trim() ?? string.Empty,
                e.Elements().FirstOrDefault(x => x.Name.LocalName == "DisplayName")?.Value ?? string.Empty));
        }
        return new EventClassInfo(
            name.Value.Trim(),
            FormatGuid((string?)name.Attribute("GUID") ?? string.Empty),
            type.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value.Trim() ?? string.Empty,
            IsTrue(name.Attribute("PersistentType")),
            IsTrue(name.Attribute("TcBaseType")),
            events);
    }

    private static bool IsTrue(XAttribute? a) => string.Equals((string?)a, "true", StringComparison.OrdinalIgnoreCase);

    // HexDecValue: decimal or "#x" hex.
    private static uint ParseId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        string s = id.Trim();
        if (s.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h) ? h : 0;
        return uint.TryParse(s.TrimStart('+'), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint d) ? d : 0;
    }

    /// <summary>The DataType element of ExportXml's <c>&lt;DataTypes&gt;</c> wrapper (or of a bare DataType) as text; null when there is none.</summary>
    public static string? DataTypeElement(string? xml)
    {
        var doc = TryParse(xml);
        if (doc?.Root is null) return null;
        var type = doc.Root.Name.LocalName == "DataType" ? doc.Root : doc.Root.Descendants().FirstOrDefault(e => e.Name.LocalName == "DataType");
        return type?.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// <paramref name="existing"/> (an event class DataType) with a new display text and/or a new event list; GUID, name and
    /// every element this server does not manage stay. An event whose id already existed keeps its other children
    /// (DescriptionText, CauseRemedy, Properties …), only Name, DisplayName and Severity are set. <c>PersistentType="true"</c>
    /// is set so the .tsproj keeps it.
    /// </summary>
    public static string ApplyUpdate(string existing, string? displayText, IReadOnlyList<EventClassEvent>? events)
    {
        var type = XElement.Parse(DataTypeElement(existing) ?? throw new InvalidOperationException("No DataType in the existing definition."));
        var name = type.Elements().First(e => e.Name.LocalName == "Name");
        name.SetAttributeValue("PersistentType", "true");

        if (displayText is not null)
        {
            var display = type.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName");
            var replacement = Text("DisplayName", displayText);
            if (display is not null) display.ReplaceWith(replacement);
            else name.AddAfterSelf(replacement);
        }

        if (events is not null)
        {
            var old = type.Elements().Where(e => e.Name.LocalName == "EventId").ToList();
            var byId = new Dictionary<uint, XElement>();
            foreach (var o in old)
            {
                uint id = ParseId((string?)o.Elements().FirstOrDefault(x => x.Name.LocalName == "Name")?.Attribute("Id"));
                byId.TryAdd(id, o);
            }
            XNode anchor = (XNode?)old.FirstOrDefault()?.PreviousNode
                           ?? (XNode?)type.Elements().LastOrDefault(e => e.Name.LocalName is "Name" or "DisplayName")
                           ?? name;
            foreach (var o in old) o.Remove();

            var added = new List<XElement>();
            foreach (var e in events)
            {
                var element = byId.TryGetValue(e.Id, out var kept) ? new XElement(kept) : new XElement("EventId");
                element.Elements().Where(x => x.Name.LocalName is "Name" or "DisplayName" or "Severity").Remove();
                element.AddFirst(
                    new XElement("Name", new XAttribute("Id", e.Id.ToString(CultureInfo.InvariantCulture)), e.Name),
                    Text("DisplayName", e.DisplayText));
                // Severity follows DescriptionText/DescriptionUrl in the schema; before CauseRemedy/Comment/Properties/Xml.
                var before = element.Elements().FirstOrDefault(x => x.Name.LocalName is "CauseRemedy" or "Comment" or "Properties" or "Xml");
                var severity = new XElement("Severity", e.Severity);
                if (before is not null) before.AddBeforeSelf(severity); else element.Add(severity);
                added.Add(element);
            }
            if (anchor.Parent == type) anchor.AddAfterSelf(added);
            else type.Add(added);
        }
        return type.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The event classes (name, GUID) in a TMC document such as ExternalTypes.tmc; empty when none or not readable.</summary>
    public static IReadOnlyList<EventClassInfo> EventClassesInTmc(string? tmc)
    {
        var doc = TryParse(tmc);
        if (doc?.Root is null) return [];
        return doc.Root.Descendants().Where(e => e.Name.LocalName == "DataType")
            .Select(ParseDataType).OfType<EventClassInfo>().ToList();
    }

    /// <summary>
    /// <paramref name="tmc"/> (null, empty or only an XML declaration — the XAE leaves that behind when it drops the last
    /// pin — count as no document) with <paramref name="dataType"/> in <c>TcModuleClass/DataTypes</c>: a DataType of the
    /// same GUID or name is replaced, otherwise it is appended. Every other DataType stays as it is.
    /// </summary>
    public static string UpsertIntoTmc(string? tmc, string dataType)
    {
        var added = XElement.Parse(dataType);
        string addedName = added.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value.Trim() ?? string.Empty;
        string addedGuid = NormalizeGuid((string?)added.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Attribute("GUID"));

        var doc = TryParse(tmc);
        if (doc?.Root is null || doc.Root.Name.LocalName != "TcModuleClass")
            doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement("TcModuleClass",
                    new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                    new XAttribute(XName.Get("noNamespaceSchemaLocation", "http://www.w3.org/2001/XMLSchema-instance"),
                        "http://www.beckhoff.com/schemas/2009/05/TcModuleClass"),
                    new XAttribute("GeneratedBy", "TwinCAT XAE Plc")));

        var root = doc.Root!;
        var types = root.Elements().FirstOrDefault(e => e.Name.LocalName == "DataTypes");
        if (types is null) { types = new XElement(root.Name.Namespace + "DataTypes"); root.Add(types); }

        var same = types.Elements().Where(e => e.Name.LocalName == "DataType").FirstOrDefault(t =>
        {
            var n = t.Elements().FirstOrDefault(x => x.Name.LocalName == "Name");
            if (n is null) return false;
            string guid = NormalizeGuid((string?)n.Attribute("GUID"));
            return (addedGuid.Length > 0 && guid == addedGuid)
                   || string.Equals(n.Value.Trim(), addedName, StringComparison.OrdinalIgnoreCase);
        });
        if (same is not null) same.ReplaceWith(added);
        else types.Add(added);

        doc.Declaration ??= new XDeclaration("1.0", "utf-8", null);
        return doc.Declaration + Environment.NewLine + doc.Root!.ToString();
    }

    private static string NormalizeGuid(string? guid) =>
        Guid.TryParse(guid, out var g) ? g.ToString("D") : string.Empty;

    /// <summary>
    /// <paramref name="plcproj"/> with <c>&lt;None Include="ExternalTypes.tmc"&gt;&lt;SubType&gt;Content&lt;/SubType&gt;&lt;/None&gt;</c>
    /// in its own ItemGroup before ProjectExtensions — the entry the XAE adds for "Add Global Data Type". Unchanged (same
    /// text) when an item already includes ExternalTypes.tmc.
    /// </summary>
    public static string EnsureExternalTypesItem(string plcproj)
    {
        var doc = XDocument.Parse(plcproj, LoadOptions.PreserveWhitespace);
        var project = doc.Root ?? throw new InvalidOperationException("The .plcproj has no root element.");
        bool present = project.Descendants().Any(e =>
            string.Equals((string?)e.Attribute("Include"), ExternalTypesFile, StringComparison.OrdinalIgnoreCase));
        if (present) return plcproj;

        XNamespace ns = project.Name.Namespace == XNamespace.None ? MsBuild : project.Name.Namespace;
        var group = new XElement(ns + "ItemGroup",
            new XText("\n    "),
            new XElement(ns + "None", new XAttribute("Include", ExternalTypesFile),
                new XText("\n      "),
                new XElement(ns + "SubType", "Content"),
                new XText("\n    ")),
            new XText("\n  "));

        var extensions = project.Elements().FirstOrDefault(e => e.Name.LocalName == "ProjectExtensions");
        if (extensions is not null) { extensions.AddBeforeSelf(group); extensions.AddBeforeSelf(new XText("\n  ")); }
        else { project.Add(new XText("  ")); project.Add(group); project.Add(new XText("\n")); }

        // The new nodes use LF; give the whole file its own line ends.
        string newline = plcproj.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string text = (doc.Declaration is null ? string.Empty : doc.Declaration + "\n") + project.ToString(SaveOptions.DisableFormatting);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return newline == "\n" ? text : text.Replace("\n", newline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a pin node name (<c>Global.ST_MyEvents</c>, or <c>Global.MyEvents</c> while the PLC has not mapped it yet)
    /// stands for the event class <paramref name="className"/>.
    /// </summary>
    public static bool PinNodeIsClass(string node, string className)
    {
        string leaf = node.Contains('.') ? node[(node.LastIndexOf('.') + 1)..] : node;
        return string.Equals(leaf, className, StringComparison.OrdinalIgnoreCase)
               || string.Equals(leaf, "ST_" + className, StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument? TryParse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return XDocument.Parse(xml); }
        catch (XmlException) { return null; }
    }
}
