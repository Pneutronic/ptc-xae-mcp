using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Enforces the "one consistent result contract" from issue #151: every
/// <c>[McpServerTool]</c> method on <see cref="TwinCatMcpTools"/> must be
/// wrapped in <c>Activity.Trace</c>/<c>TraceAsync</c> (so it is logged and
/// gated), and every method that touches the live COM session — whether
/// directly/via a *Coordinator (<c>bridge.Dispatcher.Session</c>) or through
/// the <c>ServerHost</c> field (<c>host.*</c>) — must run inside the
/// <c>McpDispatcher</c> lock (so it cannot race a concurrent operation).
/// <para>
/// Reflection alone cannot see a method body, so this test locates and
/// parses the partial-class source files
/// (<c>src/PtcXaeMcp.WinForms/Mcp/TwinCatMcpTools.*.cs</c>) by walking up
/// from the test assembly's output directory to the repo root. Justified
/// exceptions are named, one line each, in <see cref="TraceExemptions"/>,
/// <see cref="DispatcherExemptions"/> and <see cref="HostAccessExemptions"/>
/// below — anything not on a list must satisfy the rule, and every listed
/// name must still exist as a real tool (no stale allow-list entries).
/// </para>
/// </summary>
public sealed class McpToolResultContractTests
{
    /// <summary>
    /// Tools that legitimately never pass through <c>Activity.Trace</c> /
    /// <c>TraceAsync</c>. Keep this list tiny — it is the audit-trail gap.
    /// </summary>
    private static readonly Dictionary<string, string> TraceExemptions = new()
    {
        ["ptc_test_marker"] =
            "test-only narration marker for the live suite; deliberately not " +
            "logged as an XAE operation and not gated (see TwinCatMcpTools.Diagnostics.cs doc comment).",
    };

    /// <summary>
    /// Tools that legitimately touch <c>bridge.Dispatcher.Session</c> (directly
    /// or via a *Coordinator constructor) without going through
    /// <c>Dispatcher.Run</c>/<c>RunAsync</c>. Keep this list tiny — it is the
    /// race-condition exposure.
    /// </summary>
    private static readonly Dictionary<string, string> DispatcherExemptions = new()
    {
        ["twincat_generate_trial_licenses"] =
            "pure Win32 dialog automation (screenshots the security-code dialog); " +
            "the session ctor argument is unused for COM calls, matching twincat_submit_security_code.",
        ["twincat_submit_security_code"] =
            "pure Win32 dialog automation; no COM object is touched, only child-window text/click.",
        ["twincat_dismiss_security_dialog"] =
            "pure Win32 dialog automation (issue #150, C-6): answers the security-code dialog a running " +
            "twincat_activate_config is blocked on, so it must not wait for the lock that call holds.",
        ["twincat_force_close_xae"] =
            "recovery tool that kills the XAE process itself; must not serialize behind the very " +
            "dispatcher lock a hung operation may be holding.",
    };

    /// <summary>
    /// Tools whose body calls into <c>host.*</c> (<see cref="TwinCatMcpTools"/>'s
    /// <c>ServerHost</c> field, the <c>Form1</c> field before issue #268) without going
    /// through <c>Dispatcher.Run</c>/<c>RunAsync</c> — the dispatcher-lock check above only
    /// ever looked for <c>Dispatcher.Session</c> text, so a tool that reaches the live XAE
    /// through a host helper instead of a *Coordinator was invisible to it (issue #151 item 2). Each entry justifies why that helper's COM
    /// access (if any) is safe without the lock. Keep this list tiny.
    /// </summary>
    private static readonly Dictionary<string, string> HostAccessExemptions = new()
    {
        ["twincat_get_status"] =
            "ServerHost.GetStatus is deliberately lock-free and bounded: its one COM call " +
            "(MultiXaeBlockReason, only when >1 TcXaeShell is running) is wrapped in " +
            "Task.Run(...).Wait(5s) and reports a timeout error instead of hanging, so " +
            "get_status can always answer even if another XAE instance is stuck.",
        ["twincat_get_log"] =
            "ServerHost.RunLog.Text only reads the in-memory run log; no COM.",
        ["twincat_stop"] =
            "ServerHost.RequestStop only cancels the active operation's CancellationTokenSource; no COM.",
        ["twincat_apply_schema"] =
            "ServerHost.RunApplySchemaAsync acquires the same McpDispatcher lock itself, via " +
            "TryAcquire()/Release() around the COM-touching NativeTwinCatRunner call, " +
            "rather than through the Dispatcher.Run/RunAsync wrapper this check looks for.",
        ["twincat_umr_list_instances"] =
            "host.UmrManager enumerates filesystem directories for the standalone UMR " +
            "process, not the XAE COM session (see DispatcherExemptions/InformationalNotes).",
        ["twincat_umr_start"] =
            "host.UmrManager controls the standalone TcSystemServiceUm.exe process, not the XAE COM session.",
        ["twincat_umr_set_mode"] =
            "host.UmrManager controls the standalone UMR process, not the XAE COM session.",
        ["twincat_umr_stop"] =
            "host.UmrManager controls the standalone UMR process, not the XAE COM session.",
        ["twincat_shutdown"] =
            "host.UmrManager.IsRunning/.State is a plain in-memory status read of the " +
            "standalone UMR process (issue #154, A-8's force-guard), not XAE COM access; " +
            "the shutdown itself goes through bridge.RequestShutdown, not the dispatcher.",
    };

