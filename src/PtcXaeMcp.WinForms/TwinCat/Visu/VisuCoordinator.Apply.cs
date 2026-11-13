using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat.Visu;

// twincat_visu_apply and the write path every visu write goes through.
internal sealed partial class VisuCoordinator
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    // ---- apply (whole page) -----------------------------------------------------------

    public string Apply(string path, string kind, string pageJson, string? expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(path)) return "[Error] path is required.";
        path = path.Trim();
        var kindError = CheckKindArgument(kind);
        if (kindError is not null) return kindError;
        if (!TryParseJson(pageJson, "page", out var page, out var jsonError)) return jsonError!;
        try { VisuEditor.Precheck(page, isPage: true); }
        catch (VisuRequestException ex) { return "[Error] " + ex.Message + " Nothing was changed."; }

        int caret = path.LastIndexOf('^');
        if (caret <= 0) return $"[Error] '{path}' is not a tree path below a folder (parent^name).";
        string parentPath = path[..caret];
        string name = path[(caret + 1)..];
        var nameError = IecIdentifier.Validate(name, "visualization name");
        if (nameError is not null) return nameError;

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        PlcNodeInfo node;
        try { node = session.ProbePlcNode(path); }
        catch (Exception ex) { return Describe(ex); }

        if (node.Exists)
        {
            var mismatch = PlcKindResolver.Check(path, PlcKind.Visu, node, VisualizationSubtype);
            if (mismatch is not null) return mismatch;
            return Write(path, parentPath, name, expectedRevision, doc => VisuEditor.ApplyPage(doc, page, ResolverFor(path)), created: false);
        }

        // A new visualization: parent and manager are checked before the logout.
        if (!string.IsNullOrWhiteSpace(expectedRevision))
            return $"[Error] '{path}' does not exist, so expectedRevision cannot match. Omit it to create the visualization.";
        PlcNodeInfo parent;
        try { parent = session.ProbePlcNode(parentPath); }
        catch (Exception ex) { return Describe(ex); }
        if (!parent.Exists) return McpErrors.WithNext($"Parent path not found: {parentPath}", PlcKindResolver.NotFoundHint);
        if (parent.ItemType is not (PlcItemType.Folder or PlcItemType.PlcApp))
            return $"[Error] Cannot create a visualization under '{parentPath}' (ItemType {parent.ItemType?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}): " +
                   "the parent must be a folder or the PLC project node.";

        // Validate the page against an empty visualization before anything changes.
        try { VisuEditor.ApplyPage(EmptyProbeDocument(), page, ResolverFor(parentPath)); }
        catch (VisuRequestException ex) { return "[Error] " + ex.Message + " Nothing was changed."; }

        var managerError = RequireManager(parentPath);
        if (managerError is not null) return managerError;

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string newPath;
        try { newPath = session.CreatePlcChild(parentPath, name, PlcItemType.VisObj, "VISU (VISUALIZATION)"); }
        catch (Exception ex) { return Describe(ex); }

        string written = Write(newPath, parentPath, name, null, doc => VisuEditor.ApplyPage(doc, page, ResolverFor(parentPath)), created: true, skipLogout: true);
        return written.StartsWith("[Error]", StringComparison.Ordinal)
            ? $"[Error] The visualization '{newPath}' was created (empty), but its content was not written: {written["[Error] ".Length..]}"
            : written;
    }

    // ---- shared write path -----------------------------------------------------------------

    private string Write(string path, string parentPath, string name, string? expectedRevision,
        Func<VisuDocument, VisuEditor.Outcome> change, bool created, bool skipLogout = false)
    {
        string file;
        string before;
        try
        {
            file = FilePathOf(path);
            before = ReadText(file);
        }
        catch (Exception ex) { return Describe(ex); }

        VisuDocument doc;
        try { doc = VisuDocument.Parse(before); }
        catch (VisuFormatException ex) { return $"[Error] '{path}': {ex.Message} ({file}). Nothing was changed."; }

        var problem = VisuEditor.WriteProblem(doc);
        if (problem is not null)
            return $"[Error] '{path}' is not written: {problem}. The tools write only formats they have verified; read it with twincat_visu_read. Nothing was changed.";

        if (!string.IsNullOrWhiteSpace(expectedRevision))
        {
            string current = VisuJson.Revision(before);
            if (!PlcRevision.Matches(expectedRevision, current))
                return $"[Error] '{path}' changed since it was read (revision {current}, expected {expectedRevision.Trim()}). " +
                       "Read it again with twincat_visu_read and redo the change. Nothing was changed.";
        }

        VisuEditor.Outcome outcome;
        try { outcome = change(doc); }
        catch (VisuRequestException ex) { return "[Error] " + ex.Message + " Nothing was changed."; }
        catch (VisuFormatException ex) { return $"[Error] '{path}': {ex.Message}. Nothing was changed."; }

        // Every distinct text needs its own text-list id in the file (XAE keeps the ids of a taken-over file as they are).
        var idLines = VisuTextIds.Assign(doc, VisuTextIds.Load(file));
        if (idLines.Count > 0)
            outcome = outcome with { Lines = [.. outcome.Lines, $"text-list ids set on {idLines.Count} element(s) (one id per distinct text)."] };

        string after = doc.Serialize();
        // The new file must read back as a writable visualization before XAE gets it.
        try
        {
            var check = VisuDocument.Parse(after);
            var p = VisuEditor.WriteProblem(check);
            if (p is not null) return $"[Error] The new content of '{path}' would not be valid ({p}). Nothing was changed.";
        }
        catch (VisuFormatException ex) { return $"[Error] The new content of '{path}' would not be valid ({ex.Message}). Nothing was changed."; }

        if (!created)
        {
            var managerError = RequireManager(parentPath);
            if (managerError is not null) return managerError;
        }
        if (!skipLogout)
        {
            var logout = SessionGuards.EnsureOfflineForWrite(session);
            if (logout is not null) return logout;
        }

        string work = Path.Combine(WorkRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + name);
        string stagePath = Path.Combine(work, "new", name + ".TcVIS");
        string backupPath = Path.Combine(work, "before", name + ".TcVIS");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.WriteAllText(backupPath, before, Utf8NoBom);
            File.WriteAllText(stagePath, after, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[Error] Cannot prepare the files in {work}: {ex.Message}. Nothing was changed.";
        }

        // An open editor holds the object; closing is best effort (the delete below closes it as well).
        try { session.ClosePlcObject(path); }
        catch (Exception ex) when (!ComCall.IsFinal(ex)) { }

        try { session.DeletePlcObject(path); }
        catch (Exception ex) { return $"[Error] Could not replace '{path}': the delete failed ({Message(ex)}). The visualization is unchanged; its file was not touched."; }

        try
        {
            session.ImportPlcObjectFile(parentPath, name, stagePath, PlcItemType.VisObj);
        }
        catch (Exception ex)
        {
            string importError = Message(ex);
            try
            {
                session.ImportPlcObjectFile(parentPath, name, backupPath, PlcItemType.VisObj);
                return $"[Error] XAE did not take the new content of '{path}' ({importError}). The visualization as it was before was imported again. " +
                       $"Files: new {stagePath}, before {backupPath}.";
            }
            catch (Exception ex2)
            {
                return $"[Error] XAE did not take the new content of '{path}' ({importError}), and importing the previous file failed too ({Message(ex2)}). " +
                       $"The visualization is missing from the project: add it again from {backupPath} (Add existing item in the XAE).";
            }
        }

        return ReadBack(path, after, outcome, backupPath, created);
    }

    private string ReadBack(string path, string written, VisuEditor.Outcome outcome, string backupPath, bool created)
    {
        string file;
        string actual;
        try
        {
            file = FilePathOf(path);
            actual = ReadText(file);
        }
        catch (Exception ex) { return $"[Error] '{path}' was replaced, but its file could not be read back: {Message(ex)}."; }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{(created ? "Created" : "Wrote")} visualization '{path}'.");
        foreach (var line in outcome.Lines) sb.AppendLine("  " + line);
        if (actual == written)
        {
            sb.AppendLine("Read back: the project file is exactly the content written.");
        }
        else
        {
            // XAE may normalise the file on import; compare what matters.
            try
            {
                string want = VisuJson.Fingerprint(VisuDocument.Parse(written));
                string got = VisuJson.Fingerprint(VisuDocument.Parse(actual));
                if (want != got)
                    return $"[Error] '{path}' was imported, but the project file differs from what was written ({file}). Previous file: {backupPath}.";
                sb.AppendLine("Read back: XAE rewrote the file; elements, properties and interface are as written.");
            }
            catch (VisuFormatException ex)
            {
                return $"[Error] '{path}' was imported, but its project file does not read back as a visualization ({ex.Message}). Previous file: {backupPath}.";
            }
        }
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"XAE took the file over by delete + import (object id kept); previous file: {backupPath}. Not saved — twincat_save_project afterwards.");
        sb.Append(CultureInfo.InvariantCulture, $"Revision: {VisuJson.Revision(actual)}");
        return sb.ToString();
    }

    // ---- write helpers ------------------------------------------------------------------

    /// <summary>
    /// Null when the PLC project of <paramref name="anyPathBelow"/> has a visualization manager. Without one XAE adds
    /// one by itself when a visualization is created or imported (seen live), so the tools refuse and name the call.
    /// </summary>
    private string? RequireManager(string anyPathBelow)
    {
        string? root = null;
        try
        {
            root = PlcRootOf(anyPathBelow);
            if (root is null)
                return $"[Error] '{anyPathBelow}' is not inside a PLC project node. Nothing was changed.";
            if (session.ListTreeChildren(root).Any(c => c.ItemType == PlcItemType.VisMan)) return null;
        }
        catch (Exception ex) { return Describe(ex); }

        return McpErrors.WithNext(
            $"The PLC project '{root}' has no visualization manager. XAE needs one to show a visualization and would add it " +
            "by itself; the visu tools do not do that silently. Nothing was changed.",
            $"twincat_plc_create(kind='VISU', subtype='MANAGER', parentPath='{root}', name='VisualizationManager'), then call this tool again.");
    }

    /// <summary>The PLC project node above <paramref name="anyPathBelow"/>; null when there is none.</summary>
    private string? PlcRootOf(string anyPathBelow)
    {
        string probe = anyPathBelow;
        while (true)
        {
            var info = session.ProbePlcNode(probe);
            if (info.Exists && info.ItemType == PlcItemType.PlcApp) return probe;
            int caret = probe.LastIndexOf('^');
            if (caret <= 0) return null;
            probe = probe[..caret];
        }
    }

    /// <summary>
    /// Finds a visualization of the PLC project of <paramref name="anyPathBelow"/> by name, for a frame: searches the
    /// project's folders and reads the VAR_IN_OUT variables of the visualization's interface from its file.
    /// </summary>
    internal VisuEditor.VisuResolver ResolverFor(string anyPathBelow) => name =>
    {
        try
        {
            string root = PlcRootOf(anyPathBelow) ?? throw new VisuRequestException($"'{anyPathBelow}' is not inside a PLC project node.");
            string? found = FindVisualization(root, name, depth: 0);
            if (found is null) return null;
            var doc = VisuDocument.Parse(ReadText(FilePathOf(found)));
            var (variables, problem) = VisuInterfaceParser.Parse(doc.InterfaceText);
            if (problem is not null)
                throw new VisuRequestException($"The visualization '{name}' cannot be shown in a frame by the tools: {problem}.");
            // The name as the project tree has it (the reference names the object, not the file).
            return new VisuReferenceTarget(found[(found.LastIndexOf('^') + 1)..], variables);
        }
        catch (VisuRequestException) { throw; }
        catch (VisuFormatException ex) { throw new VisuRequestException($"The visualization '{name}' cannot be read: {ex.Message}."); }
        catch (Exception ex) when (!ComCall.IsFinal(ex)) { throw new VisuRequestException($"The visualization '{name}' could not be looked up: {Message(ex)}."); }
    };

    private string? FindVisualization(string path, string name, int depth)
    {
        if (depth > 8) return null;
        var children = session.ListTreeChildren(path);
        foreach (var c in children)
            if (c.ItemType == PlcItemType.VisObj && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                return path + "^" + c.Name;
        foreach (var c in children.Where(c => c.ItemType == PlcItemType.Folder))
            if (FindVisualization(path + "^" + c.Name, name, depth + 1) is { } found)
                return found;
        return null;
    }

    /// <summary>A minimal valid visualization, only to validate a page before a new one is created.</summary>
    private static VisuDocument EmptyProbeDocument() => VisuDocument.Parse(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><TcPlcObject Version=\"1.1.0.1\"><Visu Name=\"Probe\" Id=\"{00000000-0000-0000-0000-000000000000}\">" +
        "<XmlArchive><Data><o xml:space=\"preserve\" t=\"VisualObject\"><v n=\"UniqueIdGenerator\">\"3\"</v>" +
        "<o n=\"VisualElemList\" t=\"VisualElemList\"><l n=\"VisualElementList\" t=\"VisualElemCollection\" /></o>" +
        "<o n=\"TextDocument\" t=\"TextDocument\"><v n=\"TextBlobForSerialisation\">\"VAR_IN_OUT\nEND_VAR\"</v></o>" +
        "</o></Data><TypeList><Type n=\"TextDocument\">x</Type><Type n=\"VisualElemCollection\">x</Type><Type n=\"VisualElemList\">x</Type>" +
        "<Type n=\"VisualObject\">x</Type></TypeList></XmlArchive></Visu></TcPlcObject>");
}
