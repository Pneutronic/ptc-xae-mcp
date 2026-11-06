using System.Globalization;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

// twincat_plc_move. The Automation Interface has no move, and cut/paste would need the XAE in front. The route verified
// live (2026-11-05, TC 3.1.4026.22): PLCopen-export the top-level object (for a MEMBER: its POU/INTERFACE, a member alone
// cannot be exported), place it in the file's ProjectStructure (PlcOpenStructure), delete the original, import the file
// into the same PLC project. The object and every member keep their ObjectId (the GUID in the .TcPOU).
//
// Everything that can be refused is refused before the logout. From the delete on, a failure re-imports the original
// at its old place; the result never says "Moved" unless the read-back confirmed it.
internal sealed partial class PlcObjectCoordinator
{
    private static readonly string[] MovableKinds = [PlcKind.Pou, PlcKind.Interface, PlcKind.Dut, PlcKind.Gvl, PlcKind.Member];

    internal static string DefaultMoveDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ptc-xae-mcp", "move");

    /// <summary>Where twincat_plc_move keeps the exports (the original stays there for a manual restore).</summary>
    internal string MoveDirectory { get; init; } = DefaultMoveDirectory;

    /// <param name="Path">The element to move; <paramref name="NewPath"/> where it ends up.</param>
    /// <param name="CarrierPath">The top-level object that is exported, deleted and imported (the element, or a MEMBER's POU).</param>
    /// <param name="CarrierFolders">Its folder chain from the PLC project root now; <paramref name="TargetCarrierFolders"/> after the move.</param>
    private sealed record MovePlan(
        string Kind, string Path, string NewPath, int ItemType, string ProjectRoot,
        string CarrierPath, string CarrierName, string CarrierKind, PlcCatalogEntry CarrierEntry,
        IReadOnlyList<string> CarrierFolders, IReadOnlyList<string> TargetCarrierFolders, string NewCarrierPath,
        PlcOpenStructure.MemberMove? Member)
    {
        public string MapPath(string p) =>
            string.Equals(p, Path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(Path + "^", StringComparison.OrdinalIgnoreCase)
                ? NewPath + p[Path.Length..]
                : p;
    }

    /// <summary>The revision of the carrier and of each of its members (by path), and its folders.</summary>
    private sealed record MoveSnapshot(IReadOnlyDictionary<string, string> Revisions, IReadOnlySet<string> Folders);

    public string Move(string path, string kind, string targetFolderPath)
    {
        if (string.IsNullOrWhiteSpace(path)) return "[Error] path is required.";
        if (string.IsNullOrWhiteSpace(targetFolderPath))
            return "[Error] targetFolderPath is required: a FOLDER of the PLC project (or the PLC project node); " +
                   "for kind=MEMBER a folder inside its POU/INTERFACE, or the POU/INTERFACE itself.";
        string? err = ParseKindAndSubtype(kind, null, "move", out string k, out _);
        if (err is not null) return err;
        if (!MovableKinds.Contains(k))
            return $"[Error] kind {k} cannot be moved by twincat_plc_move: it moves a POU, INTERFACE, DUT, GVL or MEMBER " +
                   "(METHOD, ACTION, PROPERTY, TRANSITION).";
        path = path.Trim();
        string target = targetFolderPath.Trim().TrimEnd('^');

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        var (probeError, probed) = ProbeKind(path, k);
        if (probeError is not null) return probeError;

        MovePlan plan;
        MoveSnapshot before;
        try
        {
            var (planError, planned) = PlanMove(path, k, probed!.Value, target);
            if (planError is not null) return planError;
            plan = planned!;
        }
        catch (Exception ex) { return McpErrors.EnsureError(MoveMessage(ex)); }
        try { before = TakeSnapshot(plan.CarrierPath, plan.CarrierKind, plan.CarrierEntry); }
        catch (Exception ex)
        {
            return McpErrors.EnsureError($"Could not read '{plan.CarrierPath}' before the move: {MoveMessage(ex)}. Nothing was changed.");
        }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        return Execute(plan, before);
    }

    // ---- planning (no change) -----------------------------------------------

    private (string? Error, MovePlan? Plan) PlanMove(string path, string k, Target probed, string target)
    {
        var entry = probed.Entry;
        int itemType = probed.Node.ItemType!.Value;
        if (k == PlcKind.Member && entry.Subtype is "GET" or "SET")
            return ($"[Error] '{path}' is a property accessor ({entry.Subtype}): it belongs to its PROPERTY. Move the PROPERTY instead.", null);

        var ancestors = AncestorsOf(path);
        int rootIndex = ancestors.FindIndex(a => a.Node.ItemType == PlcItemType.PlcApp);
        if (rootIndex < 0)
            return ($"[Error] Cannot find the PLC project that holds '{path}' (no parent node is a PLC project).", null);
        string root = ancestors[rootIndex].Path;
        string parent = ancestors[0].Path;
        string name = Leaf(path);

        string carrierPath = path;
        PlcCatalogEntry carrierEntry = entry;
        if (k == PlcKind.Member)
        {
            int ownerIndex = ancestors.FindIndex(a => a.Node.ItemType is int t && MemberParents.Contains(t));
            if (ownerIndex < 0 || ownerIndex > rootIndex)
                return ($"[Error] '{path}' is not inside a POU or INTERFACE.", null);
            if (ancestors.Take(ownerIndex).Any(a => a.Node.ItemType != PlcItemType.Folder))
                return ($"[Error] '{path}' is not a member of a POU or INTERFACE (it sits below '{parent}', which is not a folder). " +
                        "Only a METHOD, ACTION, PROPERTY or TRANSITION of a POU/INTERFACE can be moved.", null);
            carrierPath = ancestors[ownerIndex].Path;
            carrierEntry = PlcElementCatalog.ForItemType(ancestors[ownerIndex].Node.ItemType!.Value)!;
        }
        string carrierName = Leaf(carrierPath);

        // The carrier's own place: a chain of folders below the PLC project.
        var carrierChain = ancestors.Where(a => a.Path.Length > root.Length && a.Path.Length < carrierPath.Length).ToList();
        if (carrierChain.Any(a => a.Node.ItemType != PlcItemType.Folder))
            return ($"[Error] '{carrierPath}' does not sit in a folder chain of the PLC project '{root}'; it cannot be moved.", null);
        var carrierFolders = FoldersBetween(root, carrierPath);

        PlcNodeInfo targetNode = session.ProbePlcNode(target);
        if (!targetNode.Exists)
            return ($"[Error] targetFolderPath not found: {target}. Create the folder first (twincat_plc_create, kind=FOLDER). " +
                    "Nothing was changed.", null);

        IReadOnlyList<string> targetCarrierFolders = carrierFolders;
        string newCarrierPath = carrierPath;
        PlcOpenStructure.MemberMove? member = null;
        if (k == PlcKind.Member)
        {
            bool toOwner = SamePath(target, carrierPath);
            if (!toOwner && !target.StartsWith(carrierPath + "^", StringComparison.OrdinalIgnoreCase))
                return ($"[Error] A MEMBER moves only inside its own POU/INTERFACE ('{carrierPath}'); '{target}' is outside it. " +
                        "Moving a member to another POU is not supported. Nothing was changed.", null);
            if (!toOwner && targetNode.ItemType != PlcItemType.Folder)
                return ($"[Error] '{target}' is not a FOLDER{ItemTypeText(targetNode)}. A member moves into a folder of its POU/INTERFACE, " +
                        "or into the POU/INTERFACE itself. Nothing was changed.", null);
            var memberFolders = toOwner ? new List<string>() : FoldersBetween(carrierPath, target).Append(Leaf(target)).ToList();
            string? chainError = RequireFolderChain(carrierPath, memberFolders, target);
            if (chainError is not null) return (chainError, null);
            member = new PlcOpenStructure.MemberMove(name, memberFolders);
        }
        else
        {
            bool toRoot = SamePath(target, root);
            if (!toRoot && !target.StartsWith(root + "^", StringComparison.OrdinalIgnoreCase))
                return ($"[Error] '{target}' is not in the PLC project of '{path}' ('{root}'). Moving between PLC projects is not supported. " +
                        "Nothing was changed.", null);
            if (!toRoot && targetNode.ItemType != PlcItemType.Folder)
                return ($"[Error] '{target}' is not a FOLDER{ItemTypeText(targetNode)}. A {k} moves into a folder of the PLC project, " +
                        "or to the PLC project node. Nothing was changed.", null);
            var folders = toRoot ? new List<string>() : FoldersBetween(root, target).Append(Leaf(target)).ToList();
            string? chainError = RequireFolderChain(root, folders, target);
            if (chainError is not null) return (chainError, null);
            targetCarrierFolders = folders;
            newCarrierPath = $"{target}^{carrierName}";
        }

        if (SamePath(target, parent))
            return ($"[Error] '{path}' is already in '{target}'. Nothing was changed.", null);

        string newPath = $"{target}^{name}";
        if (session.ProbePlcNode(newPath).Exists)
            return ($"[Error] '{target}' already holds an element named '{name}' ('{newPath}'). Nothing was changed.", null);

        // The PLCopen selector separates folders with '.', so a folder name with a dot cannot be addressed.
        string? dotted = carrierFolders.Concat(targetCarrierFolders).FirstOrDefault(f => f.Contains('.', StringComparison.Ordinal));
        if (dotted is not null)
            return ($"[Error] The folder '{dotted}' has a '.' in its name: the PLCopen export addresses an object by its folders " +
                    "separated with '.', so it cannot be moved from or into that folder. Rename the folder first. Nothing was changed.", null);

        return (null, new MovePlan(k, path, newPath, itemType, root, carrierPath, carrierName, carrierEntry.Kind!, carrierEntry,
            carrierFolders, targetCarrierFolders, newCarrierPath, member));
    }

    // Parents of path, nearest first, up to and including the PLC project node.
    private List<(string Path, PlcNodeInfo Node)> AncestorsOf(string path)
    {
        var list = new List<(string, PlcNodeInfo)>();
        for (int caret = path.LastIndexOf('^'); caret > 0; caret = path.LastIndexOf('^', caret - 1))
        {
            string p = path[..caret];
            var node = session.ProbePlcNode(p);
            list.Add((p, node));
            if (node.ItemType == PlcItemType.PlcApp) break;
        }
        return list;
    }

    // Every step of root^f1^…^fn must be a folder (a folder inside a POU, below the project root, holds members only).
    private string? RequireFolderChain(string root, IReadOnlyList<string> folders, string target)
    {
        string p = root;
        for (int i = 0; i < folders.Count - 1; i++)
        {
            p = $"{p}^{folders[i]}";
            var node = session.ProbePlcNode(p);
            if (node.ItemType != PlcItemType.Folder)
                return $"[Error] '{target}' lies below '{p}', which is not a folder{ItemTypeText(node)}" +
                       (node.ItemType is int t && MemberParents.Contains(t) ? " (a folder inside a POU/INTERFACE holds its members only)" : "") +
                       ". Nothing was changed.";
        }
        return null;
    }

    private static List<string> FoldersBetween(string root, string path)
    {
        if (path.Length <= root.Length + 1) return [];
        var segments = path[(root.Length + 1)..].Split('^');
        return segments.Take(segments.Length - 1).ToList();
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('^') + 1)..];

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string ItemTypeText(PlcNodeInfo node)
    {
        if (node.ItemType is not int t) return " (its ItemType could not be read)";
        string? kind = PlcElementCatalog.ForItemType(t)?.Kind;
        return kind is null
            ? string.Create(CultureInfo.InvariantCulture, $" (ItemType {t})")
            : $" but {PlcKindResolver.WithArticle(kind)}";
    }

