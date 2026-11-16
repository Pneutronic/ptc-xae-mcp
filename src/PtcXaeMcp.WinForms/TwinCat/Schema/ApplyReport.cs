using PtcXaeMcp.WinForms;
using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat.Schema;

/// <summary>
/// Structured outcome of one NativeTwinCatRunner.Run() apply. Building this (instead of only
/// ever throwing/logging along the way) is what lets the runner decide pass/fail once, at the
/// end, from the accumulated facts, rather than every call site deciding for itself.
///
/// Ok is false when either:
///  - MissingNodes contains a direct EtherCAT creation failure (E-4: a partially-built
///    topology, e.g. missing ESI, must not read as success), or
///  - linking.failIfUnresolved was true and any IO point failed to link (E-11).
/// Warnings collects everything that was downgraded to "continue, but tell the user" —
/// including unresolved links when failIfUnresolved is false.
/// </summary>
internal sealed class ApplyReport
{
    public bool Ok { get; init; }
    public IReadOnlyList<MissingEthercatNodeInfo> MissingNodes { get; init; } = [];
    public IReadOnlyList<LinkResult> Links { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>One line per IO point that could not be linked ("var (terminal ChN dir): reason").</summary>
    public IReadOnlyList<string> UnresolvedLinks { get; init; } = [];

    /// <summary>
    /// IO points without a <c>symbol</c>: they build hardware only and are not linked. Information,
    /// never a failure (a schema may build the hardware first and link later).
    /// </summary>
    public int NotLinked { get; init; }

    /// <summary>Every XAE dialog the run answered (issue #155 step 8), like activate_config reports them.</summary>
    public IReadOnlyList<string> DialogsAnswered { get; init; } = [];

    /// <summary>
    /// Computes Ok from the raw facts. Kept separate from the constructor so tests can hand it
    /// arbitrary combinations without going through a live run.
    /// </summary>
    internal static ApplyReport Build(
        IReadOnlyList<MissingEthercatNodeInfo> missingNodes,
        IReadOnlyList<LinkResult> links,
        IReadOnlyList<string> warnings,
        bool hadUnresolvedLinkFailure,
        IReadOnlyList<string>? unresolvedLinks = null,
        IReadOnlyList<string>? dialogsAnswered = null,
        int notLinked = 0)
    {
        bool hasDirectTopologyFailure = missingNodes.Any(n => n.IsDirectFailure);
        bool ok = !hasDirectTopologyFailure && !hadUnresolvedLinkFailure;

        return new ApplyReport
        {
            Ok = ok,
            MissingNodes = missingNodes,
            Links = links,
            Warnings = warnings,
            UnresolvedLinks = unresolvedLinks ?? [],
            DialogsAnswered = dialogsAnswered ?? [],
            NotLinked = notLinked,
        };
    }

    /// <summary>One-line-per-section summary for the "[Error] ..." message and the run log.</summary>
    internal string Summarize()
    {
        int directFailures = MissingNodes.Count(n => n.IsDirectFailure);
        int skipped = MissingNodes.Count - directFailures;
        return $"Ok={Ok} | EtherCAT: {directFailures} directly failed, {skipped} skipped | " +
               $"Links: {Links.Count} ok, {UnresolvedLinks.Count} unresolved, {NotLinked} notLinked | Warnings: {Warnings.Count}";
    }

    /// <summary>"XAE dialogs answered: none." or the list, as activate_config words it.</summary>
    internal static string DescribeDialogs(IReadOnlyList<string> dialogs) =>
        dialogs.Count == 0 ? "  XAE dialogs answered: none." : "  XAE dialogs answered: " + string.Join("; ", dialogs) + ".";

    /// <summary>
    /// <see cref="Summarize"/> plus one line per direct EtherCAT failure, consequence-skipped
    /// node and unresolved link. This is the text of a failed run's "[Error] ..." result: the
    /// MCP tool returns only the exception message, so the caller must learn from it which
    /// node / IO point failed, not only how many.
    /// </summary>
    internal string Describe()
    {
        var sb = new System.Text.StringBuilder(Summarize());
        foreach (var n in MissingNodes.Where(n => n.IsDirectFailure))
            sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  EtherCAT failed: '{n.Name}' [vInfo={n.VInfo}] under '{n.ParentName}': {n.Reason}");
        var skipped = MissingNodes.Where(n => !n.IsDirectFailure).Select(n => n.Name).ToList();
        if (skipped.Count > 0)
            sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  EtherCAT skipped (consequence): {string.Join(", ", skipped)}");
        foreach (var u in UnresolvedLinks)
            sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  Link unresolved: {u}");
        if (DialogsAnswered.Count > 0)
            sb.AppendLine().Append(DescribeDialogs(DialogsAnswered));
        return sb.ToString();
    }
}