    /// <summary>
    /// Tools intentionally exempt from the gate entirely (bypassGate: true) or
    /// from Trace's normal timing/COM path for a documented reason. Recorded
    /// for completeness per issue #151's suggested list; the automated checks
    /// above are what the test actually enforces.
    /// </summary>
    private static readonly Dictionary<string, string> InformationalNotes = new()
    {
        ["twincat_stop"] = "control command; bypasses the gate so it can never deadlock behind a stuck operation.",
        ["twincat_shutdown"] = "must return within ~200ms without waiting on the gate or dispatcher lock.",
        ["twincat_get_client_info"] = "reads only the MCP RequestContext/session registry, never the COM session.",
        ["twincat_get_status"] = "reads cached environment status; kept lock-free by design.",
        ["twincat_get_log"] = "reads the in-memory activity log buffer only.",
        ["twincat_umr_list_instances"] = "enumerates filesystem directories; controls the standalone UMR process, not the XAE COM session.",
        ["twincat_umr_start"] = "controls the standalone TcSystemServiceUm.exe process, not the XAE COM session.",
        ["twincat_umr_set_mode"] = "controls the UMR process, not the XAE COM session.",
        ["twincat_umr_stop"] = "controls the UMR process, not the XAE COM session.",
        ["twincat_import_iodd"] = "file-only IODD registration; does not touch the XAE COM session.",
        ["twincat_check_iodd_present"] = "file-only IODD lookup; does not touch the XAE COM session.",
        ["twincat_list_ethercat_box_catalog"] = "file-only ESI directory scan (issue #85); does not touch the XAE COM session.",
        ["twincat_read_scope_file"] = "file-only: runs TC3ScopeExportTool.exe on a .svdx; does not touch the XAE COM session.",
        ["twincat_convert_scope_file"] = "file-only: runs TC3ScopeExportTool.exe on a .svdx; does not touch the XAE COM session.",
    };

    private static readonly Regex ToolAttributeRegex =
        new(@"\[McpServerTool\(Name\s*=\s*""([^""]+)""[^\]]*\)\]", RegexOptions.Compiled);