    private static string Selector(IReadOnlyList<string> folders, string name) => string.Join(".", folders.Append(name));

    private static string MoveMessage(Exception ex) => ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message;

    // ---- snapshot -----------------------------------------------------------

    private MoveSnapshot TakeSnapshot(string carrierPath, string carrierKind, PlcCatalogEntry carrierEntry)
    {
        var revisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [carrierPath] = ReadContent(carrierPath, carrierKind, carrierEntry).Revision,
        };
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (carrierKind is PlcKind.Pou or PlcKind.Interface)
        {
            foreach (var m in session.ListPouElements(carrierPath))
            {
                if (m.Kind == PlcKind.Folder) { folders.Add(m.Path); continue; }
                var body = session.ReadPouMember(m.Path);
                revisions[m.Path] = PlcRevision.Of(body.Declaration, body.Implementation);
            }
        }
        return new MoveSnapshot(revisions, folders);
    }

    // Null when every element of before is in after (at map(path)) with the same revision and nothing else is.
    private static string? CompareSnapshots(MoveSnapshot before, MoveSnapshot after, Func<string, string> map, out List<string> lostFolders)
    {
        var problems = new List<string>();
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, revision) in before.Revisions)
        {
            string q = map(p);
            expected.Add(q);
            if (!after.Revisions.TryGetValue(q, out string? now)) problems.Add($"'{q}' is missing");
            else if (!PlcRevision.Matches(revision, now)) problems.Add($"'{q}' has revision {now}, was {revision}");
        }
        problems.AddRange(after.Revisions.Keys.Where(q => !expected.Contains(q)).Select(q => $"'{q}' is new"));
        lostFolders = before.Folders.Select(map).Where(f => !after.Folders.Contains(f)).ToList();
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    // ---- execution (after the logout) ----------------------------------------

    private string Execute(MovePlan plan, MoveSnapshot before)
    {
        string stem = $"{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}_{plan.CarrierName}";
        string exportFile = Path.Combine(MoveDirectory, stem + ".xml");
        string movedFile = Path.Combine(MoveDirectory, stem + ".moved.xml");
        string restoreFile = Path.Combine(MoveDirectory, stem + ".restore.xml");
        string checkFile = Path.Combine(MoveDirectory, stem + ".check.xml");
        string nothing = "Nothing was changed.";

        try { Directory.CreateDirectory(MoveDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[Error] Cannot create the folder for the export ({MoveDirectory}): {ex.Message}. {nothing}";
        }

        try { session.PlcOpenExportFrom(plan.ProjectRoot, exportFile, Selector(plan.CarrierFolders, plan.CarrierName)); }
        catch (Exception ex)
        {
            return McpErrors.EnsureError($"The PLCopen export of '{plan.CarrierPath}' failed: {MoveMessage(ex)}. {nothing}");
        }

        string original;
        bool bom;
        try { (original, bom) = ReadXml(exportFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[Error] Cannot read the export {exportFile}: {ex.Message}. {nothing}";
        }

        var (moved, movedError) = PlcOpenStructure.Rewrite(original, plan.CarrierName, plan.TargetCarrierFolders, plan.Member);
        var (restore, restoreError) = PlcOpenStructure.Rewrite(original, plan.CarrierName, plan.CarrierFolders);
        var (idsBefore, idsError) = PlcOpenStructure.ObjectIds(original);
        string? xmlError = movedError ?? restoreError ?? idsError;
        if (xmlError is not null)
            return $"[Error] Cannot move '{plan.Path}': {xmlError}. {nothing} Export kept at {exportFile}";

        try
        {
            WriteXml(movedFile, moved!, bom);
            // An export carries no parent folders: imported unchanged it would land at the project root, so the restore
            // file puts the original back into its own folder chain.
            WriteXml(restoreFile, restore!, bom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[Error] Cannot write the import file in {MoveDirectory}: {ex.Message}. {nothing}";
        }

        // ---- from here on the project changes ----
        try { session.DeletePlcObject(plan.CarrierPath); }
        catch (Exception ex)
        {
            bool? still = Resolves(plan.CarrierPath);
            if (still == true)
                return McpErrors.EnsureError($"Deleting '{plan.CarrierPath}' before the re-import failed: {MoveMessage(ex)}. " +
                                             $"The element was not moved; {nothing} Export kept at {exportFile}");
            if (still is null)
                return Unconfirmed(plan, $"deleting '{plan.CarrierPath}' failed ({MoveMessage(ex)})", restoreFile, exportFile);
            return Restore(plan, before, restoreFile, exportFile, $"the delete of '{plan.CarrierPath}' reported an error ({MoveMessage(ex)})");
        }

        bool? remains = Resolves(plan.CarrierPath);
        if (remains == true)
            return $"[Error] '{plan.CarrierPath}' still resolves after the delete (independent re-lookup); nothing was imported, " +
                   $"the element was not moved. Check the tree in the XAE (twincat_plc_list). Export kept at {exportFile}";
        if (remains is null)
            return Unconfirmed(plan, $"the read-back after deleting '{plan.CarrierPath}' failed", restoreFile, exportFile);

        try { session.PlcOpenImportInto(plan.ProjectRoot, movedFile); }
        catch (Exception ex)
        {
            return Restore(plan, before, restoreFile, exportFile, $"the import into '{plan.NewCarrierPath}' failed ({MoveMessage(ex)})");
        }

        string? failure = Verify(plan, before, idsBefore!, checkFile, out string notes);
        if (failure is not null) return Restore(plan, before, restoreFile, exportFile, failure);

        int members = before.Revisions.Count - 1;
        return $"Moved {plan.Kind} '{plan.Path}' to '{plan.NewPath}' (object GUID kept, {members} member{(members == 1 ? "" : "s")}, " +
               $"verified by read-back).{notes}\nExport of the original: {exportFile}";
    }

    // Null when the move landed: new path present with the same ItemType, old path gone, every text and every ObjectId unchanged.
    private string? Verify(MovePlan plan, MoveSnapshot before, IReadOnlyDictionary<string, string> idsBefore, string checkFile, out string notes)
    {
        notes = "";
        PlcNodeInfo landed;
        try { landed = session.ProbePlcNode(plan.NewPath); }
        catch (Exception ex) { return $"the read-back of '{plan.NewPath}' failed ({MoveMessage(ex)})"; }
        if (!landed.Exists) return $"'{plan.NewPath}' does not resolve after the import";
        // Kind and subtype, not the raw number: an interface member may enumerate under the POU member's ItemType.
        var landedEntry = landed.ItemType is int lt ? PlcElementCatalog.ForItemType(lt) : null;
        var wantedEntry = PlcElementCatalog.ForItemType(plan.ItemType);
        if (landedEntry is null || wantedEntry is null || landedEntry.Kind != wantedEntry.Kind || landedEntry.Subtype != wantedEntry.Subtype)
            return $"'{plan.NewPath}' resolves after the import, but not as the moved element{ItemTypeText(landed)} (expected ItemType " +
                   $"{plan.ItemType.ToString(CultureInfo.InvariantCulture)})";
        bool? old = Resolves(plan.Path);
        if (old != false)
            return old is null ? $"the read-back of the old path '{plan.Path}' failed" : $"the old path '{plan.Path}' still resolves after the import";

        MoveSnapshot after;
        try { after = TakeSnapshot(plan.NewCarrierPath, plan.CarrierKind, plan.CarrierEntry); }
        catch (Exception ex) { return $"reading '{plan.NewCarrierPath}' after the import failed ({MoveMessage(ex)})"; }
        string? diff = CompareSnapshots(before, after, plan.MapPath, out var lostFolders);
        if (diff is not null) return $"the read-back after the import differs from before: {diff}";

        string checkXml;
        try
        {
            session.PlcOpenExportFrom(plan.ProjectRoot, checkFile, Selector(plan.TargetCarrierFolders, plan.CarrierName));
            checkXml = ReadXml(checkFile).Text;
        }
        catch (Exception ex) { return $"the export of '{plan.NewCarrierPath}' to compare the object GUIDs failed ({MoveMessage(ex)})"; }
        var (idsAfter, idsError) = PlcOpenStructure.ObjectIds(checkXml);
        if (idsError is not null) return $"the object GUIDs after the import cannot be read: {idsError}";
        string? idDiff = PlcOpenStructure.CompareIds(idsBefore, idsAfter!);
        if (idDiff is not null) return $"the object GUIDs changed: {idDiff}";

        if (lostFolders.Count > 0)
            notes = $"\n[Warn] Folder{(lostFolders.Count == 1 ? "" : "s")} {string.Join(", ", lostFolders.Select(f => $"'{f}'"))} did not come " +
                    "back with the import (an empty folder may be missing from the PLCopen export); recreate with twincat_plc_create (kind=FOLDER).";
        return null;
    }

    // After the original was deleted: put the original back where it was and say exactly what happened.
    private string Restore(MovePlan plan, MoveSnapshot before, string restoreFile, string exportFile, string cause)
    {
        string head = $"[Error] Moving {plan.Kind} '{plan.Path}' to '{plan.NewPath}' failed after the original was deleted: {cause}.";
        string files = $" Original export: {exportFile}; the same placed in its old folder: {restoreFile}";

        // A copy the import did place would collide with the re-import of the original.
        var removed = new List<string>();
        foreach (string p in new[] { plan.NewCarrierPath, plan.CarrierPath }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Resolves(p) != true) continue;
            try { session.DeletePlcObject(p); removed.Add(p); }
            catch (Exception ex)
            {
                return $"{head} Restore stopped: the imported copy at '{p}' could not be removed ({MoveMessage(ex)}); it is in the " +
                       $"project as imported. Remove it and import {restoreFile} with twincat_plcopen_import to get the original back.{files}";
            }
        }
        string removedText = removed.Count == 0 ? "" : $" Removed the imported copy ('{string.Join("', '", removed)}').";

        try { session.PlcOpenImportInto(plan.ProjectRoot, restoreFile); }
        catch (Exception ex)
        {
            return $"{head}{removedText} Restore FAILED: re-importing the original failed ({MoveMessage(ex)}). '{plan.CarrierPath}' is NOT " +
                   $"in the project now — import {restoreFile} with twincat_plcopen_import.{files}";
        }

        MoveSnapshot again;
        try { again = TakeSnapshot(plan.CarrierPath, plan.CarrierKind, plan.CarrierEntry); }
        catch (Exception ex)
        {
            return $"{head}{removedText} Re-imported the original, but '{plan.CarrierPath}' cannot be read back ({MoveMessage(ex)}): " +
                   $"check the tree in the XAE (twincat_plc_list).{files}";
        }
        string? diff = CompareSnapshots(before, again, p => p, out _);
        return diff is null
            ? $"{head}{removedText} Restored: '{plan.Path}' is back where it was, its text unchanged (read back).{files}"
            : $"{head}{removedText} Re-imported the original at '{plan.CarrierPath}', but the read-back differs from before: {diff}.{files}";
    }

    private static string Unconfirmed(MovePlan plan, string cause, string restoreFile, string exportFile) =>
        $"[Error] Moving {plan.Kind} '{plan.Path}' stopped: {cause}, so it is not known whether '{plan.CarrierPath}' is still in " +
        $"the project. Nothing was imported. Check the tree (twincat_plc_list); if it is gone, import {restoreFile} with " +
        $"twincat_plcopen_import to put it back. Original export: {exportFile}";

    // True/false when the path does (not) resolve, null when the lookup failed (busy or blocked included: after the
    // delete, an exception escaping here would hide what was already changed).
    private bool? Resolves(string path)
    {
        try { return session.ProbePlcNode(path).Exists; }
        catch (Exception) { return null; }
    }

    // The export is UTF-8; the rewritten file keeps whether it had a byte-order mark.
    private static (string Text, bool Bom) ReadXml(string file)
    {
        byte[] bytes = File.ReadAllBytes(file);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    private static void WriteXml(string file, string text, bool bom) => File.WriteAllText(file, text, new UTF8Encoding(bom));
}
