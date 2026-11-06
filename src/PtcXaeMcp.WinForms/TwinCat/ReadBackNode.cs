namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Issue #152 B-21: the node a read-back compares against must come from a fresh lookup. The old
/// <c>LookupTreeItemWithFallback(...) ?? node</c> fell back to the very COM reference the setter had
/// just written through when the re-resolve failed — the verification then only echoed the setter
/// (circular) and passed even when the change never reached the project. A failed re-resolve is
/// "not verified" and throws.
/// </summary>
internal static class ReadBackNode
{
    public static object Require(object? freshlyResolved, string path) =>
        freshlyResolved ?? throw new InvalidOperationException(NotReResolved(path));

    public static string NotReResolved(string path) =>
        $"the write was sent, but '{path}' could not be re-resolved for the read-back, so it is not verified " +
        "(the read-back never falls back to the node the write went through). Read it back with " +
        "twincat_plc_read before relying on it.";
}
