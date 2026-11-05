namespace PtcXaeMcp.WinForms.TwinCat;

// twincat_plc_list_entries / twincat_plc_edit_entries: the entries of a declaration (fields, enum values,
// variables per section) as structured data. The text work is DeclarationEntryEditor (pure); this part resolves
// the element, reads its declaration, refuses everything it can refuse BEFORE the logout, and writes the edited
// declaration back through the verified write path.
internal sealed partial class PlcObjectCoordinator
{
    private static readonly string[] ValidOps = ["add", "set", "remove"];

    // The shape of the declaration of an element, or the reason why it has no entries.
    private static (DeclarationShape? Shape, string? Error) ShapeOf(string path, PlcCatalogEntry entry)
    {
        string what = Describe(entry);
        switch (entry.Kind)
        {
            case PlcKind.Dut when entry.Subtype == "ENUM": return (DeclarationShape.EnumBody, null);
            case PlcKind.Dut when entry.Subtype is "STRUCT" or "UNION": return (DeclarationShape.StructBody, null);
            case PlcKind.Dut:
                return (null, $"[Error] '{path}' is {what}: an alias only names a base type, it has no entries. Rewrite it with twincat_plc_write.");
            case PlcKind.Gvl or PlcKind.Pou: return (DeclarationShape.VarSections, null);
            case PlcKind.Interface:
                return (null, $"[Error] '{path}' is {what}: its declaration holds only the header (EXTENDS), no entries. Edit its methods and properties instead.");
            case PlcKind.Member when !entry.HasDeclaration:
                return (null, $"[Error] '{path}' is {what}: it has no declaration section, so no entries.");
            case PlcKind.Member when entry.Subtype == "PROPERTY":
                return (null, $"[Error] '{path}' is {what}: a property node carries only its header. Edit the variables of its Get/Set accessor.");
            case PlcKind.Member: return (DeclarationShape.VarSections, null);
            default:
                return (null, $"[Error] '{path}' is {what}: this element has no declaration entries.");
        }
    }

    private string ReadDeclarationOf(string path, string kind) => kind switch
    {
        PlcKind.Member => session.ReadPouMember(path).Declaration,
        PlcKind.Dut or PlcKind.Gvl => session.ReadGvl(path),
        _ => session.ReadPou(path).Declaration,
    };

    public (string Summary, IReadOnlyList<DeclEntryInfo> Entries) ListEntries(string path, string kind, string? section = null)
    {
        var none = Array.Empty<DeclEntryInfo>();
        if (string.IsNullOrWhiteSpace(path)) return ("[Error] path is required.", none);
        string? err = ParseKindAndSubtype(kind, null, "list_entries", out string k, out _);
        if (err is not null) return (err, none);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, none);

        var (probeError, target) = ProbeKind(path, k);
        if (probeError is not null) return (probeError, none);
        var entry = target!.Value.Entry;
        var (shape, shapeError) = ShapeOf(path, entry);
        if (shapeError is not null) return (shapeError, none);

        string declaration;
        try { declaration = ReadDeclarationOf(path, k); }
        catch (Exception ex) { return (McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message), none); }

        var (entries, parseError) = DeclarationEntryEditor.List(declaration, shape!.Value, section);
        if (parseError is not null) return ($"{parseError} (in the declaration of '{path}')", none);

        string what = entry.Subtype is null ? k : $"{k}/{entry.Subtype}";
        if (entries!.Count == 0)
            return ($"No entries in {what} '{path}'{(section is null ? "" : $" section '{DeclarationEntryEditor.NormalizeSection(section)}'")}.", entries);
        int blocked = entries.Count(e => e.Problem is not null);
        string note = blocked == 0 ? "" : $" {blocked} cannot be edited with twincat_plc_edit_entries (see the notes).";
        return ($"Found {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} in {what} '{path}'.{note}", entries);
    }

    public string EditEntries(string path, string kind, string op, string name, string? section = null, string? type = null,
        string? value = null, string? initialValue = null, string? address = null, string? comment = null,
        string? afterName = null, string? newName = null, IReadOnlyList<string>? attributes = null, string? expectedRevision = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return "[Error] path is required.";
        string normOp = (op ?? "").Trim().ToLowerInvariant();
        if (!ValidOps.Contains(normOp))
            return $"[Error] Unknown op '{op}'. Expected add, set or remove.";
        string? err = ParseKindAndSubtype(kind, null, "edit_entries", out string k, out _);
        if (err is not null) return err;

        var edit = new EntryEdit(
            normOp switch { "add" => EntryOp.Add, "set" => EntryOp.Set, _ => EntryOp.Remove },
            name, section, type, value, initialValue, address, comment, afterName, newName, attributes);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        // Everything up to the write runs BEFORE the logout: kind, shape, parse and the edit itself.
        var (probeError, target) = ProbeKind(path, k);
        if (probeError is not null) return probeError;
        var entry = target!.Value.Entry;
        var (shape, shapeError) = ShapeOf(path, entry);
        if (shapeError is not null) return shapeError;

        string declaration;
        try
        {
            if (string.IsNullOrWhiteSpace(expectedRevision)) declaration = ReadDeclarationOf(path, k);
            else
            {
                var content = ReadContent(path, k, entry);
                var stale = RevisionRefusal(path, expectedRevision, content.Revision);
                if (stale is not null) return stale;
                declaration = content.Declaration;
            }
        }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message); }

        var result = DeclarationEntryEditor.Apply(declaration, shape!.Value, edit);
        if (result.Error is not null) return result.Error;
        string newText = result.Text!;

        if (k != PlcKind.Member)
        {
            var lintError = PlcDeclarationLint.CheckNoEmbeddedMemberHeader(newText);
            if (lintError is not null) return lintError;
        }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        try
        {
            if (k == PlcKind.Member) session.WritePouMember(path, newText, implementation: null);
            else session.WritePouBody(path, newText, implementation: null);
        }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        string consequence = normOp == "add"
            ? "Run twincat_build_plc to check the project still builds."
            : "Code and ADS callers that used the old entry break — run twincat_rebuild_plc to find them.";
        // The new revision lets the caller chain the next edit without another read; best-effort.
        string revision = "";
        try { revision = $" Revision: {ReadContent(path, k, entry).Revision}"; }
        catch (Exception ex) when (!ComCall.IsFinal(ex)) { }
        return $"{result.Summary} Written to {k} '{path}' (verified by read-back). {consequence}{revision}";
    }
}
