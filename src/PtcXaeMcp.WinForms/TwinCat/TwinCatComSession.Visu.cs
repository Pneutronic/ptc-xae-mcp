namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- plc-visu -------------------------------------------------------------------
    //
    // The content of a PLC visualization is not reachable through the Automation Interface (ProduceXml returns only
    // tree metadata). XAE takes a .TcVIS over deterministically only as an import: CreateChild(name, 58, null, path)
    // under the folder, after the old object was deleted. Verified live on 4026.22: the object id in the file is kept,
    // the file is copied verbatim and XAE's in-memory object equals it. Writing the project file in place instead races
    // XAE's file watcher (a save right after the write put the old content back) and raises a blocking "changed
    // outside" prompt when the editor is open.
    //
    // CreateChild's return value is not reliable here: for a file XAE cannot read as a visualization it returned the
    // parent folder and left a plain file node "<name>.TcVIS" (ItemType 506) behind. So the result is checked by
    // looking the new path up, and such a leftover is removed again.

    private const int TemplateImportSubType = 58;
    private const int PlainFileItemType = 506;

    public string ImportPlcObjectFile(string parentPath, string name, string filePath, int expectedItemType) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(parentPath)) throw new InvalidOperationException("parentPath is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("name is required.");
        if (!File.Exists(filePath)) throw new InvalidOperationException($"File not found: {filePath}");

        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic parent = LookupTreeItemWithFallback(sysMan, parentPath)
            ?? throw new InvalidOperationException($"PLC parent tree path not found: {parentPath}");

        string? createError = null;
        try { parent.CreateChild(name, TemplateImportSubType, null, filePath); }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { createError = Innermost(ex); }

        string newPath = parentPath + "^" + name;
        int? itemType = AfterMutation(() =>
        {
            dynamic? created = LookupTreeItemOrNull(sysMan, newPath);
            return created is null ? null : ReadItemTypeOrNull(created);
        });
        if (itemType == expectedItemType) return newPath;

        string leftover = RemoveImportLeftover(parent, name);
        throw new InvalidOperationException(
            $"XAE did not import '{filePath}' as '{newPath}' (ItemType {expectedItemType})" +
            (createError is null ? "" : $": {createError}") +
            (itemType is int it ? $"; the path holds ItemType {it}" : "; the path does not resolve") + leftover);
    }, OpenTimeout);

    // A file XAE could not read lands as a plain file node named like the file; remove it so the folder is as before.
    private static string RemoveImportLeftover(dynamic parent, string name)
    {
        string fileNode = name + ".TcVIS";
        int count = ChildCountOf(parent);
        for (int i = 1; i <= count; i++)
        {
            dynamic? child = ChildAt(parent, i);
            if (child is null || !string.Equals(NameOf(child), fileNode, StringComparison.OrdinalIgnoreCase)) continue;
            if (ReadItemTypeOrNull(child) != PlainFileItemType) continue;
            try
            {
                parent.DeleteChild(fileNode);
                return $". XAE had added a plain file node '{fileNode}'; it was removed again.";
            }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                return $". XAE added a plain file node '{fileNode}' that could not be removed ({Innermost(ex)}); delete it in the XAE.";
            }
        }
        return ".";
    }
}
