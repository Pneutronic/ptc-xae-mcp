using PtcXaeMcp.WinForms.Diagnostics;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Reflection;

namespace PtcXaeMcp.WinForms;

internal sealed class McpBridge : IDisposable
{
    public const int Port = 5010;

    /// <summary>Sessions without a tool call within this window are swept.</summary>
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Env var checked alongside the <c>--diagnostics</c> command-line flag
    /// (issue #157 item 1). Either one registers the diagnostics/research/test-support
    /// tool surface (<see cref="TwinCatMcpToolsDiagnostics"/>) in addition to the
    /// production tools — used by the live integration suite host, never by a
    /// released MCP client.</summary>
    public const string DiagnosticsEnvVar = "PTC_XAE_MCP_DIAGNOSTICS";

    private readonly ServerHost _host;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _diagnosticsEnabled;
    private Task? _serverTask;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private bool _stopped;

    /// <summary>Live registry of connected MCP client sessions (per-session, multi-client).</summary>
    public McpSessionRegistry Sessions { get; } = new();

    /// <summary>Live trace of MCP tool calls + the step/pause/stop test gate.
    /// The per-run JSON log is also mirrored into the repo's <c>reports/</c>
    /// folder (when running from a checkout) so tooling can analyse a run from
    /// the repo, not just from <c>%LOCALAPPDATA%</c>.</summary>
    public McpActivity Activity { get; } =
        new(jsonLog: new McpActivityJsonLog(mirrorDirectory: RepoReportsDirOrNull()));

    /// <summary>Single-operation serializer, owned by <see cref="ServerHost"/> and shared with
    /// the GUI — one lock guards the COM session for both paths (issue #154).</summary>
    public McpDispatcher Dispatcher => _host.Dispatcher;

    /// <summary>ibn-live: host-owned, long-lived coordinator for the safe,
    /// AI-decoupled commissioning control surface, shared with the GUI (one proposal
    /// registry, heartbeat and watchdog). Its <c>ConfirmProposal</c> path is deliberately
    /// host-only (no MCP binding).</summary>
    public TwinCat.IbnLiveCoordinator LiveCoordinator => _host.LiveCoordinator;

    /// <summary>persistent-snapshot: host-owned restore-proposal registry (confirm is host-only, no MCP binding).</summary>
    public TwinCat.SnapshotRestoreCoordinator SnapshotRestore => _host.SnapshotRestore;

    /// <summary>The server started listening (true, port) or stopped (false, 0). Raised on a server thread.</summary>
    public event Action<bool, int>? StatusChanged;

    /// <summary>The server loop ended with an exception (port in use, crash); the argument is a
    /// message for the engineer. Raised on a server thread.</summary>
    public event Action<string>? Faulted;

    /// <summary><c>twincat_shutdown</c> asked the host to close. Raised on a background thread.</summary>
    public event Action? ShutdownRequested;

    /// <param name="host">Owns the session and everything the tools share with the GUI.</param>
    /// <param name="diagnosticsEnabled">Register the diagnostics/research/test-support tools
    /// (<see cref="TwinCatMcpToolsDiagnostics"/>) alongside the production tool surface — set
    /// from the <c>--diagnostics</c> command-line flag or <see cref="DiagnosticsEnvVar"/>
    /// (issue #157 item 1). Never enabled for a released MCP client.</param>
    public McpBridge(ServerHost host, bool diagnosticsEnabled = false)
    {
        _host = host;
        _diagnosticsEnabled = diagnosticsEnabled;
    }

    /// <summary>True when the diagnostics/test-support tools are registered; the GUI shows the
    /// test-harness controls (single-step, narration) only then (issue #269).</summary>
    public bool DiagnosticsEnabled => _diagnosticsEnabled;

    public void Start()
    {
        _serverTask = Task.Run(() => RunServerAsync(_cts.Token));
    }

