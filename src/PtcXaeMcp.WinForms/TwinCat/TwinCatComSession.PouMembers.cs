using System.Globalization;
namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ===================================================================
    // Structured POU members & object lifecycle (feature/pou-handling).
    // nSubType / accessor shape verified empirically against the live XAE
    // on 2026-05-15 (see PlcItemType):
    //   ACTION 608 (vInfo {lang}), METHOD 609 (vInfo {lang, ret}),
    //   PROPERTY 611 (vInfo {lang, ret}) created BARE — Get 613 / Set 614
    //   are separate CreateChild calls on the property node.
    //   PlcOpenExport/Import live on ITcPlcOpenImportExport off the PLC
    //   source-project node; the selector is a ';'-separated string of
    //   dotted object names relative to the project ("POUs.FB_Foo").
    // ===================================================================

    public IReadOnlyList<PouMemberEntry> ListPouMembers(string pouPath) => ListPouChildren(pouPath, includeFolders: false);

    // Same walk as ListPouMembers, plus the FOLDER nodes (Kind "FOLDER") a POU can group its members in
    // (TC >= 4024.14). twincat_plc_list shows them; the legacy list_pou_members must not (its counts would change).
    public IReadOnlyList<PouMemberEntry> ListPouElements(string pouPath) => ListPouChildren(pouPath, includeFolders: true);

    private IReadOnlyList<PouMemberEntry> ListPouChildren(string pouPath, bool includeFolders) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(pouPath))
            throw new InvalidOperationException("pouPath is required.");

        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic? pou = LookupTreeItemWithFallback(sysMan, pouPath)
            ?? throw new InvalidOperationException($"PLC tree path not found: {pouPath}");

        var members = new List<PouMemberEntry>();
        CollectMembers(pou, members, depth: 0, maxDepth: 4, includeFolders: includeFolders);
        return (IReadOnlyList<PouMemberEntry>)members;
    }, OpenTimeout);

    // Walk a POU's child nodes, keying off the verified nSubType (NOT the
    // localized display name — node names are German on this box, see
    // reference_tc3_4026_22_quirks). Property accessors (Get/Set) are nested
    // one level under the property node, so we recurse into PROPERTY.
    private static void CollectMembers(dynamic node, List<PouMemberEntry> sink, int depth, int maxDepth, bool includeFolders = false)
    {
        int childCount = 0;
        try { childCount = (int)node.ChildCount; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return; }

        for (int i = 1; i <= childCount; i++)
        {
            dynamic? child = null;
            try
            {
                try { child = node.Child(i); }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                if (child is null) continue;

                int itemType = -1;
                try { itemType = (int)child.ItemType; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

                string? kind = PlcMemberKind.FromItemType(itemType);
                if (kind is null && includeFolders && itemType == PlcItemType.Folder) kind = PlcKind.Folder;
                if (kind is not null)
                {
                    string name = string.Empty;
                    try { name = (string)child.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    string path = name;
                    try { path = (string)child.PathName ?? name; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

                    sink.Add(new PouMemberEntry(path, name, kind, itemType));
                }

                // Recurse into PROPERTY (Get/Set accessors) and into FOLDERs under the POU — a
                // member inside a folder carries the folder in its PathName, so it stays locatable.
                if (PlcMemberKind.ShouldDescendInto(itemType) && depth + 1 < maxDepth)
                    CollectMembers(child, sink, depth + 1, maxDepth, includeFolders);
            }
            finally
            {
                if (child is not null)
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(child); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }
    }

    public string AddPouMember(string pouPath, string kind, string name, string? returnType, string? implLanguage) =>
        AddPouMember(pouPath, kind, name, returnType, implLanguage, PropertyAccessors.Both);

    public string AddPouMember(string pouPath, string kind, string name, string? returnType,
        string? implLanguage, PropertyAccessors accessors) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic pou = LookupTreeItemWithFallback(sysMan, pouPath)
            ?? throw new InvalidOperationException($"PLC tree path not found: {pouPath}");

        // Interface members use DISTINCT nSubTypes + a different vInfo shape from
        // POU members (Infosys 242732427): METHOD 610 / PROPERTY 612 / GET 654 /
        // SET 655 with vInfo[0]=return type, vs POU 609/611/613/614 with a language
        // vInfo. Detect the parent kind once and route accordingly. The interface
        // subtype/vInfo shape is empirically gated by the integration suite.
        bool parentIsInterface = false;
        try { parentIsInterface = (int)pou.ItemType == PlcItemType.Interface; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        if (parentIsInterface && !PlcCodeClassification.MemberKindValidOnInterface(kind))
            throw new InvalidOperationException(
                $"Cannot add {kind} to INTERFACE '{pouPath}': interfaces declare only " +
                "METHODs and PROPERTYs (with GET/SET accessors), not ACTIONs.");

        // vInfo shapes verified empirically against the live XAE on
        // 2026-05-15 (TC3 4026.22). Elements are STRINGS, not boxed ints
        // (object[] of ints → "vInfo (Type: Object[]) is not supported").
        // Language is the numeric code as a string ("1"=ST … "6"=LD):
        //   * ACTION / Get / Set: a *scalar* string (a size-1 array is
        //     rejected: "vInfo (Type: String[]) is not supported").
        //   * PROPERTY: EXACTLY string[] { lang, returnType } — two elements
        //     (a third "PUBLIC" element yields a malformed declaration here).
        //   * METHOD (issue #19): a *scalar* language string only (the
        //     {lang, ret} array shape is separately rejected). Works on any FB
        //     incl. a freshly create_pou'd one; the only constraint is that the
        //     method name be a valid, non-reserved IEC 61131-3 identifier (see
        //     CreateMethodChild). Return type fixed via NormalizeMethodDeclaration.
        // Interface members (610/612/654/655) instead take the RETURN TYPE in
        // vInfo[0] (methods/properties) or a null vInfo (accessors), per Infosys.
        string langStr = PlcImplLanguage.ToCode(implLanguage).ToString(CultureInfo.InvariantCulture);
        string ret = string.IsNullOrWhiteSpace(returnType) ? "BOOL" : returnType!.Trim();

        switch (kind)
        {
            case PlcMemberKind.Method:
            {
                var m = parentIsInterface
                    ? CreateInterfaceMethodChild(sysMan!, pou, name, ret, pouPath)
                    : CreateMethodChild(sysMan!, pou, name, langStr, ret, pouPath);
                string mp = PathOf(m, pouPath, name);
                WaitMemberEnumerable(sysMan, pouPath, name,
                    parentIsInterface ? PlcItemType.InterfaceMethod : PlcItemType.Method);
                return mp;
            }
            case PlcMemberKind.Action:
            {
                // Scalar string, not an array. (POU-only — interfaces rejected above.)
                var a = CreateChildChecked(pou, name, PlcItemType.Action,
                    langStr, pouPath, "ACTION");
                string ap = PathOf(a, pouPath, name);
                WaitMemberEnumerable(sysMan, pouPath, name, PlcItemType.Action);
                return ap;
            }
            case PlcMemberKind.Property:
            {
                // PROPERTY is created bare; XAE does NOT auto-generate Get/Set
                // (verified 2026-05-15). Create the property, then both
                // accessors, so the member is build-usable in one call.
                // Interface property: subtype 612, vInfo[0]=return type, and the
                // accessors are 654/655 with a null vInfo (Infosys 242732427).
                int propType = PlcCodeClassification.MemberSubType(parentIsInterface, PlcMemberKind.Property);
                int getType  = PlcCodeClassification.MemberSubType(parentIsInterface, PlcMemberKind.Get);
                int setType  = PlcCodeClassification.MemberSubType(parentIsInterface, PlcMemberKind.Set);
                object? propInfo = parentIsInterface ? ret : new[] { langStr, ret };
                object? accInfo  = parentIsInterface ? null : (object?)langStr;
                string propLabel = parentIsInterface ? "INTERFACE PROPERTY" : "PROPERTY";

                var prop = CreateChildChecked(pou, name, propType, propInfo, pouPath, propLabel);
                string propPath = PathOf(prop, pouPath, name);
                WaitMemberEnumerable(sysMan, pouPath, name, propType);
                // Creating both accessors unconditionally used to be the only
                // option — on an INTERFACE that forces every implementer to
                // write a Set even for a read-only property. accessors narrows
                // this to just the requested side(s).
                bool wantGet = accessors is PropertyAccessors.Get or PropertyAccessors.Both;
                bool wantSet = accessors is PropertyAccessors.Set or PropertyAccessors.Both;
                // B-16 (issue #152): the property exists from here on. An accessor failure must say
                // so — and must not escape to the outer busy retry, which would replay the property
                // CreateChild ("already exists") — so the accessor steps run in AfterMutation.
                var createdAccessors = new List<string>();
                try
                {
                    if (wantGet)
                    {
                        AfterMutation(() => (object)CreateChildChecked(prop, "Get", getType, accInfo, propPath, propLabel + " Get"));
                        WaitMemberEnumerable(sysMan, propPath, "Get", getType);
                        createdAccessors.Add("Get");
                    }
                    if (wantSet)
                    {
                        AfterMutation(() => (object)CreateChildChecked(prop, "Set", setType, accInfo, propPath, propLabel + " Set"));
                        WaitMemberEnumerable(sysMan, propPath, "Set", setType);
                        createdAccessors.Add("Set");
                    }
                }
                // Busy cannot reach here un-final: AfterMutation retries it and ends in XaeBlockedException.
                catch (Exception ex)
                {
                    string missing = string.Join("/", new[] { wantGet ? "Get" : null, wantSet ? "Set" : null }
                        .Where(a => a is not null && !createdAccessors.Contains(a)));
                    throw new InvalidOperationException(PartialCreate.Describe(
                        $"{propLabel} '{name}'", propPath, $"{missing} accessor", ex.Message,
                        (createdAccessors.Count > 0 ? $"Created accessors: {string.Join("/", createdAccessors)}. " : "") +
                        $"Add the missing accessor with twincat_plc_create (kind=MEMBER, subtype={missing.Split('/')[0].ToUpperInvariant()}, " +
                        $"pouPath '{propPath}')."), ex);
                }
                // list_pou_members walks from the POU and recurses into the
                // property; that separate COM call can still see the property
                // with zero children for a moment after the accessors settle
                // under propPath. Poll the POU-rooted view until the requested
                // accessor(s) are visible there too, so a follow-up list sees them.
                if (wantGet || wantSet)
                    WaitPropertyAccessorsVisible(sysMan, pouPath, name, wantGet && wantSet ? 2 : 1);
                return propPath;
            }
            case PlcMemberKind.Get:
            {
                int getType = PlcCodeClassification.MemberSubType(parentIsInterface, PlcMemberKind.Get);
                object? accInfo = parentIsInterface ? null : (object?)langStr;
                var g = CreateChildChecked(pou, "Get", getType, accInfo, pouPath, "PROPERTY Get");
                string gp = PathOf(g, pouPath, "Get");
                WaitMemberEnumerable(sysMan, pouPath, "Get", getType);
                return gp;
            }
            case PlcMemberKind.Set:
            {
                int setType = PlcCodeClassification.MemberSubType(parentIsInterface, PlcMemberKind.Set);
                object? accInfo = parentIsInterface ? null : (object?)langStr;
                var s = CreateChildChecked(pou, "Set", setType, accInfo, pouPath, "PROPERTY Set");
                string sp = PathOf(s, pouPath, "Set");
                WaitMemberEnumerable(sysMan, pouPath, "Set", setType);
                return sp;
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported member kind '{kind}'. Expected METHOD, ACTION, PROPERTY, GET, or SET.");
        }
    }, OpenTimeout);

    // After CreateChild the new member is not always immediately visible to a
    // *separate* COM call (the next tool's tree walk) while XAE registers it —
    // the same settle lag behind the METHOD/CreatePou quirks. Poll a freshly
    // re-resolved parent until the child appears by name + nSubType so
    // add_pou_member only returns once list/read_pou_member can see it.
    // The settle polls below run after a CreateChild: busy there is the normal settle lag and
    // is retried locally (AfterMutation), never replayed by the caller's retry scope.
    private static void WaitMemberEnumerable(object sysMan, string parentPath,
        string childName, int expectedItemType) =>
        AfterMutation<object?>(() => { WaitMemberEnumerableCore(sysMan, parentPath, childName, expectedItemType); return null; });

    private static void WaitMemberEnumerableCore(object sysMan, string parentPath,
        string childName, int expectedItemType)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            dynamic? parent = LookupTreeItemWithFallback(sysMan, parentPath);
            if (parent is not null)
            {
                int cc = 0; try { cc = (int)parent.ChildCount; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                for (int i = 1; i <= cc; i++)
                {
                    dynamic? c; try { c = parent.Child(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                    if (c is null) continue;
                    string nm = string.Empty;
                    try { nm = (string)c.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    if (!string.Equals(nm, childName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int it = -1; try { it = (int)c.ItemType; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    if (it == expectedItemType) return;
                    // An interface member may enumerate under either the interface
                    // subtype (610/612/654/655) or the equivalent POU subtype after
                    // TC3 settles; accept either when they map to the same kind so we
                    // don't burn the full timeout on a benign subtype difference.
                    string? wantKind = PlcMemberKind.FromItemType(expectedItemType);
                    if (wantKind is not null && PlcMemberKind.FromItemType(it) == wantKind) return;
                }
            }
            if (DateTime.UtcNow >= deadline) return; // best-effort; don't hard-fail the create
            Thread.Sleep(TimeSpan.FromMilliseconds(400));
        }
    }

    // Poll the POU-rooted view (re-resolving the POU each time, exactly as
    // list_pou_members does) until the named PROPERTY shows >= 2 children
    // (its Get + Set). Best-effort: returns on timeout rather than failing the
    // add — the accessors do exist (already confirmed under propPath), this
    // only smooths the eventual-consistency window for an immediate list.
    private static void WaitPropertyAccessorsVisible(object sysMan, string pouPath, string propName, int expectedAccessorCount) =>
        AfterMutation<object?>(() => { WaitPropertyAccessorsVisibleCore(sysMan, pouPath, propName, expectedAccessorCount); return null; });

    private static void WaitPropertyAccessorsVisibleCore(object sysMan, string pouPath, string propName, int expectedAccessorCount)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            dynamic? pou = LookupTreeItemWithFallback(sysMan, pouPath);
            if (pou is not null)
            {
                int cc = 0; try { cc = (int)pou.ChildCount; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                for (int i = 1; i <= cc; i++)
                {
                    dynamic? c; try { c = pou.Child(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                    if (c is null) continue;
                    string nm = string.Empty;
                    try { nm = (string)c.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    int it = -1; try { it = (int)c.ItemType; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    if (it != PlcItemType.Property ||
                        !string.Equals(nm, propName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int gc = 0; try { gc = (int)c.ChildCount; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    if (gc >= expectedAccessorCount) return;
                }
            }
            if (DateTime.UtcNow >= deadline) return;
            Thread.Sleep(TimeSpan.FromMilliseconds(400));
        }
    }

    // Issue #19 — actual root cause (established live 2026-05-19, TC3 4026.22):
    //
    // METHOD add works on a freshly twincat_create_pou'd FB. The earlier
    // "...not possible on node (Name mismatch)" / build-time C0243 ("name in
    // signature not identical to object name") was NOT a platform/FB-state
    // defect — it was caused by passing a method name that is an IEC 61131-3
    // reserved word (e.g. CALC, ADD — IL operators). TC3's CreateChild surfaces
    // a reserved/invalid sub-object identifier as "Name mismatch" (the XAE GUI
    // shows the clearer "Der Name muss ein gültiger IEC 61131-3-Bezeichner
    // sein"). With a valid, non-reserved name a METHOD is created, compiles,
    // and runs (verified end-to-end with a build-green integration scenario).
    //
    // vInfo: the documented scalar-language string (Infosys 242732427: METHOD
    // vInfo = [0]lang [1]ret [2]access [3]PLCopen, all optional; the {lang,ret}
    // ARRAY shape is rejected). TC3 puts the language code in the return-type
    // slot (`METHOD <name> : 1`), so NormalizeMethodDeclaration rewrites the
    // header to the requested return type.
    private dynamic CreateMethodChild(object sysMan, dynamic pou, string name,
        string langStr, string ret, string pouPath)
    {
        dynamic created;
        try { created = pou.CreateChild(name, PlcItemType.Method, "", langStr); }
        catch (Exception ex) when (
            ex.Message.Contains("Name mismatch", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot add METHOD '{name}' to '{pouPath}': TC3 rejected the name " +
                "(\"Name mismatch\"). The method name must be a valid IEC 61131-3 " +
                "identifier and must not be a reserved word — IL operators such as " +
                "ADD, CAL, CALC, SUB, MUL, DIV, LD, ST, JMP, RET are reserved and " +
                "fail this way. Choose a different method name.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"CreateChild failed for METHOD '{name}' under '{pouPath}': {ex.Message}", ex);
        }
        if (created is null)
            throw new InvalidOperationException(
                $"CreateChild returned null for METHOD '{name}' under '{pouPath}'.");

        AfterMutation<object?>(() => { NormalizeMethodDeclaration(sysMan, created, name, ret, pouPath); return null; });
        return created;
    }

    // Interface METHOD (nSubType 610): unlike the POU method (609, scalar language
    // vInfo) the interface method takes the RETURN TYPE in vInfo[0] (Infosys
    // 242732427). Interface methods are declaration-only — no implementation body.
    // Same reserved-word "Name mismatch" surface as CreateMethodChild, and we still
    // normalise the header so the declaration reads `METHOD <name> : <ret>` even if
    // TC3 echoes the vInfo back into the return slot.
    private dynamic CreateInterfaceMethodChild(object sysMan, dynamic itf, string name, string ret, string itfPath)
    {
        dynamic created;
        try { created = itf.CreateChild(name, PlcItemType.InterfaceMethod, "", ret); }
        catch (Exception ex) when (
            ex.Message.Contains("Name mismatch", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot add METHOD '{name}' to interface '{itfPath}': TC3 rejected the name " +
                "(\"Name mismatch\"). The method name must be a valid IEC 61131-3 " +
                "identifier and must not be a reserved word.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"CreateChild failed for interface METHOD '{name}' under '{itfPath}' " +
                $"(nSubType {PlcItemType.InterfaceMethod}): {ex.Message}", ex);
        }
        if (created is null)
            throw new InvalidOperationException(
                $"CreateChild returned null for interface METHOD '{name}' under '{itfPath}'.");

        AfterMutation<object?>(() => { NormalizeMethodDeclaration(sysMan, created, name, ret, itfPath); return null; });
        return created;
    }

    // A scalar-vInfo METHOD comes back declared `METHOD <name> : <langCode>`
    // (e.g. "... : 1"). Rewrite ONLY the header line to the requested return
    // type, keeping any VAR_INPUT/VAR blocks the caller may add later intact.
    // Issue #148 (B-7): no longer best-effort. The setter is checked and the header is
    // read back from a freshly re-resolved node; any failure throws
    // "METHOD '<name>' created at <path>, header NOT fixed: ..." so the add is never
    // reported as a success with `: 1` as the return type. The node itself stays (it exists
    // and is usable); the message says so.
    private static void NormalizeMethodDeclaration(object sysMan, dynamic methodNode, string name, string ret,
        string parentPath)
    {
        string methodPath = parentPath + "^" + name;
        string NotFixed(string why) =>
            $"METHOD '{name}' created at {methodPath}, header NOT fixed: {why} " +
            $"Fix the header manually (twincat_write_pou_member) to '{MethodHeader.Want(name, ret)}'.";

        string decl;
        try { decl = (string)methodNode.DeclarationText ?? string.Empty; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx))
        {
            throw new InvalidOperationException(NotFixed($"DeclarationText could not be read ({comEx.Message})."), comEx);
        }

        if (!MethodHeader.Matches(decl, name, ret))
        {
            string rebuilt = MethodHeader.Rewrite(decl, name, ret);
            try { methodNode.DeclarationText = rebuilt; }
            catch (Exception comEx) when (ComCall.NotBusy(comEx))
            {
                throw new InvalidOperationException(NotFixed($"setting DeclarationText failed ({comEx.Message})."), comEx);
            }
        }

        // B-21 (issue #152): verify on a re-resolved node only — never on methodNode itself.
        object? fresh = LookupTreeItemWithFallback(sysMan, methodPath);
        if (fresh is null)
            throw new InvalidOperationException(NotFixed(ReadBackNode.NotReResolved(methodPath)));
        dynamic verifyNode = fresh;
        string actual;
        try { actual = (string)verifyNode.DeclarationText ?? string.Empty; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx))
        {
            throw new InvalidOperationException(NotFixed($"read-back failed ({comEx.Message})."), comEx);
        }
        if (!MethodHeader.Matches(actual, name, ret))
            throw new InvalidOperationException(NotFixed(
                $"read-back shows '{MethodHeader.HeaderLine(actual) ?? "(no METHOD header)"}'."));
    }

    private static dynamic CreateChildChecked(dynamic parent, string name, int subType,
        object? vInfo, string parentPath, string label)
    {
        dynamic? created;
        try { created = parent.CreateChild(name, subType, "", vInfo); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"CreateChild failed for {label} '{name}' under '{parentPath}': {ex.Message}", ex);
        }
        if (created is null)
            throw new InvalidOperationException(
                $"CreateChild returned null for {label} '{name}' under '{parentPath}'.");
        return created;
    }


    private static string PathOf(dynamic node, string parentPath, string name)
    {
        string fallback = parentPath + "^" + name;
        object n = node;
        return AfterMutation(() => ComCall.ReadProp(() => (string?)((dynamic)n).PathName, null) ?? fallback);
    }

    public PouBody ReadPouMember(string memberPath) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(memberPath))
            throw new InvalidOperationException("memberPath is required.");

        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic? node = LookupTreeItemWithFallback(sysMan, memberPath)
            ?? throw new InvalidOperationException($"PLC member path not found: {memberPath}");

        // Member shapes differ: METHOD/GET/SET have BOTH a declaration and an
        // implementation; an ACTION has ONLY an implementation (no
        // ITcPlcDeclaration — reading it throws); a PROPERTY parent has only a
        // declaration. Treat both sections as best-effort so every member kind
        // reads successfully and the caller sees whichever parts exist.
        // The node was resolved, so it exists. A freshly-added ACTION has an
        // empty implementation and no declaration at all — that is valid, not
        // an error. Both sections are best-effort; empty content is returned
        // as-is (the caller already knows the member kind from list).
        string decl;
        try { decl = ReadDeclarationText(node, memberPath); }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { decl = string.Empty; }
        string impl;
        try { impl = ReadImplementationText(node, memberPath); }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { impl = string.Empty; }
        string lang = ReadImplementationLanguage(node) ?? "ST";
        return new PouBody(decl, impl, lang);
    }, OpenTimeout);

    public void WritePouMember(string memberPath, string? declaration, string? implementation) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(memberPath))
            throw new InvalidOperationException("memberPath is required.");
        if (declaration is null && implementation is null)
            throw new InvalidOperationException("At least one of declaration or implementation must be supplied.");

        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic? node = LookupTreeItemWithFallback(sysMan, memberPath)
            ?? throw new InvalidOperationException($"PLC member path not found: {memberPath}");

        if (declaration is not null)
        {
            try { node.DeclarationText = declaration; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Setting DeclarationText on '{memberPath}' failed: {ex.Message}.", ex);
            }
        }
        if (implementation is not null)
        {
            try { node.ImplementationText = implementation; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Setting ImplementationText on '{memberPath}' failed: {ex.Message}. " +
                    "A PROPERTY parent node has no implementation — target its Get/Set child instead.", ex);
            }
        }

        // Re-resolve the tree item instead of reusing `node` — see the matching
        // comment in TwinCatComSession.PlcCode.cs WritePouBody.
        dynamic verifyNode = ReadBackNode.Require(LookupTreeItemWithFallback(sysMan, memberPath), memberPath);
        if (declaration is not null)
        {
            string actual = ReadDeclarationText(verifyNode, memberPath);
            if (!BodiesMatch(declaration, actual))
                throw new InvalidOperationException(
                    $"DeclarationText verify failed on '{memberPath}'. " +
                    $"Expected (first 200): {Preview(declaration)} | Actual (first 200): {Preview(actual)}");
        }
        if (implementation is not null)
        {
            string actual = ReadImplementationText(verifyNode, memberPath);
            if (!BodiesMatch(implementation, actual))
                throw new InvalidOperationException(
                    $"ImplementationText verify failed on '{memberPath}'. " +
                    $"Expected (first 200): {Preview(implementation)} | Actual (first 200): {Preview(actual)}");
        }
    }, OpenTimeout);

    public PlcNodeInfo ProbePlcNode(string treePath) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(treePath)) return PlcNodeInfo.Missing;
        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic? node = LookupTreeItemWithFallback(sysMan, treePath);
        if (node is null) return PlcNodeInfo.Missing;
        return new PlcNodeInfo(true, ReadItemTypeOrNull(node), ReadChildCountOrNull(node));
    }, OpenTimeout);

    // Unreadable ItemType is "unknown", never a number that could pass a kind check (issue #148:
    // the old `catch { } → -1` let an unreadable node through the delete blacklist).
    private static int? ReadItemTypeOrNull(dynamic node)
    {
        try { return (int)node.ItemType; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    // Unreadable child count is "unknown", never "empty" — a folder delete must not mistake it for an empty folder.
    private static int? ReadChildCountOrNull(dynamic node)
    {
        try { return (int)node.ChildCount; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    public void DeletePlcObject(string treePath) => DeletePlcNode(treePath, PlcDeletePolicy.Check);

    // Folders are organisational nodes the old whitelist refuses; twincat_plc_delete (kind=FOLDER) removes
    // them through this path, which re-checks that the node really is a folder.
    public void DeletePlcFolder(string treePath) => DeletePlcNode(treePath, PlcDeletePolicy.CheckFolder);

    private void DeletePlcNode(string treePath, Func<string, PlcNodeInfo, string?> policy) => ComBusyRetry.Run<object?>(() =>
    {
        if (string.IsNullOrWhiteSpace(treePath))
            throw new InvalidOperationException("treePath is required.");

        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic? node = LookupTreeItemWithFallback(sysMan, treePath)
            ?? throw new InvalidOperationException($"PLC tree path not found: {treePath}");

        // Whitelist (issue #148, LIVE-8): re-checked here on the node that will actually be
        // deleted, independent of the coordinator's pre-logout check.
        var refusal = policy(treePath, new PlcNodeInfo(true, ReadItemTypeOrNull(node)));
        if (refusal is not null)
            throw new InvalidOperationException(refusal["[Error] ".Length..]);

        // The lookup has type-based fallbacks; DeleteChild(leaf) below acts on the parent's
        // child by NAME. Refuse when the checked node is not the one that name addresses.
        string? resolvedPath = ComCall.ReadProp(() => (string?)node.PathName, null);
        if (resolvedPath is not null && !string.Equals(resolvedPath, treePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to delete '{treePath}': the path resolved to a different node ('{resolvedPath}'). " +
                "Pass the exact tree path from twincat_plc_list.");

        int caret = treePath.LastIndexOf('^');
        if (caret <= 0)
            throw new InvalidOperationException(
                $"'{treePath}' has no parent — refusing to delete a top-level project node.");
        string parentPath = treePath[..caret];
        string leaf = treePath[(caret + 1)..];

        dynamic? parent = LookupTreeItemWithFallback(sysMan, parentPath)
            ?? throw new InvalidOperationException($"Parent tree path not found: {parentPath}");

        try { parent.DeleteChild(leaf); }
        // Busy is not "the parent rejects the name": it propagates instead of triggering the fallback.
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            // Fall back to the node's own Delete() if the parent rejects the name
            // (some XAE versions index members differently).
            try { node.Delete(); }
            catch (Exception ex2)
            {
                throw new InvalidOperationException(
                    $"Delete failed for '{treePath}': {ex.Message} / {ex2.Message}", ex2);
            }
        }
        return null;
    }, OpenTimeout);

    public PlcRenameResult RenamePlcObject(string treePath, string newName, bool updateReferences = false) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(treePath))
            throw new InvalidOperationException("treePath is required.");
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("newName is required.");

        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);
        dynamic? node = LookupTreeItemWithFallback(sysMan, treePath)
            ?? throw new InvalidOperationException($"PLC tree path not found: {treePath}");

        // The setter blocks while the XAE shows "Automatisches Umbenennen über Refactoring"; the watcher
        // (Win32 only, scoped to this XAE) answers it so the call returns.
        using var prompt = new RenameRefactoringWatcher(XaeWindowScope.For(TryGetXaeMainWindow(dte)), updateReferences);
        prompt.Start();
        try { node.Name = newName; }
        catch (Exception ex)
        {
            prompt.Stop();
            throw new InvalidOperationException(
                $"Rename failed for '{treePath}' -> '{newName}': {ex.Message}" +
                (prompt.Outcome is { } o ? $" (refactoring prompt: {o})" : ""), ex);
        }
        prompt.Stop();

        int caret = treePath.LastIndexOf('^');
        string fallbackPath = caret > 0 ? treePath[..(caret + 1)] + newName : newName;
        // After the rename a replay would look up the OLD path — keep busy local.
        string newPath = AfterMutation(() => ComCall.ReadProp(() => (string?)node.PathName, null) ?? fallbackPath);
        return new PlcRenameResult(newPath, prompt.Outcome, prompt.Failed);
    }, OpenTimeout);

    public string PlcOpenExport(string filePath, string selector) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException("filePath is required.");
        if (string.IsNullOrWhiteSpace(selector))
            throw new InvalidOperationException("selector is required (e.g. \"POUs.FB_Foo\").");

        dynamic dte = GetActiveTcDte();
        dynamic? plcProj = FindPlcSourceProjectNode(dte)
            ?? throw new InvalidOperationException("No PLC source project node found.");

        try { plcProj.PlcOpenExport(filePath, selector); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"PlcOpenExport('{filePath}', '{selector}') failed: {ex.Message}. " +
                "The selector must be ';'-separated dotted object names relative to the " +
                "PLC project, e.g. \"POUs.FB_Foo\".", ex);
        }

        if (!System.IO.File.Exists(filePath))
            throw new InvalidOperationException(
                $"PlcOpenExport reported success but '{filePath}' does not exist.");
        return filePath;
    }, OpenTimeout);

    public string PlcOpenImport(string filePath, int conflictMode) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException("filePath is required.");
        if (!System.IO.File.Exists(filePath))
            throw new InvalidOperationException($"PLCopen file not found: {filePath}");
        if (conflictMode is < 0 or > 3)
            throw new InvalidOperationException(
                $"conflictMode {conflictMode} out of range. Expected 0=None, 1=Rename, 2=Replace, 3=Skip.");

        dynamic dte = GetActiveTcDte();
        dynamic? plcProj = FindPlcSourceProjectNode(dte)
            ?? throw new InvalidOperationException("No PLC source project node found.");

        try { plcProj.PlcOpenImport(filePath, conflictMode); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"PlcOpenImport('{filePath}', {conflictMode}) failed: {ex.Message}", ex);
        }
        return filePath;
    }, OpenTimeout);
}
