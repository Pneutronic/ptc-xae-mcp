using EnvDTE80;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- Check all objects (see CheckAllObjects.cs) ------------------------------
    //
    // First ITcPlcIECProject2.CheckAllObjects() on the PLC project item: no window is touched, so the XAE stays
    // behind the user's work. Only when this XAE does not offer that call, the XAE command runs instead; it is
    // enabled only while an object of a PLC project is selected in the Solution Explorer (live: disabled with
    // SYSTEM or an I/O node selected, enabled with a POU), and selecting it brings the XAE to the front — so that
    // way needs bringToFront=true. Either way the Error List is cleared before the check: it keeps every message
    // since the XAE started, so an error from an earlier build would otherwise be reported as this one's.

    private static readonly TimeSpan CheckAllObjectsTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckAllObjectsInterval = TimeSpan.FromMilliseconds(500);

    public BuildResult CheckAllPlcObjects(string? plcProjectName, bool bringToFront)
    {
        var (dteObj, project, display, hwnd, commandName) = ComBusyRetry.Run<(object, object, List<string>, IntPtr, string?)>(() =>
        {
            dynamic d = GetActiveTcDte();
            SaveBeforeBuild(d);
            object sysMan = RequireSystemManager(d);
            var (nested, path) = PlcProjectForCheck(sysMan, plcProjectName);
            return ((object)d, nested, path, (IntPtr)TryGetXaeMainWindow(d), ResolveCheckAllObjectsName((DTE2)d));
        }, OpenTimeout);
        dynamic dte = dteObj;

        // Some rows survive the clear (live: an HMI server warning that comes straight back). They are not this
        // check's; they are noted here and left out of the result. A compiler error never survives a clear.
        ErrorListClearResult cleared = ClearErrorList();
        if (!cleared.Success && cleared.RemainingCount < 0)
            throw new InvalidOperationException(
                $"the Error List could not be cleared first, so its entries would not be this check's: {cleared.Message}");
        List<ErrorListEntry> leftOver = ComBusyRetry.Run(() => ReadErrorListEntries((DTE2)dteObj), ErrorReadTimeout);

        Dictionary<string, OutputPaneDelta.PaneMark> priorPanes =
            ComBusyRetry.Run<Dictionary<string, OutputPaneDelta.PaneMark>>(() => MeasureOutputPanes(dteObj), OpenTimeout);

        bool? reported = null;
        string note;
        bool viaInterface = ComBusyRetry.Run(() =>
        {
            try
            {
                reported = (bool)((dynamic)project).CheckAllObjects();
                return true;
            }
            catch (Exception ex) when (ComCall.NotBusy(ex) && IsMissingCheckAllObjects(ex)) { return false; }
        }, CheckAllObjectsTimeout);

        if (viaInterface)
            note = "ran through the Automation Interface (ITcPlcIECProject2.CheckAllObjects); the XAE was not brought to the front.";
        else
        {
            string label = string.Join(" > ", display);
            if (!bringToFront)
                throw new InvalidOperationException(
                    "this XAE does not offer ITcPlcIECProject2.CheckAllObjects. Its 'Check all objects' command needs the PLC project " +
                    $"'{label}' selected in the Solution Explorer, which brings the XAE in front of the user's work — pass " +
                    "bringToFront=true to allow that.");
            if (commandName is null)
                throw new InvalidOperationException(
                    "this XAE offers neither ITcPlcIECProject2.CheckAllObjects nor the 'Check all objects' command " +
                    $"({CheckAllObjectsCommand.Guid}:{CheckAllObjectsCommand.Id}).");
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("no XAE main-window handle to select the PLC project in the Solution Explorer.");
            if (!SolutionExplorerAutomation.TrySelectPath(hwnd, display, out string selectNote))
                throw new InvalidOperationException(
                    $"could not select the PLC project '{label}' in the Solution Explorer, which the command needs: {selectNote}");

            bool? available = ComBusyRetry.Run(() => ReadCheckAllObjectsAvailable(dteObj, commandName), OpenTimeout);
            if (available != true)
                throw new InvalidOperationException(
                    $"'{commandName}' is {(available is null ? "unreadable" : "disabled")} although the PLC project " +
                    $"'{label}' is selected ({selectNote}).");

            // A call the XAE rejects did not run, so retrying the call alone is safe.
            ComBusyRetry.Run(() => { dte.ExecuteCommand(commandName); }, OpenTimeout);
            note = $"ran the XAE command '{commandName}' (this XAE does not offer ITcPlcIECProject2.CheckAllObjects); " +
                   "the XAE was brought to the front (bringToFront=true).";
        }

        // The interface call returns when the check is done; the wait still lets the Error List and the Output
        // window settle before they are read. After the command it is what tells that the check has finished.
        bool finished = CheckAllObjectsWait.Until(
            () => SampleCheckAllObjects(dteObj, commandName), CheckAllObjectsTimeout, CheckAllObjectsInterval);

        return ComBusyRetry.Run(() =>
        {
            List<(string Name, string Text)> panes = CollectOutputPanes(dteObj, priorPanes);
            string output = string.Join("\n", panes.Select(pn => pn.Text));
            List<BuildError> errors = ToBuildErrors(WithoutLeftOver(ReadErrorListEntries((DTE2)dteObj), leftOver), output);
            string? summary = BuildTruth.ExtractCompilerSummary(output);
            string? failure = CheckAllObjectsVerdict.FailureReason(errors, summary, finished, CheckAllObjectsTimeout, reported);
            string log = CollectBuildLog(dteObj, panes, output);
            return new BuildResult(failure is null, failure is null ? 0 : 1, errors, log, summary, Artifact: null, failure, note);
        }, ErrorReadTimeout);
    }

    // The member is not on this XAE's IDispatch (an XAE before ITcPlcIECProject2): RuntimeBinder,
    // DISP_E_UNKNOWNNAME, DISP_E_MEMBERNOTFOUND.
    private static bool IsMissingCheckAllObjects(Exception ex) =>
        ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
        || ex.HResult is unchecked((int)0x80020006) or unchecked((int)0x80020003);

    // The nested PLC project item (it carries ITcPlcIECProject2) and its localized Solution-Explorer names: TIPC,
    // the PLC, its nested project. plcProjectName matches the PLC or its project (case-insensitive); null takes the
    // first PLC.
    private static (object Project, List<string> Display) PlcProjectForCheck(object sysMan, string? plcProjectName)
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
            return (nested, [NameOf(tipc), plcName, projectName]);
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

    private static bool? ReadCheckAllObjectsAvailable(object dteObj, string? commandName)
    {
        if (commandName is null) return null;
        dynamic dte = dteObj;
        try { return (bool)dte.Commands.Item(commandName, 0).IsAvailable; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    private static CheckAllObjectsSample? SampleCheckAllObjects(object dteObj, string? commandName)
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
