using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>What ITcPlcLibraryManager2.ProduceAllLibrarySignatures returned, the tree node that answered and the XAE host.</summary>
internal sealed record LibrarySignatureExport(string Xml, string Node, string Host);

/// <summary>One entry of a signature section, e.g. an input "IN : BOOL".</summary>
internal sealed record SignatureItem(string Name, string DataType, string? Comment);

/// <summary>A section of a type signature (Inputs, Outputs, Constants, …) with its entries.</summary>
internal sealed record SignatureSection(string Name, IReadOnlyList<SignatureItem> Items);

/// <summary>One public type of a library; <see cref="Type"/> is the XML type attribute (FunctionBlock, Function, VarGlobal, Type, …).</summary>
internal sealed record TypeSignatureInfo(string Name, string Type, string? Comment, IReadOnlyList<SignatureSection> Sections);

internal sealed record LibrarySignatureSet(string Name, string Version, string Distributor, IReadOnlyList<TypeSignatureInfo> Types, XElement Element);

// The signature XML (Infosys TE1000 "Exporting PLC library signatures"): one Library element per resolved library with
// LibraryName, Version, Distributor and TypeSignatures/TypeSignature[@type] carrying Name, Comment and sections such as
// Inputs/Input, Outputs/Output, Constants/Constant (Name, DataType, Comment). A section the sample does not show is read
// the same way, so nothing the XAE adds is dropped. Several Library roots in one string are accepted.
internal static class LibrarySignatureXml
{
    private static readonly Regex XmlDeclaration = new(@"<\?xml[^>]*\?>", RegexOptions.CultureInvariant);

    internal static IReadOnlyList<LibrarySignatureSet> Parse(string xml)
    {
        XElement root;
        try { root = XElement.Parse("<SignatureRoot>" + XmlDeclaration.Replace(xml, "") + "</SignatureRoot>"); }
        catch (XmlException ex) { throw new FormatException($"The library signature XML cannot be read: {ex.Message}", ex); }

        return root.Descendants().Where(e => e.Name.LocalName == "Library")
            .Select(lib => new LibrarySignatureSet(
                Text(lib, "LibraryName"), Text(lib, "Version"), Text(lib, "Distributor"),
                lib.Descendants().Where(e => e.Name.LocalName == "TypeSignature").Select(ReadType).ToList(),
                lib))
            .ToList();
    }

    private static TypeSignatureInfo ReadType(XElement t)
    {
        var sections = t.Elements()
            .Where(e => e.Name.LocalName is not ("Name" or "Comment") && e.HasElements)
            .Select(s => new SignatureSection(s.Name.LocalName,
                s.Elements().Select(i => new SignatureItem(Text(i, "Name"), Text(i, "DataType"), Optional(i, "Comment"))).ToList()))
            .ToList();
        return new TypeSignatureInfo(Text(t, "Name"), (string?)t.Attribute("type") ?? "", Optional(t, "Comment"), sections);
    }

    private static string Text(XElement e, string child) => Optional(e, child) ?? "";

    private static string? Optional(XElement e, string child)
    {
        string? value = e.Elements().FirstOrDefault(c => c.Name.LocalName == child)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
