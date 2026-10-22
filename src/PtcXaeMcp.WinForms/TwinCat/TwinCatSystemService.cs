using System.Runtime.InteropServices;

namespace PtcXaeMcp.WinForms.TwinCat;

internal enum SystemServiceState { Unknown, Running, NotRunning }

internal readonly record struct SystemServiceStatus(SystemServiceState State, string Detail);

/// <summary>
/// State of the local TwinCAT System Service (TcSysSrv). Without it there is no local AMS router,
/// so ADS, activation, login and the UMR cannot work. The tools used to fail with misleading
/// ClientSyncTimeOut / E_ACCESSDENIED / "Login is disabled" errors instead (issue #231). The MCP
/// host is not elevated and must never try to start the service itself.
/// </summary>
internal static class TwinCatSystemService
{
    internal const string ServiceName = "TcSysSrv";

    /// <summary>Test seam; production reads the Service Control Manager.</summary>
    internal static Func<SystemServiceStatus> Probe { get; set; } = QueryScm;

    /// <summary>
    /// Tools that need the local AMS router. Stop/abort-type tools (ibn_abort, ibn_disarm,
    /// umr_stop, stop_scope_record, logout) stay unblocked: they must always be allowed to try.
    /// </summary>
    internal static readonly IReadOnlySet<string> AdsTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "twincat_activate_config", "twincat_activate_boot_project", "twincat_restart_runtime", "twincat_set_system_mode",
        "twincat_login", "twincat_plc_start", "twincat_plc_stop", "twincat_get_plc_online_state",
        "twincat_read_ads_variable", "twincat_read_ads_variables_batch", "twincat_write_ads_variable",
        "twincat_measure_signal_transition", "twincat_broadcast_search", "twincat_add_route",
        "twincat_umr_start", "twincat_umr_set_mode", "twincat_start_scope_record",
        "twincat_read_event_logger", "twincat_confirm_event_logger",
        "twincat_ibn_arm", "twincat_ibn_propose",
        "twincat_list_ads_symbols", "twincat_persistent_snapshot_save", "twincat_persistent_snapshot_compare",
        "twincat_persistent_snapshot_restore",
    };

    public static SystemServiceStatus Query()
    {
        try { return Probe(); }
        catch (Exception ex) { return new SystemServiceStatus(SystemServiceState.Unknown, ex.Message); }
    }

    public static string StatusText(SystemServiceStatus s) => s.State switch
    {
        SystemServiceState.Running => "TwinCAT System: Running",
        SystemServiceState.NotRunning => $"TwinCAT System: {s.Detail} ⚠",
        _ => $"TwinCAT System: Unknown ({s.Detail})",
    };

    public static string BlockedMessage(SystemServiceStatus s) =>
        $"[Error] Blocked: the TwinCAT System Service ({ServiceName}) is {s.Detail} — no local AMS router, " +
        "so ADS, activation, login and the UMR cannot work. Do not retry: reboot the machine (a service " +
        "restart was not sufficient when seen live) or start the service as administrator. The MCP cannot do this.";

    /// <summary>The block message for an ADS tool while the service is confirmed not running; unknown never blocks.</summary>
    public static string? BlockReason(string toolName, out SystemServiceStatus status)
    {
        status = default;
        if (!AdsTools.Contains(toolName)) return null;
        status = Query();
        return status.State == SystemServiceState.NotRunning ? BlockedMessage(status) : null;
    }

    /// <summary>A warning to append when the service was running before an ADS tool and is not after it.</summary>
    public static string? StoppedDuring(SystemServiceStatus before)
    {
        if (before.State != SystemServiceState.Running) return null;
        var after = Query();
        return after.State == SystemServiceState.NotRunning
            ? $"\n[Warning] The TwinCAT System Service ({ServiceName}) went from Running to {after.Detail} during this call. " +
              "ADS, activation and the UMR no longer work; stop and reboot the machine."
            : null;
    }

    private static SystemServiceStatus QueryScm()
    {
        const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004;
        const int ErrorServiceDoesNotExist = 1060;
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return new(SystemServiceState.Unknown, $"SCM not accessible (Win32 {Marshal.GetLastWin32Error()})");
        try
        {
            IntPtr svc = OpenServiceW(scm, ServiceName, ServiceQueryStatus);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return new(SystemServiceState.Unknown,
                    err == ErrorServiceDoesNotExist ? "not installed" : $"not queryable (Win32 {err})");
            }
            try
            {
                if (!QueryServiceStatus(svc, out var st))
                    return new(SystemServiceState.Unknown, $"not queryable (Win32 {Marshal.GetLastWin32Error()})");
                return st.dwCurrentState switch
                {
                    4 => new(SystemServiceState.Running, "Running"),
                    1 => new(SystemServiceState.NotRunning, "Stopped"),
                    2 => new(SystemServiceState.NotRunning, "StartPending"),
                    3 => new(SystemServiceState.NotRunning, "StopPending"),
                    5 => new(SystemServiceState.NotRunning, "ContinuePending"),
                    6 => new(SystemServiceState.NotRunning, "PausePending"),
                    7 => new(SystemServiceState.NotRunning, "Paused"),
                    _ => new(SystemServiceState.Unknown, $"state {st.dwCurrentState}"),
                };
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                    dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
