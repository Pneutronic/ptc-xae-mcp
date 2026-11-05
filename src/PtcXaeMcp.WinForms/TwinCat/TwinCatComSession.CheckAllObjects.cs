using EnvDTE80;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- Check all objects (see CheckAllObjects.cs) ------------------------------
    //
    // The command is enabled only while an object of a PLC project is selected in the Solution
    // Explorer (live: disabled with SYSTEM or an I/O node selected, enabled with a POU). The PLC
    // project node is therefore selected first, by its localized names via UIAutomation like
    // twincat_open_object. The Error List is cleared before the check: it keeps every message since
    // the XAE started, so an error from an earlier build would otherwise be reported as this one's.

    private static readonly TimeSpan CheckAllObjectsTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckAllObjectsInterval = TimeSpan.FromMilliseconds(500);

    public BuildResult CheckAllPlcObjects(string? plcProjectName)
    {
        var (dteObj, display, hwnd, commandName) = ComBusyRetry.Run<(object, List<string>, IntPtr, string)>(() =>
        {
            dynamic d = GetActiveTcDte();
            SaveBeforeBuild(d);
            object sysMan = RequireSystemManager(d);
            string name = ResolveCheckAllObjectsName((DTE2)d)
                ?? throw new InvalidOperationException(
                    $"this XAE has no 'Check all objects' command (command {CheckAllObjectsCommand.Guid}:{CheckAllObjectsCommand.Id}).");
            return ((object)d, PlcProjectDisplayPath(sysMan, plcProjectName), (IntPtr)TryGetXaeMainWindow(d), name);
        }, OpenTimeout);
        dynamic dte = dteObj;

        // Some rows survive the clear (live: an HMI server warning that comes straight back). They are not this
        // check's; they are noted here and left out of the result. A compiler error never survives a clear.
        ErrorListClearResult cleared = ClearErrorList();
        if (!cleared.Success && cleared.RemainingCount < 0)
            throw new InvalidOperationException(
                $"the Error List could not be cleared first, so its entries would not be this check's: {cleared.Message}");
        List<ErrorListEntry> leftOver = ComBusyRetry.Run(() => ReadErrorListEntries((DTE2)dteObj), ErrorReadTimeout);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("no XAE main-window handle to select the PLC project in the Solution Explorer.");
        if (!SolutionExplorerAutomation.TrySelectPath(hwnd, display, out string note))
            throw new InvalidOperationException(
                $"could not select the PLC project '{string.Join(" > ", display)}' in the Solution Explorer, which the command needs: {note}");

        bool? available = ComBusyRetry.Run(() => ReadCheckAllObjectsAvailable(dteObj, commandName), OpenTimeout);
        if (available != true)
            throw new InvalidOperationException(
                $"'{commandName}' is {(available is null ? "unreadable" : "disabled")} although the PLC project " +
                $"'{string.Join(" > ", display)}' is selected ({note}).");

        Dictionary<string, OutputPaneDelta.PaneMark> priorPanes =
            ComBusyRetry.Run<Dictionary<string, OutputPaneDelta.PaneMark>>(() => MeasureOutputPanes(dteObj), OpenTimeout);
        // A call the XAE rejects did not run, so retrying the call alone is safe.
        ComBusyRetry.Run(() => { dte.ExecuteCommand(commandName); }, OpenTimeout);

        bool finished = CheckAllObjectsWait.Until(
            () => SampleCheckAllObjects(dteObj, commandName), CheckAllObjectsTimeout, CheckAllObjectsInterval);

        return ComBusyRetry.Run(() =>
        {
            List<(string Name, string Text)> panes = CollectOutputPanes(dteObj, priorPanes);
            string output = string.Join("\n", panes.Select(pn => pn.Text));
            List<BuildError> errors = ToBuildErrors(WithoutLeftOver(ReadErrorListEntries((DTE2)dteObj), leftOver), output);
            string? summary = BuildTruth.ExtractCompilerSummary(output);
            string? failure = CheckAllObjectsVerdict.FailureReason(errors, summary, finished, CheckAllObjectsTimeout);
            string log = CollectBuildLog(dteObj, panes, output);
            return new BuildResult(failure is null, failure is null ? 0 : 1, errors, log, summary, Artifact: null, failure);
        }, ErrorReadTimeout);
    }

    // Localized Solution-Explorer names of the PLC project node: TIPC, the PLC, its nested project.
    // plcProjectName matches the PLC or its project (case-insensitive); null takes the first PLC.
    private static List<string> PlcProjectDisplayPath(object sysMan, string? plcProjectName)
    {
        object tipc = (object?)LookupTreeItemOrNull(sysMan, "TIPC")
            ?? throw new InvalidOperationException("the solution has no PLC (TIPC not found).");
        foreach (object plcNode in (List<object>)Children(tipc))
        {
            object? nested = ComCall.ReadProp<object?>(() => ((dynamic)plcNode).NestedProject, null);
            if (nested is null) continue;
            string plcName = NameOf(plcNode), projectName = NameOf(nested);
            if (plcProjectName is not null
                && !string.Equals(plcName, plcProjectName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(projectName, plcProjectName, StringComparison.OrdinalIgnoreCase)) continue;
            return [NameOf(tipc), plcName, projectName];
        }
        throw new InvalidOperationException(plcProjectName is null
            ? "no PLC project with sources found under TIPC (a PLC referenced as a stand-alone .tspproj is not supported)."
            : $"PLC project '{plcProjectName}' not found under TIPC.");
    }

    // The rows of <paramref name="entries"/> minus one occurrence of each left-over row.
    private static List<ErrorListEntry> WithoutLeftOver(List<ErrorListEntry> entries, List<ErrorListEntry> leftOver)
    {
        var remaining = entries.ToList();
        foreach (var old in leftOver)
        {
            int i = remaining.FindIndex(e => e.Description == old.Description && e.File == old.File && e.Line == old.Line);
            if (i >= 0) remaining.RemoveAt(i);
        }
        return remaining;
    }

    private static string? ResolveCheckAllObjectsName(DTE2 dte)
    {
        try { return (string?)dte.Commands.Item(CheckAllObjectsCommand.Guid, CheckAllObjectsCommand.Id)?.Name; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    private static bool? ReadCheckAllObjectsAvailable(object dteObj, string commandName)
    {
        dynamic dte = dteObj;
        try { return (bool)dte.Commands.Item(commandName, 0).IsAvailable; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    private static CheckAllObjectsSample? SampleCheckAllObjects(object dteObj, string commandName)
    {
        try
        {
            bool? available = ReadCheckAllObjectsAvailable(dteObj, commandName);
            int errorCount;
            try { errorCount = ((DTE2)dteObj).ToolWindows.ErrorList.ErrorItems.Count; }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { errorCount = -1; }
            int outputLength = ReadOutputPanes(dteObj).Sum(p => p.Text.Length);
            return new CheckAllObjectsSample(available, errorCount, outputLength);
        }
        catch (Exception ex) when (ComCall.IsBusy(ex)) { return null; }
    }
}
