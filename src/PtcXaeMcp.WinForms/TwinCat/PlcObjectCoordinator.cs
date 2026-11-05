namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One element as listed by twincat_plc_list.</summary>
internal sealed record PlcElementEntry(string Path, string Name, string Kind, string? Subtype, string? Language);

/// <summary>The text content of one element as read by twincat_plc_read (only the sections the element has).</summary>
internal sealed record PlcElementContent(
    string Kind, string? Subtype, bool HasDeclaration, bool HasImplementation,
    string Declaration, string Implementation, string Language)
{
    /// <summary>Fingerprint of the text as read; twincat_plc_write(expectedRevision) refuses when it moved on.</summary>
    public string Revision => PlcRevision.Of(Declaration, Implementation);
}

/// <summary>
/// A short content hash of a PLC element's two sections. Two clients editing the same element (parallel
/// sessions, a human in the XAE) otherwise overwrite each other silently: the second write wins with text
/// built on a state that no longer exists. Read → edit → write with the revision from the read turns that
/// into a refusal before anything is changed.
/// </summary>
internal static class PlcRevision
{
    /// <summary>16 hex digits of SHA-256 over declaration, a NUL separator and implementation (UTF-8, as read).</summary>
    internal static string Of(string declaration, string implementation)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(declaration + "\0" + implementation);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 8).ToLowerInvariant();
    }

    internal static bool Matches(string expected, string actual) =>
        string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
}

// Orchestrates the generic twincat_plc_* verbs: one schema for every
// manipulation of every PLC element, selected by `kind`. Every verb that targets an existing node first
// compares `kind` with the node's real ItemType (PlcKindResolver) — a mismatch is refused before any
// logout and without a change. Creating, reading and deleting the text elements reuses the verified
// legacy coordinators; what is new here is the kind check, the per-kind parameter check, FOLDER and
// PARAMLIST handling, and one write path for declaration and implementation of every element.
//
// Kinds whose content has no verified Automation Interface path yet (text lists, image pools, visualisations,
// recipes, trends, UML, task references) answer "[Error] ... not supported" with the reason, never silently.
internal sealed partial class PlcObjectCoordinator(ITwinCatSession session)
{
    private const string WriteTool = "twincat_plc_write";
    private const string DeleteTool = "twincat_plc_delete";

    // Allowed parents of a FOLDER: a folder, the PLC project root, and (TC >= 4024.14) a POU or INTERFACE.
    private static readonly int[] FolderParents =
    [
        PlcItemType.PlcApp, PlcItemType.Folder, PlcItemType.PouProgram, PlcItemType.PouFunction,
        PlcItemType.PouFunctionBlock, PlcItemType.Interface,
    ];

    private static readonly int[] MemberParents =
    [
        PlcItemType.PouProgram, PlcItemType.PouFunction, PlcItemType.PouFunctionBlock, PlcItemType.Interface,
    ];

    private PlcCodeCoordinator CodeCoordinator => new(session) { WriteToolName = WriteTool, DeleteToolName = DeleteTool };

    // ---- kind gate -----------------------------------------------------------

    // Kinds whose element can be listed, created, renamed and deleted, but whose content (text-list rows, images,
    // visualisation objects, recipes, class diagram, task assignment) XAE does not hand out: ProduceXml returns only tree
    // metadata and there is no declaration/implementation (verified live 2026-10-07).
    private static readonly string[] ContentlessKinds =
        [PlcKind.TextList, PlcKind.ImagePool, PlcKind.Visu, PlcKind.Recipe, PlcKind.Trend, PlcKind.Uml, PlcKind.TaskRef];

    private static readonly string[] ContentVerbs = ["read", "write", "list_entries", "edit_entries"];

