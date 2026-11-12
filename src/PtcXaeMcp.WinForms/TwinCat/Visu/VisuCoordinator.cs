using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat.Visu;

/// <summary>
/// The twincat_visu_* tools. Reading parses the visualization's .TcVIS file (its content is not reachable through
/// the Automation Interface). Writing builds the new file, checks everything before the automatic logout, then
/// replaces the object the way XAE takes a file over deterministically: close its editor, delete the object, import
/// the file under the same name (<see cref="ITwinCatSession.ImportPlcObjectFile"/>; the object id in the file is kept),
/// and read the project file back. The file as it was is kept under %LOCALAPPDATA%\ptc-xae-mcp\visu and imported again
/// when XAE refuses the new one.
/// </summary>
internal sealed partial class VisuCoordinator(ITwinCatSession session)
{
    internal const string VisualizationSubtype = "VISUALIZATION";

    /// <summary>Where backups and the files handed to XAE go; tests point it elsewhere.</summary>
    internal string WorkRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ptc-xae-mcp", "visu");

    // ---- list element types ---------------------------------------------------------

    public static string ListElementTypes() => VisuJson.Catalog();

    // ---- read -----------------------------------------------------------------------

    public string Read(string path, string kind)
    {
        var (error, file) = ResolveExisting(path, kind);
        if (error is not null) return error;
        try
        {
            string text = ReadText(file!);
            var doc = VisuDocument.Parse(text);
            return VisuJson.Page(doc, path.Trim(), file!, VisuJson.Revision(text));
        }
        catch (VisuFormatException ex) { return $"[Error] '{path}': {ex.Message} ({file})"; }
        catch (IOException ex) { return $"[Error] Cannot read '{file}': {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { return $"[Error] Cannot read '{file}': {ex.Message}"; }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private (string? Error, string? File) ResolveExisting(string path, string kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("[Error] path is required.", null);
        path = path.Trim();
        var kindError = CheckKindArgument(kind);
        if (kindError is not null) return (kindError, null);
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, null);
        PlcNodeInfo node;
        try { node = session.ProbePlcNode(path); }
        catch (Exception ex) { return (Describe(ex), null); }
        var mismatch = PlcKindResolver.Check(path, PlcKind.Visu, node, VisualizationSubtype);
        if (mismatch is not null) return (mismatch, null);
        try { return (null, FilePathOf(path)); }
        catch (Exception ex) { return (Describe(ex), null); }
    }

    /// <summary>kind is mandatory like on every PLC write; the visu tools handle kind VISU only.</summary>
    internal static string? CheckKindArgument(string? kind)
    {
        if (!PlcKindResolver.TryParseKind(kind, out string k, out var err)) return err;
        return k == PlcKind.Visu ? null
            : $"[Error] The visu tools work on kind VISU (a visualization), not {k}. Use twincat_plc_read/twincat_plc_write for {k}.";
    }

    /// <summary>The .TcVIS of the node: the FullPath XAE reports in the item's properties.</summary>
    private string FilePathOf(string path)
    {
        string xml = session.ProduceTreeItemXml(path);
        string? file = VisuFiles.FullPathFromTreeItemXml(xml);
        if (string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException($"XAE does not report a file for '{path}'.");
        if (!File.Exists(file))
            throw new InvalidOperationException($"The file of '{path}' does not exist: {file}");
        return file;
    }

    private static string ReadText(string file) => Encoding.UTF8.GetString(File.ReadAllBytes(file));

    private static bool TryParseJson(string? json, string what, out JsonElement value, out string? error)
    {
        value = default;
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = $"[Error] {what} is required."; return false; }
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            value = parsed.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"[Error] {what} is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static string Describe(Exception ex) =>
        McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message);

    private static string Message(Exception ex) => ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message;

    /// <summary>For tests: the element ids of a file.</summary>
    internal static IReadOnlyList<int> ElementIds(string fileText) =>
        VisuDocument.Parse(fileText).Elements.Select(e => VisuEditor.ElementId(e) ?? -1).ToList();

}

/// <summary>Where XAE keeps a PLC object's file. Pure.</summary>
internal static class VisuFiles
{
    /// <summary>The FullPath property of ProduceXml (<c>&lt;VSProperty&gt;&lt;Name&gt;FullPath&lt;/Name&gt;&lt;Value&gt;…</c>); null when absent.</summary>
    public static string? FullPathFromTreeItemXml(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var prop = doc.Descendants("VSProperty").FirstOrDefault(p => (string?)p.Element("Name") == "FullPath");
            string? value = (string?)prop?.Element("Value");
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (System.Xml.XmlException) { return null; }
    }
}
