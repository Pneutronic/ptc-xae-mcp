namespace PtcXaeMcp.WinForms.TwinCat.Visu;

// twincat_visu_edit_elements.
internal sealed partial class VisuCoordinator
{
    // ---- edit elements ------------------------------------------------------------------

    public string Edit(string path, string kind, string operationsJson, string? interfaceText, string? expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(path)) return "[Error] path is required.";
        path = path.Trim();
        var kindError = CheckKindArgument(kind);
        if (kindError is not null) return kindError;
        if (!TryParseJson(string.IsNullOrWhiteSpace(operationsJson) ? "[]" : operationsJson, "operations", out var ops, out var jsonError))
            return jsonError!;
        try { VisuEditor.Precheck(ops, isPage: false); }
        catch (VisuRequestException ex) { return "[Error] " + ex.Message + " Nothing was changed."; }
        int caret = path.LastIndexOf('^');
        if (caret <= 0) return $"[Error] '{path}' is not a tree path below a folder (parent^name).";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;
        PlcNodeInfo node;
        try { node = session.ProbePlcNode(path); }
        catch (Exception ex) { return Describe(ex); }
        var mismatch = PlcKindResolver.Check(path, PlcKind.Visu, node, VisualizationSubtype);
        if (mismatch is not null) return mismatch;

        return Write(path, path[..caret], path[(caret + 1)..], expectedRevision,
            doc => VisuEditor.ApplyOperations(doc, ops, interfaceText, ResolverFor(path)), created: false);
    }
}