    // Null when the verb works for the kind in this build, else the reason as an "[Error] ...".
    private static string? Unsupported(string kind, string verb)
    {
        if (kind == PlcKind.Library)
            return $"[Error] kind LIBRARY is not handled by twincat_plc_{verb}: library references have their own " +
                   "tools (twincat_list_library_references, twincat_add_library_reference, twincat_remove_library_reference, " +
                   "twincat_set_library_version).";
        if (ContentlessKinds.Contains(kind) && ContentVerbs.Contains(verb))
            return $"[Error] kind {kind} is not supported by twincat_plc_{verb}: XAE does not expose the content of this element " +
                   "through the Automation Interface (ProduceXml returns only tree metadata; it has no declaration or " +
                   "implementation). Listing, creating (where XAE allows it), renaming and deleting work. Nothing was changed.";
        return null;
    }

    private static string Describe(PlcCatalogEntry e) =>
        e.Subtype is null ? PlcKindResolver.WithArticle(e.Kind!) : $"{PlcKindResolver.WithArticle(e.Kind!)} ({e.Subtype})";

    private readonly record struct Target(PlcNodeInfo Node, PlcCatalogEntry Entry);

    // Resolve the node and compare it with the requested kind (before any logout). Error text or the target.
    private (string? Error, Target? Target) ProbeKind(string path, string kind, string? subtype = null)
    {
        PlcNodeInfo node;
        try { node = session.ProbePlcNode(path); }
        catch (Exception ex) when (ComCall.IsBusy(ex)) { return (McpErrors.EnsureError(ComCall.Describe(ex)), null); }
        catch (Exception ex) { return (McpErrors.EnsureError(ex.Message), null); }

        string? mismatch = PlcKindResolver.Check(path, kind, node, subtype);
        if (mismatch is not null) return (mismatch, null);
        return (null, new Target(node, PlcElementCatalog.ForItemType(node.ItemType!.Value)!));
    }

    private static string? ParseKindAndSubtype(string? rawKind, string? rawSubtype, string verb,
        out string kind, out string? subtype)
    {
        subtype = null;
        if (!PlcKindResolver.TryParseKind(rawKind, out kind, out var err)) return err;
        if (!PlcKindResolver.TryParseSubtype(kind, rawSubtype, out subtype, out err)) return err;
        return Unsupported(kind, verb);
    }

    // ---- list ----------------------------------------------------------------