    public void Stop()
    {
        // Idempotent (CA1001: McpBridge now implements IDisposable, whose Dispose()
        // calls this; a caller that already called Stop() explicitly — the existing
        // Program.cs FormClosed handler — must not hit ObjectDisposedException from a
        // second Cancel()/Dispose() on _cts).
        if (_stopped) return;
        _stopped = true;

        HostLog.Shared.Info("MCP server stop requested (host shutting down).");
        _cts.Cancel();
        // The dispatcher belongs to ServerHost, which disposes it after the server stopped.
        _cts.Dispose();
    }

    /// <summary>
    /// Satisfies CA1001 (a type owning disposable fields — <see cref="_cts"/> — must itself
    /// be disposable). Delegates to <see cref="Stop"/>, which was already the real
    /// teardown path (called from Program.cs's <c>FormClosed</c> handler); this adds no new
    /// behavior, just a standard entry point for callers that hold McpBridge as an
    /// <see cref="IDisposable"/> (e.g. a future <c>using</c> scope or test fixture).
    /// </summary>
    public void Dispose() => Stop();

    /// <param name="delay">Grace period before the form (and process) closes.</param>
    /// <param name="initiatedBy">Who asked for the shutdown — "tool" (the
    /// <c>twincat_shutdown</c> MCP tool) or "GUI" (the WinForms window closed by
    /// the user) — logged so a host-log reader can tell the two apart (issue #147 item 4).</param>
    public void RequestShutdown(TimeSpan delay, string initiatedBy = "tool")
    {
        HostLog.Shared.Info($"Shutdown requested by {initiatedBy}; closing in {delay.TotalMilliseconds:F0} ms.");
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            // The GUI closes its window (BeginInvokeSafe also covers a handle that
            // does not exist yet, issue #154 A-12/F-30); closing ends the process.
            ShutdownRequested?.Invoke();
        });
    }

    private async Task RunServerAsync(CancellationToken ct)
    {
        try
        {
            HostLog.Shared.Info($"MCP server starting on port {Port}...");

            var builder = WebApplication.CreateBuilder();
            // Drop the console/debug providers Kestrel wires by default, and
            // forward Warning+ from ASP.NET/Kestrel/the MCP SDK into the same
            // host log instead of nowhere (issue #147 item 3).
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new HostLogLoggerProvider(HostLog.Shared));
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.Services.AddSingleton(_host);
            builder.Services.AddSingleton(this);
            builder.Services.AddSingleton(Sessions);
            builder.Services.AddSingleton(Activity);
            var mcpBuilder = builder.Services
                .AddMcpServer(opts => opts.ServerInfo = new() { Name = "ptc-xae-mcp", Title = ProductInfo.DisplayName, Version = ThisAssemblyVersion() })
                .WithHttpTransport(opts =>
                    // Register every client at the real connect boundary (the
                    // initialize handshake) instead of relying on a single
                    // diagnostic tool to populate the registry (issue #71).
                    opts.SessionMigrationHandler = new McpSessionTracker(Sessions))
                .WithTools<TwinCatMcpTools>();

            // Diagnostics/research/test-support tools (ptc_test_marker, twincat_show_step,
            // twincat_safety_recon, twincat_safety_probe_create_alias, twincat_dump_nc_tree,
            // twincat_open_plc_object/twincat_close_plc_object) are NEVER part of the default
            // tool surface — a released MCP client never sees them. Opt in with --diagnostics
            // or PTC_XAE_MCP_DIAGNOSTICS=1 (issue #157 item 1); the live integration suite host
            // does this so it can still call twincat_show_step etc.
            if (_diagnosticsEnabled)
            {
                HostLog.Shared.Info("Diagnostics tool surface enabled (--diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1).");
                mcpBuilder.WithTools<TwinCatMcpToolsDiagnostics>();
            }

            var app = builder.Build();
            app.Urls.Add($"http://localhost:{Port}");

            // Localhost-only hardening (#156 item 7, SECURITY.md). Runs before
            // everything else: a request whose Host header is not one of the
            // three loopback forms on this port, or whose Origin header (when
            // present — a browser doing DNS rebinding, not a normal MCP
            // client) is not an http(s) loopback origin, never reaches a tool.
            // The optional bearer token (PTC_XAE_MCP_TOKEN) is enforced for
            // every path except /health, which stays reachable for a plain
            // liveness probe and reports whether a token is required.
            app.Use(async (ctx, next) =>
            {
                string? hostHeader = ctx.Request.Headers.Host.ToString();
                if (!LocalhostGuard.IsAllowedHost(hostHeader, Port))
                {
                    HostLog.Shared.Warn($"MCP request rejected: Host header '{hostHeader}' is not localhost:{Port}.");
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsync("Forbidden: Host header not allowed.");
                    return;
                }

                string? originHeader = ctx.Request.Headers.Origin.ToString();
                if (!LocalhostGuard.IsAllowedOrigin(originHeader))
                {
                    HostLog.Shared.Warn($"MCP request rejected: Origin header '{originHeader}' is not an http(s) loopback origin.");
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsync("Forbidden: Origin not allowed.");
                    return;
                }

                bool isHealthEndpoint = ctx.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase);
                if (!isHealthEndpoint)
                {
                    string? configuredToken = Environment.GetEnvironmentVariable(LocalhostGuard.TokenEnvVar);
                    string? authHeader = ctx.Request.Headers.Authorization.ToString();
                    if (!LocalhostGuard.IsAuthorized(authHeader, configuredToken))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await ctx.Response.WriteAsync("Unauthorized: missing or invalid bearer token.");
                        return;
                    }
                }

                await next();
            });

            // Per-request keep-alive: bump LastSeen for the calling session so
            // a long-lived but quiet client (e.g. mid-build) is not swept by
            // the TTL. Unknown ids are ignored; the count is unaffected.
            app.Use(async (ctx, next) =>
            {
                var sid = ctx.Request.Headers[McpSessionTracker.SessionIdHeader].ToString();
                if (!string.IsNullOrEmpty(sid))
                    Sessions.Refresh(sid, DateTimeOffset.UtcNow);
                await next();
            });

            // Plain HTTP GET, deliberately NOT MCP-protocol — a client (or a human with
            // curl) needs a way to tell "server not started yet" apart from "server
            // crashed and never restarted" without speaking the MCP handshake, and
            // without guessing a tool call. This is also the minimal documented HTTP
            // fallback form referenced by the MCP-hardening backlog (item 7):
            //   curl http://localhost:5010/health
            // Never touches XAE/COM — reachable even while a COM call elsewhere is
            // wedged, which is exactly the situation this exists to detect from outside.
            app.MapGet("/health", () => Results.Json(new
            {
                status = "ok",
                version = ThisAssemblyVersion(),
                pid = Environment.ProcessId,
                startedAtUtc = _startedAtUtc,
                uptimeSeconds = (int)(DateTime.UtcNow - _startedAtUtc).TotalSeconds,
                connectedSessions = Sessions.Snapshot().Count,
                hostLog = HostLog.Shared.CurrentPath,
                activityLog = Activity.JsonLog.CurrentPath,
                authRequired = LocalhostGuard.AuthRequired(),
            }));

            app.MapMcp("/");
            await app.StartAsync(ct);
            StatusChanged?.Invoke(true, Port);
            HostLog.Shared.Info($"MCP server started on port {Port} (pid {Environment.ProcessId}).");
            _ = Task.Run(() => SweepLoopAsync(ct), ct);
            await app.WaitForShutdownAsync(ct);
            HostLog.Shared.Info("MCP server stopped.");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — window was closed or Stop() called
            HostLog.Shared.Info("MCP server run loop cancelled (normal shutdown).");
        }
        catch (Exception ex)
        {
            // A port-binding failure (AddressInUse) is by far the most common cause
            // here — call it out explicitly rather than a generic "crashed" message
            // (issue #147 item 4), without logging the same exception twice.
            var reason = IsPortBindingFailure(ex)
                ? $"MCP server failed to bind port {Port}"
                : "MCP server terminated unexpectedly";
            HostLog.Shared.WriteException(reason, ex, Thread.CurrentThread.Name);
            Faulted?.Invoke(IsPortBindingFailure(ex)
                ? $"The MCP server could not listen on port {Port} — another program (or another " +
                  $"ptc-xae-mcp) already uses it. MCP clients cannot connect to this window.\n\n{ex.Message}"
                : $"MCP server terminated unexpectedly:\n{ex.Message}");
        }
        finally
        {
            StatusChanged?.Invoke(false, 0);
        }
    }

    /// <summary>
    /// Periodically evicts sessions with no tool activity within
    /// <see cref="SessionTtl"/> — HTTP/SSE clients disconnect without a clean
    /// close and ModelContextProtocol 1.3.0 raises no disconnect event.
    /// </summary>
    private async Task SweepLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(SweepInterval, ct);
                int removed = Sessions.Sweep(SessionTtl, DateTimeOffset.UtcNow);
                if (removed > 0)
                    HostLog.Shared.Info($"Session sweep evicted {removed} stale session(s) (TTL {SessionTtl}).");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>True when the exception chain contains a socket "address already in use" failure.</summary>
    private static bool IsPortBindingFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException se &&
                se.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
                return true;
            if (e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("failed to bind", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The single source of truth for the version reported over MCP (<c>ServerInfo.Version</c>)
    /// and HTTP (<c>/health</c>'s <c>version</c> field). Reads the assembly's informational
    /// version (<c>&lt;Version&gt;</c> in <c>Directory.Build.props</c>, propagated by the SDK
    /// into <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>) rather than
    /// the four-part <c>AssemblyVersion</c>, so both surfaces track the one release number
    /// (issue #157 item 3) instead of drifting independently.
    /// </summary>
    internal static string ThisAssemblyVersion() =>
        typeof(McpBridge).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(McpBridge).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// The version as shown in the GUI (window title and header): the release number plus the
    /// short commit, e.g. <c>1.3.0+6495580d94…</c> → <c>v1.3.0 (6495580)</c>. Built from
    /// <see cref="ThisAssemblyVersion"/> so the GUI, MCP and <c>/health</c> never disagree.
    /// </summary>
    internal static string DisplayVersion(string informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return "v?";
        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
            return "v" + informationalVersion;
        string release = informationalVersion[..plus];
        string commit = informationalVersion[(plus + 1)..];
        if (commit.Length > 7)
            commit = commit[..7];
        return commit.Length == 0 ? "v" + release : $"v{release} ({commit})";
    }

    /// <summary>Opt-in switch for <see cref="RepoReportsDirOrNull"/> (issue #147 item 6).</summary>
    public const string ReportsMirrorEnvVar = "PTC_XAE_MCP_REPORTS_MIRROR";

    /// <summary>
    /// Locates the repo's <c>reports/ptc-xae-mcp/</c> directory by walking up
    /// from the running binary to the checkout root (the folder containing
    /// <c>.git</c>), so per-run logs are mirrored into the repo for automated
    /// analysis. Only active when <see cref="ReportsMirrorEnvVar"/> is set to
    /// <c>"1"</c> — every other developer/CI machine gets only the canonical
    /// %LOCALAPPDATA% sink, never an uninvited write into a git checkout.
    /// Returns null when the flag is unset/not "1", or when not running from a
    /// checkout at all.
    /// </summary>
    internal static string? RepoReportsDirOrNull()
    {
        if (Environment.GetEnvironmentVariable(ReportsMirrorEnvVar) != "1")
            return null;

        try
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
                 dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return Path.Combine(dir.FullName, "reports", "ptc-xae-mcp");
            }
        }
        catch { /* best effort — no mirror */ }
        return null;
    }
}

[McpServerToolType]
internal sealed partial class TwinCatMcpTools(ServerHost host, McpBridge bridge);
