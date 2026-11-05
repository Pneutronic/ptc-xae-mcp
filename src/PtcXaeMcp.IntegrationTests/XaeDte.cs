using System.Runtime.InteropServices;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Direct COM access from the test process to the running XAE's DTE — only for arranging a state the
/// MCP tools deliberately never leave behind (every mutating tool saves), e.g. an unsaved change the
/// shell sees. One XAE runs at a time, so the ROT entry is that one.
/// </summary>
internal static class XaeDte
{
    private static readonly string[] ProgIds = ["TcXaeShell.DTE.17.0", "TcXaeShell.DTE.15.0"];

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    /// <summary>The running XAE's DTE, or null when none is registered.</summary>
    public static dynamic? TryGet()
    {
        foreach (string progId in ProgIds)
        {
            if (CLSIDFromProgID(progId, out Guid clsid) != 0) continue;
            if (GetActiveObject(ref clsid, IntPtr.Zero, out object? dte) == 0 && dte is not null)
                return dte;
        }
        return null;
    }

    private const int RpcECallRejected = unchecked((int)0x80010001);
    private const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);

    /// <summary>Runs <paramref name="call"/>, retrying while the XAE rejects calls because it is busy.</summary>
    public static T Retry<T>(Func<T> call)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return call(); }
            catch (COMException ex) when ((ex.HResult == RpcECallRejected || ex.HResult == RpcEServerCallRetryLater)
                                          && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>
    /// Sets the cycle time (100 ns units) of the first real-time task through the System Manager's
    /// ConsumeXml, WITHOUT saving: the .tsproj becomes dirty in the shell, the file on disk stays as is.
    /// Returns whether the project then reports unsaved changes.
    /// </summary>
    /// <remarks>
    /// Every COM reference taken here is released before returning. A System Manager reference left to
    /// the finalizer is released later, possibly after its solution was closed; the XAE then shows the
    /// modal "System Manager already disposed!" message box, which blocks the next close call
    /// (seen live in the full suite).
    /// </remarks>
    public static bool SetFirstTaskCycleTimeUnsaved(int cycleTime100ns)
    {
        object dte = TryGet() ?? throw new InvalidOperationException("No running XAE (TcXaeShell.DTE) in the ROT.");
        var refs = new List<object> { dte };
        T Track<T>(T o) where T : class { refs.Add(o); return o; }
        try
        {
            return Retry(() =>
            {
                dynamic solution = Track<object>(((dynamic)dte).Solution);
                dynamic projects = Track<object>(solution.Projects);
                dynamic project = Track<object>(projects.Item(1));
                dynamic sysManager = Track<object>(project.Object);
                dynamic tasks = Track<object>(sysManager.LookupTreeItem("TIRT"));
                dynamic task = Track<object>(tasks.Child(1));
                task.ConsumeXml($"<TreeItem><TaskDef><CycleTime>{cycleTime100ns}</CycleTime></TaskDef></TreeItem>");
                return !(bool)project.Saved;
            });
        }
        finally
        {
            for (int i = refs.Count - 1; i >= 0; i--)
            {
                try { if (Marshal.IsComObject(refs[i])) Marshal.FinalReleaseComObject(refs[i]); }
                catch (Exception) { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Cleanup for a task created by twincat_plc_task_create (no MCP tool removes one): DeleteChild of the PLC task
    /// reference under <paramref name="plcProjectPath"/> and of the real-time task under TIRT, best effort, unsaved.
    /// Returns what happened per item.
    /// </summary>
    public static string DeleteTaskAndReference(string plcProjectPath, string taskName)
    {
        object dte = TryGet() ?? throw new InvalidOperationException("No running XAE (TcXaeShell.DTE) in the ROT.");
        var refs = new List<object> { dte };
        T Track<T>(T o) where T : class { refs.Add(o); return o; }
        try
        {
            return Retry(() =>
            {
                dynamic solution = Track<object>(((dynamic)dte).Solution);
                dynamic projects = Track<object>(solution.Projects);
                dynamic project = Track<object>(projects.Item(1));
                dynamic sysManager = Track<object>(project.Object);
                var report = new List<string>();
                foreach (string parent in new[] { plcProjectPath, "TIRT" })
                {
                    try
                    {
                        dynamic node = Track<object>(sysManager.LookupTreeItem(parent));
                        node.DeleteChild(taskName);
                        report.Add($"{parent}^{taskName}: deleted");
                    }
                    catch (COMException ex) when (ex.HResult != RpcECallRejected && ex.HResult != RpcEServerCallRetryLater)
                    {
                        report.Add($"{parent}^{taskName}: {ex.Message}");
                    }
                }
                return string.Join("; ", report);
            });
        }
        finally
        {
            for (int i = refs.Count - 1; i >= 0; i--)
            {
                try { if (Marshal.IsComObject(refs[i])) Marshal.FinalReleaseComObject(refs[i]); }
                catch (Exception) { /* best effort */ }
            }
        }
    }
}