    public (string Summary, IReadOnlyList<PlcElementEntry> Entries) List(string? kind, string? parentPath, bool recursive = true)
    {
        var none = Array.Empty<PlcElementEntry>();
        string? normKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            string? err = ParseKindAndSubtype(kind, null, "list", out string k, out _);
            if (err is not null) return (err, none);
            normKind = k;
        }
        if (normKind == PlcKind.Member && string.IsNullOrWhiteSpace(parentPath))
            return ("[Error] parentPath (the POU or INTERFACE that owns the members) is required for kind=MEMBER.", none);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, none);

        PlcNodeInfo? parent = null;
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            try { parent = session.ProbePlcNode(parentPath); }
            catch (Exception ex) { return (McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message), none); }
            if (!parent.Exists) return ($"[Error] parentPath not found: {parentPath}", none);
        }

        // A POU/INTERFACE owns members; so does a PROPERTY (its Get/Set accessors). A folder is a plain container.
        bool parentOwnsMembers = parent?.ItemType is int pt
            && (MemberParents.Contains(pt) || pt is PlcItemType.Property or PlcItemType.InterfaceProperty);
        bool wantMembers = parentOwnsMembers
            ? normKind is null or PlcKind.Member or PlcKind.Folder
            : normKind == PlcKind.Member;

        List<PlcElementEntry> entries;
        try
        {
            if (wantMembers)
            {
                if (!parentOwnsMembers)
                    return ($"[Error] '{parentPath}' cannot own members (ItemType {parent!.ItemType}). " +
                            "kind=MEMBER needs a POU, an INTERFACE or a PROPERTY as parentPath.", none);
                // Under a POU the folders the members are grouped in are elements too.
                entries = session.ListPouElements(parentPath!)
                    .Select(m => ToEntry(m.Path, m.Name, m.Kind == PlcKind.Folder ? PlcKind.Folder : PlcKind.Member, m.ItemType, null))
                    .ToList();
            }
            else
            {
                entries = session.ListPlcElements()
                    .Select(p => ToEntry(p.Path, p.Name, p.Kind, p.ItemType, p.Language))
                    .ToList();
            }
        }
        catch (Exception ex) { return (McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message), none); }

        if (normKind is not null) entries = entries.Where(e => e.Kind == normKind).ToList();
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            string prefix = parentPath + "^";
            entries = entries.Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!recursive)
            {
                int depth = parentPath.Count(c => c == '^') + 1;
                entries = entries.Where(e => e.Path.Count(c => c == '^') == depth).ToList();
            }
        }

        if (entries.Count == 0)
            return ("No matching PLC elements found.", entries);

        string counts = string.Join(", ", entries.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}"));
        return ($"Found {entries.Count} element{(entries.Count == 1 ? "" : "s")} ({counts}).", entries);
    }

    private static PlcElementEntry ToEntry(string path, string name, string kind, int? itemType, string? language) =>
        new(path, name, kind, itemType is int it ? PlcElementCatalog.ForItemType(it)?.Subtype : null, language);

    // ---- read ----------------------------------------------------------------

    public (string Summary, PlcElementContent? Content) Read(string path, string kind, string? subtype = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("[Error] path is required.", null);
        string? err = ParseKindAndSubtype(kind, subtype, "read", out string k, out string? sub);
        if (err is not null) return (err, null);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, null);

        var (probeError, target) = ProbeKind(path, k, sub);
        if (probeError is not null) return (probeError, null);
        var entry = target!.Value.Entry;
        if (entry.Content == PlcContent.Structure)
            return ($"[Error] '{path}' is a FOLDER: it has no declaration or implementation to read. " +
                    "List its contents with twincat_plc_list (parentPath).", null);

        try
        {
            var content = ReadContent(path, k, entry);
            string what = entry.Subtype is null ? k : $"{k}/{entry.Subtype}";
            string lang = content.Language.Length > 0 ? $" ({content.Language})" : "";
            return ($"Read {what} '{path}'{lang}. Revision: {content.Revision}", content);
        }
        catch (Exception ex) { return (McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message), null); }
    }

    private PlcElementContent ReadContent(string path, string k, PlcCatalogEntry entry)
    {
        PouBody body = k switch
        {
            PlcKind.Member => session.ReadPouMember(path),
            PlcKind.Dut or PlcKind.Gvl => new PouBody(session.ReadGvl(path), string.Empty, string.Empty),
            _ => session.ReadPou(path),
        };
        return new PlcElementContent(k, entry.Subtype, entry.HasDeclaration, entry.HasImplementation,
            entry.HasDeclaration ? body.Declaration : string.Empty,
            entry.HasImplementation ? body.Implementation : string.Empty,
            entry.HasImplementation ? body.Language : string.Empty);
    }

    // ---- write ---------------------------------------------------------------

    /// <param name="expectedRevision">The revision twincat_plc_read reported; a different current revision refuses the write
    /// before the logout. Null skips the check.</param>
    public string Write(string path, string kind, string? declaration, string? implementation,
        string? declarationFile = null, string? implementationFile = null, string? expectedRevision = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return "[Error] path is required.";
        string? err = ParseKindAndSubtype(kind, null, "write", out string k, out _);
        if (err is not null) return err;

        var declIn = SourceTextInput.Resolve("declaration", declaration, declarationFile);
        if (declIn.Error is not null) return declIn.Error;
        var implIn = SourceTextInput.Resolve("implementation", implementation, implementationFile);
        if (implIn.Error is not null) return implIn.Error;
        declaration = declIn.Text;
        implementation = implIn.Text;
        if (declaration is null && implementation is null)
            return "[Error] At least one of declaration or implementation must be supplied — " +
                   "inline, or from a file via declarationFile/implementationFile.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        // Everything below runs BEFORE the logout: a wrong path, a wrong kind or a section the element does
        // not have must not cost a logout from a running plant.
        var (probeError, target) = ProbeKind(path, k);
        if (probeError is not null) return probeError;
        var entry = target!.Value.Entry;

        if (entry.Content == PlcContent.Structure)
            return $"[Error] '{path}' is a FOLDER: it has no declaration or implementation to write.";
        if (declaration is not null && !entry.HasDeclaration)
            return $"[Error] '{path}' is {Describe(entry)}: it has no declaration section, only an implementation. " +
                   "Write only its implementation (declaration must be null).";
        if (implementation is not null && !entry.HasImplementation)
            return $"[Error] '{path}' is {Describe(entry)}: it has no implementation section. " +
                   "Write only its declaration (implementation must be null).";

        // A MEMBER's own declaration starts with its header ("METHOD Foo : BOOL"); the check is meant for the
        // declaration of a parent (POU/INTERFACE/GVL/DUT), where a member header is always wrong.
        if (k != PlcKind.Member)
        {
            var lintError = PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration);
            if (lintError is not null) return lintError;
        }

        // A declaration is text in every language. An implementation is text in ST and IL; in SFC, FBD, CFC and LD it is
        // XAE's own XML (the root element depends on the language), which XAE refuses to take as anything else.
        if (implementation is not null && entry.HasImplementation)
        {
            try
            {
                var existing = k == PlcKind.Member ? session.ReadPouMember(path) : session.ReadPou(path);
                if (PlcImplLanguage.IsGraphical(existing.Language))
                {
                    string? xmlProblem = PlcImplLanguage.CheckGraphicalXml(existing.Language, implementation);
                    if (xmlProblem is not null)
                        return $"[Error] '{path}' is implemented in {existing.Language}: {xmlProblem}. Nothing was changed.";
                }
            }
            catch (Exception ex) { return McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message); }
        }

        if (!string.IsNullOrWhiteSpace(expectedRevision))
        {
            string current;
            try { current = ReadContent(path, k, entry).Revision; }
            catch (Exception ex) { return McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message); }
            if (!PlcRevision.Matches(expectedRevision, current))
                return $"[Error] '{path}' changed since it was read (expected revision {expectedRevision.Trim()}, now {current}) — " +
                       "someone else wrote it in between. Nothing was changed. Read it again with twincat_plc_read, apply your edit " +
                       "to that text and write with the new revision.";
        }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        try
        {
            if (k == PlcKind.Member) session.WritePouMember(path, declaration, implementation);
            else session.WritePouBody(path, declaration, implementation);
        }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        // The new revision lets the caller chain the next edit without another read; best-effort.
        string revision = "";
        try { revision = $" Revision: {ReadContent(path, k, entry).Revision}"; }
        catch (Exception ex) when (!ComCall.IsFinal(ex)) { }
        return $"Wrote {k} '{path}'.{revision}" + SourceTextInput.FormatNotes(declIn, implIn);
    }

    // ---- create --------------------------------------------------------------

    /// <param name="implementation">Written into the new element after it was created (through <see cref="Write"/>, with its
    /// read-back). Before, a body passed to the tool was dropped without a word and the element kept its empty skeleton.</param>
    public string Create(string kind, string? subtype, string parentPath, string name, string? declaration = null,
        string? returnType = null, string? implLanguage = null, string? accessors = null, string? implementation = null)
    {
        string created = CreateElement(kind, subtype, parentPath, name, declaration, returnType, implLanguage, accessors, implementation,
            out string k, out string? sub);
        if (implementation is null || created.StartsWith("[Error]", StringComparison.Ordinal)) return created;

        string written = Write(CreatedPath(parentPath, k, sub, name), k, declaration: null, implementation);
        return written.StartsWith("[Error]", StringComparison.Ordinal)
            ? $"{created}\n[Error] The element was created, but its implementation was not written: {written["[Error]".Length..].TrimStart()}"
            : $"{created}\n{written}";
    }

    private string CreateElement(string kind, string? subtype, string parentPath, string name, string? declaration,
        string? returnType, string? implLanguage, string? accessors, string? implementation, out string k, out string? sub)
    {
        k = string.Empty;
        sub = null;
        if (string.IsNullOrWhiteSpace(parentPath)) return "[Error] parentPath is required.";
        if (string.IsNullOrWhiteSpace(name)) return "[Error] name is required.";
        string? err = ParseKindAndSubtype(kind, subtype, "create", out k, out sub);
        if (err is not null) return err;

        // Default / required subtype per kind.
        switch (k)
        {
            case PlcKind.Pou when sub is null:
                return "[Error] subtype is required for kind=POU (PROGRAM | FUNCTION | FUNCTION_BLOCK).";
            case PlcKind.Member when sub is null:
                return "[Error] subtype is required for kind=MEMBER (METHOD | ACTION | PROPERTY | GET | SET | TRANSITION).";
            case PlcKind.Dut when sub is null: sub = "STRUCT"; break;
            case PlcKind.Gvl when sub is null: sub = "NORMAL"; break;
            case PlcKind.TextList when sub is null: sub = "LOCAL"; break;
            case PlcKind.ImagePool when sub is null: sub = "LOCAL"; break;
            case PlcKind.Uml when sub is null: sub = "CLASSDIAGRAM"; break;
            case PlcKind.TaskRef when sub is null: sub = "PROGRAM"; break;
            case PlcKind.Visu when sub is null:
                return "[Error] subtype is required for kind=VISU (VISUALIZATION | MANAGER | TARGET | WEB).";
            case PlcKind.Recipe when sub is null:
                return "[Error] subtype is required for kind=RECIPE (MANAGER | DEFINITION).";
        }

        string? misfit = Misfit(k, sub, declaration, returnType, implLanguage, accessors, implementation);
        if (misfit is not null) return misfit;

        switch (k)
        {
            case PlcKind.Pou:
                return CodeCoordinator.CreatePou(parentPath, name, sub!, returnType, implLanguage, declaration);

            case PlcKind.Dut:
                return CodeCoordinator.CreateDut(parentPath, name, sub, declaration);

            case PlcKind.Interface:
                return CodeCoordinator.CreateInterface(parentPath, name, declaration);

            case PlcKind.Gvl when sub == "NORMAL":
                return CodeCoordinator.CreateGvl(parentPath, name, persistent: false, declaration);

            case PlcKind.Gvl:
                return CreateChildElement(parentPath, name, PlcItemType.GvlParamList, "GVL (parameter list)",
                    [PlcItemType.PlcApp, PlcItemType.Folder], "a folder or the PLC project node",
                    iecName: true, declaration);

            case PlcKind.Folder:
                return CreateChildElement(parentPath, name, PlcItemType.Folder, "FOLDER",
                    FolderParents, "a folder, the PLC project node, a POU or an INTERFACE",
                    iecName: false, declaration: null);

            case PlcKind.Member:
                return CreateMember(parentPath, sub!, name, declaration, returnType, implLanguage, accessors);

            default:
                return CreateContentless(parentPath, name, k, sub!);
        }
    }

    // What XAE lets CreateChild create (null vInfo), and under which parent — verified live 2026-10-07. The GLOBAL text
    // list / image pool, the text-list enumeration, the trend elements and the task itself were refused by CreateChild at
    // the project root; they are not offered rather than guessed.
    private static readonly Dictionary<(string Kind, string Subtype), (int ItemType, int[] Parents, string ParentText)> Contentless = new()
    {
        [(PlcKind.TextList, "LOCAL")]        = (PlcItemType.TextList,         [PlcItemType.PlcApp, PlcItemType.Folder], "a folder or the PLC project node"),
        [(PlcKind.ImagePool, "LOCAL")]       = (PlcItemType.ImagePool,        [PlcItemType.PlcApp, PlcItemType.Folder], "a folder or the PLC project node"),
        [(PlcKind.Uml, "CLASSDIAGRAM")]      = (PlcItemType.UmlClassDiagram,  [PlcItemType.PlcApp, PlcItemType.Folder], "a folder or the PLC project node"),
        [(PlcKind.Visu, "MANAGER")]          = (PlcItemType.VisMan,           [PlcItemType.PlcApp, PlcItemType.Folder], "a folder or the PLC project node"),
        [(PlcKind.Visu, "VISUALIZATION")]    = (PlcItemType.VisObj,           [PlcItemType.PlcApp, PlcItemType.Folder], "a folder or the PLC project node"),
        [(PlcKind.Visu, "TARGET")]           = (PlcItemType.TargetVisu,       [PlcItemType.VisMan], "a visualization manager (kind=VISU, subtype=MANAGER)"),
        [(PlcKind.Visu, "WEB")]              = (PlcItemType.WebVisu,          [PlcItemType.VisMan], "a visualization manager (kind=VISU, subtype=MANAGER)"),
        [(PlcKind.Recipe, "MANAGER")]        = (PlcItemType.RecipeManager,    [PlcItemType.PlcApp, PlcItemType.Folder], "a folder or the PLC project node"),
        [(PlcKind.Recipe, "DEFINITION")]     = (PlcItemType.RecipeDefinition, [PlcItemType.RecipeManager], "a recipe manager (kind=RECIPE, subtype=MANAGER)"),
        [(PlcKind.TaskRef, "PROGRAM")]       = (PlcItemType.ProgRef,          [PlcItemType.Task], "a PLC task node (name = the PROGRAM to run)"),
    };

    private string CreateContentless(string parentPath, string name, string kind, string subtype)
    {
        if (!Contentless.TryGetValue((kind, subtype), out var spec))
            return $"[Error] kind={kind}, subtype={subtype} cannot be created through the Automation Interface " +
                   "(CreateChild refused it live 2026-10-07). Create it in XAE.";
        return CreateChildElement(parentPath, name, spec.ItemType, $"{kind} ({subtype})", spec.Parents, spec.ParentText,
            iecName: true, declaration: null);
    }

    // FOLDER and PARAMLIST: a plain CreateChild with the parent type checked before the logout.
    private string CreateChildElement(string parentPath, string name, int subType, string label,
        int[] allowedParents, string allowedParentsText, bool iecName, string? declaration)
    {
        var nameError = iecName ? IecIdentifier.Validate(name, $"{label} name") : PlcFolderName.Validate(name, $"{label} name");
        if (nameError is not null) return nameError;
        name = name.Trim();
        if (iecName)
        {
            var lintError = PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration);
            if (lintError is not null) return lintError;
        }

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        PlcNodeInfo parent;
        try { parent = session.ProbePlcNode(parentPath); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message); }
        if (!parent.Exists) return $"[Error] Parent path not found: {parentPath}";
        if (parent.ItemType is not int pt || !allowedParents.Contains(pt))
            return $"[Error] Cannot create a {label} under '{parentPath}' " +
                   $"(ItemType {(parent.ItemType is int shown ? shown.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unreadable")}): the parent must be {allowedParentsText}.";

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string newPath;
        try { newPath = session.CreatePlcChild(parentPath, name, subType, label); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        var partial = CodeCoordinator.ApplyDeclaration(newPath, $"{label} '{name}'", declaration, WriteTool);
        return partial ?? $"Created {label} '{name}' at {newPath}.";
    }

    private string CreateMember(string parentPath, string subtype, string name, string? declaration,
        string? returnType, string? implLanguage, string? accessors)
    {
        // A TRANSITION takes no creation parameters; it is ST, has an implementation and no declaration (live 2026-10-07).
        if (subtype == "TRANSITION")
            return CreateChildElement(parentPath, name, PlcItemType.Transition, "TRANSITION",
                [PlcItemType.PouProgram, PlcItemType.PouFunction, PlcItemType.PouFunctionBlock], "a POU (PROGRAM or FUNCTION_BLOCK in SFC)",
                iecName: true, declaration: null);

        string summary = new PouMembersCoordinator(session).AddPouMember(parentPath, subtype, name, returnType, implLanguage, accessors, out string? memberPath);
        if (memberPath is null || McpErrors.IsError(summary) || declaration is null) return summary;

        // Optional declaration for the new member, through the verified write path with a read-back.
        // The member exists already: a failure must say so instead of reading as "nothing happened".
        var logout = SessionGuards.EnsureOfflineForWrite(session);
        if (logout is not null) return PartialMember(summary, memberPath, logout);
        try { session.WritePouMember(memberPath, declaration, implementation: null); }
        catch (Exception ex) { return PartialMember(summary, memberPath, $"[Error] {ex.Message}"); }
        return summary + " Declaration written.";
    }

    private static string PartialMember(string createdSummary, string memberPath, string cause) =>
        "[Error] " + PartialCreate.Describe($"The member ({createdSummary.TrimEnd('.')})", memberPath, "declaration",
            cause.Replace("[Error] ", ""),
            $"Write it with {WriteTool} (path '{memberPath}'), or delete the member with {DeleteTool}.");

    // Parameters that do not apply to the requested element are an error, never silently ignored.
    private static string? Misfit(string kind, string? subtype, string? declaration, string? returnType,
        string? implLanguage, string? accessors, string? implementation = null)
    {
        string where = subtype is null ? $"kind={kind}" : $"kind={kind} (subtype {subtype})";
        string? Reject(string parameter, string why) => $"[Error] {parameter} does not apply to {where}: {why}.";

        if (implementation is not null && !TakesImplementation(kind, subtype))
            return Reject("implementation", kind == PlcKind.Member && subtype == "PROPERTY"
                ? "a PROPERTY's code is in its Get/Set accessors; write those"
                : "only a POU and its METHOD, ACTION, TRANSITION, GET and SET have an implementation");

        if (declaration is not null)
        {
            if (kind == PlcKind.Folder) return Reject("declaration", "a folder has no declaration");
            if (Array.IndexOf(ContentlessKinds, kind) >= 0) return Reject("declaration", "this element has no declaration");
            if (kind == PlcKind.Member && subtype is "ACTION" or "TRANSITION")
                return Reject("declaration", $"a {subtype} has only an implementation");
        }
        if (returnType is not null)
        {
            bool ok = (kind == PlcKind.Pou && subtype == "FUNCTION")
                   || (kind == PlcKind.Member && subtype is "METHOD" or "PROPERTY");
            if (!ok) return Reject("returnType", "only a FUNCTION, a METHOD and a PROPERTY return a value");
        }
        if (implLanguage is not null && kind is not (PlcKind.Pou or PlcKind.Member))
            return Reject("implLanguage", "only a POU and its members have an implementation");
        if (implLanguage is not null && kind == PlcKind.Member && subtype == "TRANSITION")
            return Reject("implLanguage", "a transition is always created in ST");
        if (accessors is not null && !(kind == PlcKind.Member && subtype == "PROPERTY"))
            return Reject("accessors", "only a PROPERTY has Get/Set accessors");
        return null;
    }

    private static bool TakesImplementation(string kind, string? subtype) =>
        kind == PlcKind.Pou || (kind == PlcKind.Member && subtype is "METHOD" or "ACTION" or "TRANSITION" or "GET" or "SET");

    // Tree path of an element Create just made: GET/SET accessors are always named Get/Set.
    private static string CreatedPath(string parentPath, string kind, string? subtype, string name) =>
        $"{parentPath.Trim()}^{(kind == PlcKind.Member && subtype is "GET" ? "Get" : kind == PlcKind.Member && subtype is "SET" ? "Set" : name.Trim())}";

    // ---- delete --------------------------------------------------------------

    public string Delete(string path, string kind, bool recursive = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return "[Error] path is required.";
        string? err = ParseKindAndSubtype(kind, null, "delete", out string k, out _);
        if (err is not null) return err;
        if (recursive && k != PlcKind.Folder)
            return $"[Error] recursive does not apply to kind={k}: only a FOLDER can be deleted with its contents.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        var (probeError, target) = ProbeKind(path, k);
        if (probeError is not null) return probeError;

        if (k != PlcKind.Folder)
            return new PouMembersCoordinator(session).DeletePlcObject(path);

        int? children = target!.Value.Node.ChildCount;
        if (!recursive)
        {
            if (children is null)
                return $"[Error] Cannot tell whether folder '{path}' is empty (its child count could not be read). " +
                       "Pass recursive=true to delete it with everything in it.";
            if (children > 0)
                return $"[Error] Folder '{path}' is not empty ({children} direct child{(children == 1 ? "" : "ren")}). " +
                       "Pass recursive=true to delete it with everything in it.";
        }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        try { session.DeletePlcFolder(path); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        var gone = PlcTreeReadBack.RequireMissing(session, path, $"Delete of '{path}'");
        if (gone is not null) return gone;
        return $"Deleted FOLDER '{path}' (read-back: the path no longer resolves). " +
               "(If anything in it was referenced elsewhere, run twincat_build_plc to check the project still builds.)";
    }

    // ---- rename --------------------------------------------------------------

    public string Rename(string path, string kind, string newName, bool updateReferences = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return "[Error] path is required.";
        if (string.IsNullOrWhiteSpace(newName)) return "[Error] newName is required.";
        string? err = ParseKindAndSubtype(kind, null, "rename", out string k, out _);
        if (err is not null) return err;

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        var (probeError, target) = ProbeKind(path, k);
        if (probeError is not null) return probeError;
        if (target!.Value.Entry.Subtype is "GET" or "SET")
            return $"[Error] '{path}' is a property accessor: Get/Set nodes have fixed names and cannot be renamed.";

        bool code = k is PlcKind.Pou or PlcKind.Member;
        if (code)
        {
            string? collision;
            try { collision = RenameCollision(path, k, newName.Trim()); }
            catch (Exception ex) { return McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message); }
            if (collision is not null)
                return $"[Error] '{path}' cannot be renamed to '{newName.Trim()}': {collision}. The XAE would accept it and only " +
                       "the next build fails. Nothing was changed.";
        }

        string result = new PouMembersCoordinator(session).RenamePlcObject(path, newName, folderName: k == PlcKind.Folder, updateReferences: updateReferences);
        return code && !result.StartsWith("[Error]", StringComparison.Ordinal) ? result + OldNameAssignments(path, k, newName.Trim()) : result;
    }

    // The new name against the element's own variables and, for a member, its POU's (see RenameChecks). An extra check:
    // a declaration that cannot be read is skipped (the rename goes ahead as before); a busy XAE is rethrown.
    private string? RenameCollision(string path, string kind, string newName)
    {
        string? Declaration(Func<PouBody> read)
        {
            try { return read().Declaration; }
            catch (Exception ex) when (ComCall.NotBusy(ex)) { return null; }
        }

        if (kind == PlcKind.Member)
        {
            string parent = path[..path.LastIndexOf('^')];
            return RenameChecks.Collision(newName, Declaration(() => session.ReadPouMember(path)), Declaration(() => session.ReadPou(parent)));
        }
        return RenameChecks.Collision(newName, Declaration(() => session.ReadPou(path)), null);
    }

    // Best effort after the rename: with the XAE's refactoring option off, "Old := …" (the return value of a METHOD or
    // FUNCTION) stays in the body and the next build fails.
    private string OldNameAssignments(string oldPath, string kind, string newName)
    {
        string oldName = oldPath[(oldPath.LastIndexOf('^') + 1)..];
        string newPath = oldPath[..(oldPath.LastIndexOf('^') + 1)] + newName;
        try
        {
            string body = (kind == PlcKind.Member ? session.ReadPouMember(newPath) : session.ReadPou(newPath)).Implementation;
            IReadOnlyList<int> lines = RenameChecks.AssignmentsTo(body, oldName);
            return lines.Count == 0 ? ""
                : $"\n[Warn] The body still assigns to the old name '{oldName}' (line {string.Join(", ", lines)}): rename those " +
                  $"return-value assignments to '{newName}' with twincat_plc_write, or the build fails.";
        }
        catch (Exception ex) when (!ComCall.IsFinal(ex)) { return ""; }
    }
}
