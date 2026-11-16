using PtcXaeMcp.WinForms.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    /// <summary>
    /// Records the calling session into the registry from a tool invocation.
    /// ModelContextProtocol 1.3.0 has no connect event, so every tool call that
    /// can resolve its <see cref="RequestContext{T}"/> registers/refreshes the
    /// session here. Best-effort: never throws into the tool path.
    /// </summary>
    internal static McpClientSession? RecordSession(
        McpSessionRegistry registry, RequestContext<CallToolRequestParams>? ctx)
    {
        var server = ctx?.Server;
        if (server is null)
            return null;

        var info = server.ClientInfo;
        var session = new McpClientSession(
            SessionId: server.SessionId ?? "(unknown)",
            ClientName: string.IsNullOrWhiteSpace(info?.Name) ? "(unknown)" : info!.Name,
            ClientVersion: string.IsNullOrWhiteSpace(info?.Version) ? "(unknown)" : info!.Version,
            NegotiatedProtocolVersion: server.NegotiatedProtocolVersion ?? "(unknown)",
            ConnectedAt: DateTimeOffset.UtcNow);

        registry.Touch(session);
        return session;
    }

    [McpServerTool(Name = "twincat_get_client_info", ReadOnly = true)]
    [Description(
        "Return this session's self-reported MCP client identity and tool profile. " +
        "Client name, version, MCP protocol version, session id, tool profile and access policy. The identity is " +
        "unauthenticated: diagnostics only, never for access control. If the session cannot be resolved, reports the " +
        "most recently connected client and says so (may be stale).")]
    public string GetClientInfo(RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_client_info", null, () =>
        {
            var resolved = RecordSession(bridge.Sessions, context);
            if (resolved is not null)
            {
                return
                    $"Client (this session): {resolved.ClientName} {resolved.ClientVersion}\n" +
                    $"MCP protocol: {resolved.NegotiatedProtocolVersion}\n" +
                    $"Session id: {resolved.SessionId}\n" +
                    $"Tool profile: {bridge.Surface.ProfileFor(resolved.ClientName).Describe()}\n" +
                    $"Access policy: {bridge.Policy.Current.Summary()}\n" +
                    HumanConfirmation.StatusLine(bridge.Policy.Current, context.Server?.ClientCapabilities) + "\n" +
                    "Note: clientInfo is self-reported and unauthenticated; it only picks which tools are listed, never what is allowed.";
            }

            var fallback = bridge.Sessions.MostRecent();
            if (fallback is not null)
            {
                return
                    "Note: per-call session could not be resolved; reporting the " +
                    "most-recently-connected client, which may be stale (may not be you):\n" +
                    $"Client: {fallback.ClientName} {fallback.ClientVersion}\n" +
                    $"MCP protocol: {fallback.NegotiatedProtocolVersion}\n" +
                    $"Session id: {fallback.SessionId}\n" +
                    "Note: clientInfo is self-reported and unauthenticated.";
            }

            return "[Error] No client session could be resolved.";
        }, cancellationToken);
}
