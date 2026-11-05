namespace PtcXaeMcp.WinForms.TwinCat;

// Orchestrates PLC source-code tools (twincat_list_pous / twincat_read_pou /
// twincat_read_gvl / twincat_write_pou_body / twincat_create_pou).
//
// Almost pure delegation to the session — the COM session is the work. Each
// public method begins with SessionGuards.RequireOpenSolution and wraps the
// session call in a try/catch that turns InvalidOperationException into an
// "[Error] ..." summary, matching the convention used by LibraryCoordinator.
//
// WritePouBody adds one extra check: it refuses non-ST POUs up front by
// reading PouBody.Language first, so the user gets a clear refusal instead of
// the cryptic "no <ST> child" error the patcher would otherwise raise.
internal sealed class PlcCodeCoordinator(ITwinCatSession session)
{
    // The tools a partial-create message points the caller to. The legacy tools name themselves; the
    // generic twincat_plc_* tools (PlcObjectCoordinator) override both so the advice names the tools in use.
    internal string? WriteToolName { get; init; }
    internal string? DeleteToolName { get; init; }

    public (string Summary, IReadOnlyList<PouEntry> Entries) ListPous()
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<PouEntry>());

        IReadOnlyList<PouEntry> entries;
        try { entries = session.ListPous(); }
        catch (Exception ex)
        {
            return ($"[Error] {ex.Message}", Array.Empty<PouEntry>());
        }

        if (entries.Count == 0)
            return ("No POUs, GVLs, or DUTs found in the open PLC project.", entries);

        int pous = entries.Count(e => e.Kind == PouKind.Pou);
        int gvls = entries.Count(e => e.Kind == PouKind.Gvl);
        int duts = entries.Count(e => e.Kind == PouKind.Dut);
        int itfs = entries.Count(e => e.Kind == PouKind.Interface);
        // Only surface the INTERFACE tally when there is at least one, so the
        // common (interface-free) project keeps its original "POU, GVL, DUT" shape.
        string itfPart = itfs > 0 ? $", {itfs} INTERFACE" : string.Empty;
        return ($"Found {entries.Count} item{(entries.Count == 1 ? "" : "s")} " +
                $"({pous} POU, {gvls} GVL, {duts} DUT{itfPart}).",
                entries);
    }

    public (string Summary, PouBody? Body) ReadPou(string treePath)
    {
        // Cheap input validation first — an empty treePath is a programmer error
        // and should fail the same way regardless of whether a solution is open.
        if (string.IsNullOrWhiteSpace(treePath))
            return ("[Error] treePath is required.", null);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, null);

        try
        {
            var body = session.ReadPou(treePath);
            return ($"Read POU '{treePath}' ({body.Language}).", body);
        }
        catch (Exception ex)
        {
            return ($"[Error] {ex.Message}", null);
        }
    }

    public (string Summary, string? Declaration) ReadGvl(string treePath)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return ("[Error] treePath is required.", null);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, null);

        try
        {
            var decl = session.ReadGvl(treePath);
            return ($"Read GVL '{treePath}'.", decl);
        }
        catch (Exception ex)
        {
            return ($"[Error] {ex.Message}", null);
        }
    }

    private bool IsDeclarationOnlyNode(string treePath)
    {
        try
        {
            return session.ProbePlcNode(treePath).ItemType is int itemType
                && PlcCodeClassification.IsDeclarationOnly(itemType);
        }
        catch (Exception ex) when (!ComCall.IsFinal(ex)) { return false; }
    }

    public string WritePouBody(string treePath, string? declaration, string? implementation,
        string? declarationFile = null, string? implementationFile = null)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return "[Error] treePath is required.";

        var declIn = SourceTextInput.Resolve("declaration", declaration, declarationFile);
        if (declIn.Error is not null) return declIn.Error;
        var implIn = SourceTextInput.Resolve("implementation", implementation, implementationFile);
        if (implIn.Error is not null) return implIn.Error;
        declaration = declIn.Text;
        implementation = implIn.Text;

        if (declaration is null && implementation is null)
            return "[Error] At least one of declaration or implementation must be supplied — " +
                   "inline, or from a file via declarationFile/implementationFile.";
        var lintError = PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration);
        if (lintError is not null) return lintError;

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        // Up-front target read + language check BEFORE the logout (issue #148, B-15): a wrong
        // path or a non-ST POU must not cost a logout from a running plant. Non-ST POUs get a
        // clean refusal instead of a late "no <ST> child" error from the write path.
        try
        {
            var existing = session.ReadPou(treePath);
            if (!string.Equals(existing.Language, "ST", StringComparison.OrdinalIgnoreCase))
                return $"[Error] POU '{treePath}' is implemented in {existing.Language}. Only ST POUs can be written through this tool.";

            // ReadPou reports an INTERFACE as "ST" with an empty implementation, so the check above
            // passes; the ImplementationText setter would then fail only after the logout below.
            // A failed probe keeps the previous behaviour (no extra refusal) — it must not block
            // writes to ordinary POUs that worked before this guard existed.
            if (implementation is not null && IsDeclarationOnlyNode(treePath))
                return $"[Error] '{treePath}' is an INTERFACE: it has no implementation section. " +
                       "Write only its declaration (implementation must be null).";
        }
        catch (Exception ex) { return McpErrors.EnsureError(ex.Message); }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        try
        {
            session.WritePouBody(treePath, declaration, implementation);
            return $"Wrote POU '{treePath}'." + SourceTextInput.FormatNotes(declIn, implIn);
        }
        catch (Exception ex)
        {
            return $"[Error] {ex.Message}";
        }
    }

    public string WriteGvl(string treePath, string? declaration, string? declarationFile = null)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return "[Error] treePath is required.";

        var declIn = SourceTextInput.Resolve("declaration", declaration, declarationFile);
        if (declIn.Error is not null) return declIn.Error;
        declaration = declIn.Text;

        if (string.IsNullOrWhiteSpace(declaration))
            return "[Error] declaration is required — inline, or from a file via declarationFile.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        // Confirm the node exists and is reachable as a declaration-only object — BEFORE the
        // logout (issue #148, B-15). ReadGvl reads only DeclarationText (ITcPlcDeclaration),
        // which GVLs/DUTs expose — unlike ReadPou, it never touches ImplementationText, so it
        // can't trip the "no ITcPlcImplementation" error that the write_pou_body language
        // guard raises on a GVL.
        try { _ = session.ReadGvl(treePath); }
        catch (Exception ex) { return McpErrors.EnsureError(ex.Message); }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        try
        {
            // Declaration-only write: implementation=null means the session sets ONLY
            // DeclarationText and leaves ImplementationText untouched, with the same
            // read-back verification write_pou_body uses.
            session.WritePouBody(treePath, declaration, implementation: null);
            return $"Wrote GVL '{treePath}'." + SourceTextInput.FormatNotes(declIn);
        }
        catch (Exception ex)
        {
            return $"[Error] {ex.Message}";
        }
    }

    public string CreatePou(string parentPath, string name, string pouType, string? returnType, string? implLanguage,
        string? declaration = null)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            return "[Error] parentPath is required.";
        if (string.IsNullOrWhiteSpace(name))
            return "[Error] name is required.";
        if (string.IsNullOrWhiteSpace(pouType))
            return "[Error] pouType is required (PROGRAM | FUNCTION_BLOCK | FUNCTION).";

        string normType = pouType.Trim().ToUpperInvariant();
        if (normType is not ("PROGRAM" or "FUNCTION_BLOCK" or "FUNCTION"))
            return $"[Error] Unknown pouType '{pouType}'. Expected PROGRAM, FUNCTION_BLOCK, or FUNCTION.";

        var nameError = IecIdentifier.Validate(name, "POU name");
        if (nameError is not null) return nameError;
        string normName = name.Trim();
        var lintError = PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration);
        if (lintError is not null) return lintError;

        // Unknown language -> [Error], never a silent ST (issue #148, LIVE-9 / B-8).
        var langError = PlcImplLanguage.Validate(implLanguage);
        if (langError is not null) return langError;
        string normLang = PlcImplLanguage.Normalize(implLanguage);

        // FUNCTION needs a return type; default to BOOL when caller omits one.
        // PROGRAM / FUNCTION_BLOCK ignore returnType.
        string normReturn = normType == "FUNCTION"
            ? (string.IsNullOrWhiteSpace(returnType) ? "BOOL" : returnType!.Trim())
            : string.Empty;

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;
        error = SessionGuards.RequirePlcNode(session, parentPath, "Parent path");
        if (error is not null) return error;
        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string newPath;
        try { newPath = session.CreatePou(parentPath, normName, normType, normReturn, normLang); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        // Optional declaration, written through the same verified path as write_pou_body. Without it
        // TwinCAT's default skeleton remains.
        var partial = ApplyDeclaration(newPath, $"{normType} '{normName}'", declaration, "twincat_plc_write");
        return partial ?? $"Created {normType} '{normName}' at {newPath}.";
    }

    public string CreateDut(string parentPath, string name, string? dutType, string? declaration)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            return "[Error] parentPath is required.";
        if (string.IsNullOrWhiteSpace(name))
            return "[Error] name is required.";

        // dutType defaults to STRUCT (the most common DUT kind) when omitted.
        string normType = string.IsNullOrWhiteSpace(dutType) ? "STRUCT" : dutType!.Trim().ToUpperInvariant();
        if (normType is not ("STRUCT" or "ENUM" or "UNION" or "ALIAS"))
            return $"[Error] Unknown dutType '{dutType}'. Expected STRUCT, ENUM, UNION, or ALIAS.";

        var nameError = IecIdentifier.Validate(name, "DUT name");
        if (nameError is not null) return nameError;

        // ALIAS is special: CreateChild needs the base type as vInfo at creation
        // time (it rejects a null vInfo with "Base class not specified", issue #106).
        // STRUCT/ENUM/UNION are created with a null vInfo. We derive the base type
        // from the declaration (TYPE <name> : <baseType>; END_TYPE), so an ALIAS
        // without a declaration is rejected up front with a clear message.
        string? baseType = null;
        if (normType == "ALIAS")
        {
            baseType = ExtractAliasBaseType(declaration);
            if (baseType is null)
                return "[Error] ALIAS DUTs require a declaration of the form " +
                       "'TYPE <name> : <baseType>; END_TYPE' — the base type is needed at creation time.";
        }

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;
        error = SessionGuards.RequirePlcNode(session, parentPath, "Parent path");
        if (error is not null) return error;
        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        name = name.Trim();
        string newPath;
        try { newPath = session.CreateDut(parentPath, name, normType, baseType); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        // Fill the declaration through the same verified path as write_gvl
        // (set DeclarationText + read-back). Omitted → TwinCAT's default skeleton.
        var partial = ApplyDeclaration(newPath, $"DUT {normType} '{name}'", declaration, "twincat_plc_write");
        return partial ?? $"Created DUT {normType} '{name}' at {newPath}.";
    }

    // B-16 (issue #152): the second step of a multi-step create (writing the declaration) can fail
    // after the object already exists. The result must say so — "created at <path>; declaration NOT
    // applied" — instead of a bare error that reads as "nothing happened". Null when there was no
    // declaration to write or it was written and verified.
    internal string? ApplyDeclaration(string newPath, string what, string? declaration, string fixTool)
    {
        if (string.IsNullOrWhiteSpace(declaration)) return null;
        fixTool = WriteToolName ?? fixTool;
        try
        {
            session.WritePouBody(newPath, declaration, implementation: null);
            return null;
        }
        catch (Exception ex)
        {
            return "[Error] " + PartialCreate.Describe(what, newPath, "declaration", ex.Message,
                $"Write it with {fixTool} ({(WriteToolName is null ? "treePath" : "path")} '{newPath}'), or delete the object with {DeleteToolName ?? "twincat_plc_delete"}.");
        }
    }

    // Parse the base type out of an alias declaration: TYPE <name> : <baseType> ;.
    // The first ':' follows the type name, the first ';' after it closes the alias,
    // so the slice between them is the base type (e.g. "INT", "POINTER TO ST_Foo",
    // "ARRAY[0..9] OF INT"). Returns null when there is no parsable base type.
    private static string? ExtractAliasBaseType(string? declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration)) return null;
        int colon = declaration.IndexOf(':');
        if (colon < 0) return null;
        int semicolon = declaration.IndexOf(';', colon + 1);
        if (semicolon < 0) return null;
        string baseType = declaration.Substring(colon + 1, semicolon - colon - 1).Trim();
        return baseType.Length == 0 ? null : baseType;
    }

    private static readonly System.Text.RegularExpressions.Regex PersistentKeyword =
        new(@"\bPERSISTENT\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public string CreateGvl(string parentPath, string name, bool persistent, string? declaration)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            return "[Error] parentPath is required.";
        if (string.IsNullOrWhiteSpace(name))
            return "[Error] name is required.";

        var nameError = IecIdentifier.Validate(name, "GVL name");
        if (nameError is not null) return nameError;
        name = name.Trim();

        // "Persistent" is a section of an ordinary GVL, not an object type (nSubType 629 is a
        // Parameter List). A declaration that lacks the keyword would silently yield a plain GVL, so
        // it is refused up front — before the logout and before anything is created.
        if (persistent)
        {
            if (string.IsNullOrWhiteSpace(declaration))
                declaration = "VAR_GLOBAL PERSISTENT\nEND_VAR";
            else if (!PersistentKeyword.IsMatch(declaration))
                return "[Error] persistent=true needs a declaration with a 'VAR_GLOBAL PERSISTENT' section " +
                       "(or omit declaration for an empty one); the given declaration has no PERSISTENT keyword.";
        }

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;
        error = SessionGuards.RequirePlcNode(session, parentPath, "Parent path");
        if (error is not null) return error;
        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string kind = persistent ? "persistent GVL" : "GVL";
        string newPath;
        try { newPath = session.CreateGvl(parentPath, name); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        var partial = ApplyDeclaration(newPath, $"{kind} '{name}'", declaration, "twincat_plc_write");
        return partial ?? $"Created {kind} '{name}' at {newPath}.";
    }

    // ---- visible-for-video ---------------------------------------------------
    // Opt-in "show me this object" action: open a PLC source object in the visible
    // XAE editor (brought forward only with bringToFront). Mirrors ReadPou's guard order (blank path →
    // error before any session touch; RequireOpenSolution; session throw → [Error]).
    public (string Summary, OpenPlcObjectResult? Result) OpenPlcObject(string treePath, bool bringToFront = false)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return ("[Error] treePath is required.", null);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, null);

        try
        {
            var r = session.OpenPlcObject(treePath, bringToFront);
            return (FormatPlcOpenSummary(r.TreePath, r.AlreadyOpen, r.ForegroundRequested, r.ForegroundConfirmed, r.Note), r);
        }
        catch (Exception ex)
        {
            return ($"[Error] {ex.Message}", null);
        }
    }

    // Tab activation is the reliable part; OS foreground is best-effort and
    // distinguished in the summary so a denied focus-steal never reads as a failure
    // (Windows blocks foreground steals from a background process). Shared by
    // OpenPlcObject and OpenObject(PlcObject) so the deprecated alias and the
    // generalized tool produce byte-identical PLC summaries.
    private static string FormatPlcOpenSummary(
        string treePath, bool alreadyOpen, bool foregroundRequested, bool foregroundConfirmed, string? note)
    {
        string openPart = alreadyOpen
            ? $"already open — re-activated tab for '{treePath}'"
            : $"opened '{treePath}' and activated its tab";
        string fgPart = ForegroundPart(foregroundRequested, foregroundConfirmed);
        string notePart = string.IsNullOrWhiteSpace(note) ? "" : $" — {note}";
        return $"Opened PLC object: {openPart}; {fgPart}.{notePart}";
    }

    private static string ForegroundPart(bool requested, bool confirmed) =>
        !requested ? "XAE left where it is (brought to the foreground only with bringToFront=true)"
        : confirmed ? "XAE raised to foreground"
        : "OS foreground requested but not confirmed (Windows may block focus steal)";

    // ---- open-object (generalization; alias + new tool share this) -----------
    // PLC objects → own editor tab (FormatPlcOpenSummary, identical to OpenPlcObject).
    // Sysman nodes → selected in the shared sysman editor (no per-node tab). Same guard
    // order as ReadPou (blank path → error before any session touch; RequireOpenSolution).
    public (string Summary, OpenObjectResult? Result) OpenObject(string treePath, bool bringToFront = false)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return ("[Error] treePath is required.", null);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, null);

        try
        {
            var r = session.OpenObject(treePath, bringToFront);
            string summary = r.Kind == OpenObjectKind.PlcObject
                ? FormatPlcOpenSummary(r.TreePath, r.AlreadyOpen, r.ForegroundRequested, r.ForegroundConfirmed, r.Note)
                : FormatSysmanOpenSummary(r.TreePath, r.ForegroundRequested, r.ForegroundConfirmed, r.Note);
            return (summary, r);
        }
        catch (Exception ex)
        {
            return ($"[Error] {ex.Message}", null);
        }
    }

    private static string FormatSysmanOpenSummary(string treePath, bool foregroundRequested, bool foregroundConfirmed, string? note)
    {
        string fgPart = ForegroundPart(foregroundRequested, foregroundConfirmed);
        string notePart = string.IsNullOrWhiteSpace(note) ? "" : $" — {note}";
        // Issue #124: the session activates the node (select + Enter) when the XAE is in the foreground.
        bool activated = note?.Contains("Enter sent to open its editor", StringComparison.Ordinal) == true;
        return activated
            ? $"Opened object: activated system-manager node '{treePath}' (selected + Enter, like a double-click) — its " +
              $"editor is open (the shared sysman editor; a Scope node opens its Scope view); {fgPart}.{notePart}"
            : $"Opened object: selected system-manager node '{treePath}' — the shared sysman editor follows the selection once it is open (opening it needs the XAE in the foreground); {fgPart}.{notePart}";
    }

    public string CloseObject(string treePath)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return "[Error] treePath is required.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        try
        {
            var r = session.CloseObject(treePath);
            string notePart = string.IsNullOrWhiteSpace(r.Note) ? "" : $" — {r.Note}";

            if (r.Kind == OpenObjectKind.SysmanNode)
                return $"No tab to close for system-manager node '{r.TreePath}'.{notePart}";

            if (!r.WasOpen)
                return $"No editor tab was open for '{r.TreePath}'.{notePart}";
            string savePart = r.Saved ? "saved and " : "";
            return r.Closed
                ? $"Closed object: {savePart}closed the tab for '{r.TreePath}'.{notePart}"
                : $"[Error] Found the tab for '{r.TreePath}' but could not close it.{notePart}";
        }
        catch (Exception ex)
        {
            return $"[Error] {ex.Message}";
        }
    }

    // Counterpart to OpenPlcObject: close an object's visible editor tab. Mirrors the
    // same guard order (blank path → error before any session touch; RequireOpenSolution).
    public string ClosePlcObject(string treePath)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return "[Error] treePath is required.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        try
        {
            var r = session.ClosePlcObject(treePath);
            string note = string.IsNullOrWhiteSpace(r.Note) ? "" : $" — {r.Note}";
            if (!r.WasOpen)
                return $"No editor tab was open for '{r.TreePath}'.{note}";
            string savePart = r.Saved ? "saved and " : "";
            return r.Closed
                ? $"Closed PLC object: {savePart}closed the tab for '{r.TreePath}'.{note}"
                : $"[Error] Found the tab for '{r.TreePath}' but could not close it.{note}";
        }
        catch (Exception ex)
        {
            return $"[Error] {ex.Message}";
        }
    }

    public string CreateInterface(string parentPath, string name, string? declaration)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            return "[Error] parentPath is required.";
        if (string.IsNullOrWhiteSpace(name))
            return "[Error] name is required.";

        var nameError = IecIdentifier.Validate(name, "INTERFACE name");
        if (nameError is not null) return nameError;
        name = name.Trim();

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;
        error = SessionGuards.RequirePlcNode(session, parentPath, "Parent path");
        if (error is not null) return error;
        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string newPath;
        try { newPath = session.CreateInterface(parentPath, name); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        // Interfaces are declaration-only; fill the optional declaration through
        // the same verified path as write_gvl (see CreateDut). Methods are added
        // afterwards via add_pou_member. Omitted → TwinCAT's default
        // `INTERFACE … END_INTERFACE` skeleton.
        var partial = ApplyDeclaration(newPath, $"INTERFACE '{name}'", declaration, "twincat_plc_write");
        return partial ?? $"Created INTERFACE '{name}' at {newPath}.";
    }
}