    [Fact]
    public void EveryMcpServerTool_IsTracedOrExplicitlyExempted()
    {
        var bodies = LoadToolBodies();
        var failures = new List<string>();

        foreach (var tool in DiscoverToolNames())
        {
            if (TraceExemptions.ContainsKey(tool))
                continue;

            if (!bodies.TryGetValue(tool, out var body))
            {
                failures.Add($"{tool}: no source body found to inspect (source parsing out of sync with reflection).");
                continue;
            }

            bool traced = body.Contains(".Trace(", StringComparison.Ordinal)
                       || body.Contains(".TraceAsync(", StringComparison.Ordinal);
            if (!traced)
                failures.Add($"{tool}: does not call Activity.Trace/TraceAsync and is not in TraceExemptions.");
        }

        Assert.True(failures.Count == 0, "Untraced MCP tools:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void EveryComTouchingTool_UsesTheDispatcher_OrIsExplicitlyExempted()
    {
        var bodies = LoadToolBodies();
        var failures = new List<string>();

        foreach (var tool in DiscoverToolNames())
        {
            if (!bodies.TryGetValue(tool, out var body))
                continue; // reported by the trace test above

            bool touchesCom = body.Contains("Dispatcher.Session", StringComparison.Ordinal);
            if (!touchesCom)
                continue;

            bool dispatched = body.Contains("Dispatcher.Run(", StringComparison.Ordinal)
                            || body.Contains("Dispatcher.RunAsync(", StringComparison.Ordinal);
            if (!dispatched && !DispatcherExemptions.ContainsKey(tool))
                failures.Add($"{tool}: touches Dispatcher.Session without Dispatcher.Run/RunAsync and is not in DispatcherExemptions.");
        }

        Assert.True(failures.Count == 0, "COM access bypassing the dispatcher lock:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void EveryHostTouchingTool_UsesTheDispatcher_OrIsExplicitlyExempted()
    {
        // Mirrors EveryComTouchingTool_UsesTheDispatcher_OrIsExplicitlyExempted
        // above, but for COM access reached through the ServerHost field (`host.`)
        // instead of `bridge.Dispatcher.Session` / a *Coordinator ctor. Same
        // coarse-text-match limitation: a tool that ALSO calls Dispatcher.Run/
        // RunAsync anywhere in its body (e.g. only for an unrelated
        // afterSuccess UI refresh) is not flagged, matching how the sibling
        // check treats Dispatcher.Session.
        var bodies = LoadToolBodies();
        var failures = new List<string>();

        foreach (var tool in DiscoverToolNames())
        {
            if (!bodies.TryGetValue(tool, out var body))
                continue; // reported by the trace test above

            bool touchesHost = body.Contains("host.", StringComparison.Ordinal);
            if (!touchesHost)
                continue;

            bool dispatched = body.Contains("Dispatcher.Run(", StringComparison.Ordinal)
                            || body.Contains("Dispatcher.RunAsync(", StringComparison.Ordinal);
            if (!dispatched && !HostAccessExemptions.ContainsKey(tool))
                failures.Add($"{tool}: touches host.* without Dispatcher.Run/RunAsync and is not in HostAccessExemptions.");
        }

        Assert.True(failures.Count == 0, "COM access via ServerHost bypassing the dispatcher lock:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AllowLists_ReferenceOnlyRealTools_NoStaleEntries()
    {
        var toolNames = new HashSet<string>(DiscoverToolNames(), StringComparer.Ordinal);
        var stale = TraceExemptions.Keys
            .Concat(DispatcherExemptions.Keys)
            .Concat(HostAccessExemptions.Keys)
            .Concat(InformationalNotes.Keys)
            .Where(name => !toolNames.Contains(name))
            .ToList();

        Assert.True(stale.Count == 0,
            "Allow-list entries reference tools that no longer exist: " + string.Join(", ", stale));
    }

    [Fact]
    public void ReflectionDiscoversToolsAndSourceParsingAgree()
    {
        // Guards the test itself: if reflection finds tools the source parser
        // doesn't (or vice versa), the checks above would silently under-cover.
        var reflected = new HashSet<string>(DiscoverToolNames(), StringComparer.Ordinal);
        var parsed = new HashSet<string>(LoadToolBodies().Keys, StringComparer.Ordinal);

        var onlyInReflection = reflected.Except(parsed).ToList();
        Assert.True(onlyInReflection.Count == 0,
            "Tools found via reflection but not via source parsing: " + string.Join(", ", onlyInReflection));
    }

    // ---- discovery helpers -------------------------------------------------

    /// <summary>
    /// Tool types reflected for tool discovery: the production surface
    /// (<see cref="TwinCatMcpTools"/>) plus the diagnostics/research/test-support
    /// surface (<see cref="TwinCatMcpToolsDiagnostics"/>, issue #157 item 1) — the
    /// latter is only registered at runtime with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1,
    /// but its tool bodies must satisfy the same trace/dispatcher contract.
    /// </summary>
    private static readonly Type[] ToolTypes = [typeof(TwinCatMcpTools), typeof(TwinCatMcpToolsDiagnostics)];

    private static IEnumerable<string> DiscoverToolNames()
    {
        return ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// Maps toolName -> the source text of its method body, parsed out of
    /// every <c>TwinCatMcpTools.*.cs</c> partial-class file. A "body" here
    /// runs from the end of the <c>[McpServerTool]</c> attribute to the start
    /// of the next one (or end of file) — coarse, but good enough to grep for
    /// the API calls this contract requires.
    /// </summary>
    private static Dictionary<string, string> LoadToolBodies()
    {
        var dir = FindMcpSourceDirectory();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(dir, "TwinCatMcpTools.*.cs"))
        {
            var text = File.ReadAllText(file);
            var matches = ToolAttributeRegex.Matches(text);
            for (int i = 0; i < matches.Count; i++)
            {
                var name = matches[i].Groups[1].Value;
                int start = matches[i].Index + matches[i].Length;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                result[name] = text[start..end];
            }
        }

        return result;
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to find
    /// <c>src/PtcXaeMcp.WinForms/Mcp</c>, so the test works regardless of
    /// repo root name or worktree location.
    /// </summary>
    private static string FindMcpSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PtcXaeMcp.WinForms", "Mcp");
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, "TwinCatMcpTools.*.cs").Any())
                return candidate;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/PtcXaeMcp.WinForms/Mcp by walking up from " +
            AppContext.BaseDirectory + " — repo layout changed?");
    }
}
