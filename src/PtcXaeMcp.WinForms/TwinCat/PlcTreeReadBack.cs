namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Read-back of a PLC tree mutation through an independent session call (issue #152, B-13):
/// <see cref="ITwinCatSession.ProbePlcNode"/> re-resolves the path from the system manager, so a
/// passing check is what a fresh tool call would see — not an echo of the COM node the mutation
/// went through. Each check returns null when the expected state holds, otherwise an
/// <c>[Error]</c> text naming <c>operation</c> (e.g. "Delete of '^POUs^FB_Foo'").
/// </summary>
internal static class PlcTreeReadBack
{
    /// <summary>The path must no longer resolve (after a delete, or the old path after a rename).</summary>
    public static string? RequireMissing(ITwinCatSession session, string path, string operation)
    {
        PlcNodeInfo after;
        try { after = session.ProbePlcNode(path); }
        catch (Exception ex) { return Unreadable(operation, path, ex); }
        return after.Exists
            ? $"[Error] {operation} did not land: '{path}' still resolves after the change (independent re-lookup). " +
              "Check the tree in the XAE (twincat_plc_list, with parentPath for members)."
            : null;
    }

    /// <summary>The path must resolve (after a create, or the new path after a rename).</summary>
    public static string? RequirePresent(ITwinCatSession session, string path, string operation)
    {
        PlcNodeInfo after;
        try { after = session.ProbePlcNode(path); }
        catch (Exception ex) { return Unreadable(operation, path, ex); }
        return after.Exists
            ? null
            : $"[Error] {operation} did not land: '{path}' does not resolve after the change (independent re-lookup). " +
              "Check the tree in the XAE (twincat_plc_list, with parentPath for members).";
    }

    // An unreadable read-back is "unknown", never "it worked".
    private static string Unreadable(string operation, string path, Exception ex) =>
        McpErrors.EnsureError(
            $"{operation} was sent, but the read-back of '{path}' failed ({ComCall.Describe(ex)}), " +
            "so it is not confirmed. Check the tree in the XAE before retrying.");
}
