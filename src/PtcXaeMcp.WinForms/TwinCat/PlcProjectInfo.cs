using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One field of a PLC project's Project Information, as ProduceXml returns it (Value is "" when not set).</summary>
internal sealed record PlcProjectInfoField(string Name, string Value);

// The Project Information of a PLC project (Title, Version, Company, …) is the <ProjectInfo> section of the PLC project
// item's XML: ProduceXml returns <TreeItem><IECProjectDef><ProjectInfo><Company/><Title/><Version/>…; ConsumeXml with only
// some of those elements changes exactly those and leaves the others as they are; an empty element clears the field
// (live, TwinCAT 3.1.4026.22). Infosys TE1000 documents the section only for DocFormat.
internal static class PlcProjectInfoXml
{
    /// <summary>The fields the tool can set, in the order they are reported.</summary>
    internal static readonly string[] Settable = ["Title", "Version", "Company", "Author", "DefaultNamespace", "Description", "Released"];

    internal static IReadOnlyList<PlcProjectInfoField> Parse(string produceXml)
    {
        XElement root;
        try { root = XElement.Parse(produceXml); }
        catch (XmlException ex) { throw new FormatException($"The PLC project XML cannot be read: {ex.Message}", ex); }
        var info = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProjectInfo")
            ?? throw new FormatException("The PLC project XML has no <ProjectInfo> section (this XAE does not expose the Project Information).");
        return info.Elements().Select(e => new PlcProjectInfoField(e.Name.LocalName, e.Value.Trim())).ToList();
    }

    /// <summary>The ConsumeXml document that sets exactly <paramref name="values"/> ("" clears a field).</summary>
    internal static string Build(IEnumerable<KeyValuePair<string, string>> values)
    {
        var info = new XElement("ProjectInfo", values.Select(v => new XElement(v.Key, v.Value)));
        return new XElement("TreeItem", new XElement("IECProjectDef", info)).ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>A library version: four numbers 0..65535 separated by dots, as the XAE's Project Information takes it.</summary>
    internal static bool IsVersion(string version) =>
        version.Split('.') is { Length: 4 } parts
        && parts.All(p => p.Length > 0 && ushort.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _));

    internal static string Format(string plc, IReadOnlyList<PlcProjectInfoField> fields)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Project Information of {plc}:");
        foreach (var f in fields.OrderBy(f => Array.IndexOf(Settable, f.Name) is var i && i < 0 ? int.MaxValue : i))
            sb.Append(CultureInfo.InvariantCulture, $"\n  {f.Name}: {(f.Value.Length == 0 ? "(not set)" : f.Value)}");
        return sb.ToString();
    }
}

// twincat_plc_project_info: reads the Project Information of a PLC project and sets the fields that are given. A library
// needs at least the Title (twincat_save_as_library fails with "'Title' not specified" otherwise) and usually a Version.
internal sealed class PlcProjectInfoCoordinator(ITwinCatSession session)
{
    public string Run(string? plcProjectName, string? title = null, string? version = null, string? company = null,
        string? author = null, string? defaultNamespace = null, string? description = null, bool? released = null)
    {
        var wanted = new List<KeyValuePair<string, string>>();
        void Add(string name, string? value) { if (value is not null) wanted.Add(new(name, value.Trim())); }
        Add("Title", title);
        Add("Version", version);
        Add("Company", company);
        Add("Author", author);
        Add("DefaultNamespace", defaultNamespace);
        Add("Description", description);
        if (released is bool r) wanted.Add(new("Released", r ? "true" : "false"));

        var versionValue = wanted.FirstOrDefault(w => w.Key == "Version").Value;
        if (!string.IsNullOrEmpty(versionValue) && !PlcProjectInfoXml.IsVersion(versionValue))
            return $"[Error] version '{versionValue}' is not a library version: four numbers 0–65535 separated by dots, e.g. 1.0.0.0. Nothing was changed.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;
        string plc = string.IsNullOrWhiteSpace(plcProjectName) ? "the PLC project" : $"PLC project '{plcProjectName.Trim()}'";

        IReadOnlyList<PlcProjectInfoField> before;
        try { before = PlcProjectInfoXml.Parse(session.ReadPlcProjectXml(plcProjectName)); }
        catch (FormatException ex) { return $"[Error] {ex.Message}"; }
        catch (Exception ex) when (ComCall.IsBusy(ex)) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        catch (Exception ex) { return $"[Error] The Project Information of {plc} could not be read: {ex.Message}"; }

        if (wanted.Count == 0) return PlcProjectInfoXml.Format(plc, before);

        try { session.ConsumePlcProjectXml(plcProjectName, PlcProjectInfoXml.Build(wanted)); }
        catch (Exception ex) when (ComCall.IsBusy(ex)) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        catch (Exception ex) { return $"[Error] The XAE refused the Project Information: {ex.Message} Nothing was saved."; }

        IReadOnlyList<PlcProjectInfoField> after;
        try { after = PlcProjectInfoXml.Parse(session.ReadPlcProjectXml(plcProjectName)); }
        catch (Exception ex) when (!ComCall.IsBusy(ex)) { return $"[Error] The Project Information was written but could not be read back: {ex.Message}"; }

        var mismatches = wanted
            .Select(w => (w.Key, Wanted: w.Value, Got: after.FirstOrDefault(a => a.Name == w.Key)?.Value))
            .Where(m => !string.Equals(m.Wanted, m.Got, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mismatches.Count > 0)
            return "[Error] The read-back differs from what was set: " +
                   string.Join("; ", mismatches.Select(m => $"{m.Key} is '{m.Got ?? "(missing)"}', not '{m.Wanted}'")) +
                   ". Nothing was saved.";

        try { session.SaveAll(); }
        catch (Exception ex) when (!ComCall.IsBusy(ex)) { return $"[Error] The Project Information was set but the project could not be saved: {ex.Message}"; }

        return $"Set {string.Join(", ", wanted.Select(w => w.Key))} (verified by read-back, project saved).\n" +
               PlcProjectInfoXml.Format(plc, after);
    }
}
