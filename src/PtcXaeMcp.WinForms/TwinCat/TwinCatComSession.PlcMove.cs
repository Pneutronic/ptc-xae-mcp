using System.Globalization;
namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ===================================================================
    // twincat_plc_move: PLCopen export/import on a given PLC project node.
    // The legacy PlcOpenExport/PlcOpenImport work on the FIRST PLC project;
    // a move exports from and imports into the project that holds the
    // element. Verified live 2026-11-05 (TC 3.1.4026.22): the export of a
    // top-level object (a member alone is refused: "Couldn't create
    // ExportObjects"), PlcOpenImport(file, 0, "", true) on the PLC project
    // node (folder nodes do not offer it) places the object by the file's
    // ProjectStructure with its ObjectIds unchanged.
    // ===================================================================

    public void PlcOpenExportFrom(string plcProjectPath, string filePath, string selector) => ComBusyRetry.Run<object?>(() =>
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException("filePath is required.");
        if (string.IsNullOrWhiteSpace(selector))
            throw new InvalidOperationException("selector is required (e.g. \"POUs.FB_Foo\").");

        dynamic project = RequirePlcProjectNode(plcProjectPath);
        if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);

        try { project.PlcOpenExport(filePath, selector); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"PlcOpenExport('{filePath}', '{selector}') on '{plcProjectPath}' failed: {ex.Message}", ex);
        }

        if (!System.IO.File.Exists(filePath))
            throw new InvalidOperationException($"PlcOpenExport reported success but '{filePath}' does not exist.");
        return null;
    }, OpenTimeout);

    public void PlcOpenImportInto(string plcProjectPath, string filePath) => ComBusyRetry.Run<object?>(() =>
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException("filePath is required.");
        if (!System.IO.File.Exists(filePath))
            throw new InvalidOperationException($"PLCopen file not found: {filePath}");

        dynamic project = RequirePlcProjectNode(plcProjectPath);
        try { project.PlcOpenImport(filePath, 0, "", true); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"PlcOpenImport('{filePath}') into '{plcProjectPath}' failed: {ex.Message}", ex);
        }
        return null;
    }, OpenTimeout);

    // The node must be the PLC project itself (ItemType 600): an export/import on any other node fails or acts elsewhere.
    private static object RequirePlcProjectNode(string plcProjectPath)
    {
        if (string.IsNullOrWhiteSpace(plcProjectPath))
            throw new InvalidOperationException("plcProjectPath is required.");
        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        object node = LookupTreeItemWithFallback(sysMan, plcProjectPath)
            ?? throw new InvalidOperationException($"PLC project node not found: {plcProjectPath}");
        int? itemType = ReadItemTypeOrNull(node);
        if (itemType != PlcItemType.PlcApp)
            throw new InvalidOperationException(
                $"'{plcProjectPath}' is not a PLC project node (ItemType " +
                $"{itemType?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}).");
        return node;
    }
}
