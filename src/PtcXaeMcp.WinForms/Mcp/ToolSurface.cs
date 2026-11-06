using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.Diagnostics;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// Which tools a session sees: its profile (<see cref="ToolProfiles"/>) and the access policy together. tools/list
/// leaves out what the profile hides and what the policy forbids; a call to a tool the profile hides is refused
/// (the policy itself is enforced in <see cref="McpActivity.TraceAsync"/>, so the tool console is covered too).
/// When the window changes the profile or the policy, every connected session is told to reload its tool list.
/// </summary>
internal sealed class ToolSurface
{
    private readonly Func<string, string?> _getEnv;
    private readonly Func<AccessPolicy> _policy;
    private readonly ConcurrentDictionary<string, McpServer> _servers = new(StringComparer.Ordinal);

    public ToolSurface(Func<string, string?> getEnv, Func<AccessPolicy> policy, string? windowProfile)
    {
        _getEnv = getEnv;
        _policy = policy;
        WindowProfile = string.IsNullOrWhiteSpace(windowProfile) ? ToolProfiles.Automatic : windowProfile.Trim();
    }

    /// <summary>The profile chosen in the window: <see cref="ToolProfiles.Automatic"/>, <c>all</c>, <c>core</c> or a toolset list.</summary>
    public string WindowProfile { get; private set; }

    /// <summary>The value of <see cref="ToolProfiles.EnvVar"/>, which overrides the window; null when unset.</summary>
    public string? EnvironmentProfile => _getEnv(ToolProfiles.EnvVar) is { Length: > 0 } v ? v : null;

    public EffectiveProfile ProfileFor(string? clientName) =>
        ToolProfiles.Resolve(EnvironmentProfile, WindowProfile, clientName);

    /// <summary>
    /// Shown in tools/list: the profile shows its toolset and the policy allows its level. Emergency stops ignore
    /// the policy. A name the catalog does not know (none in a release build) is shown.
    /// </summary>
    public bool Visible(string toolName, EffectiveProfile profile)
    {
        if (ToolCatalog.Find(toolName) is not { } tool) return true;
        bool allowed = tool.AlwaysAllowed || tool.Level <= _policy().Level;
        return allowed && profile.Shows(tool.Toolset);
    }

    public void SetWindowProfile(string value)
    {
        WindowProfile = string.IsNullOrWhiteSpace(value) ? ToolProfiles.Automatic : value.Trim();
        HostLog.Shared.Info($"Tool profile set in the window: {WindowProfile}.");
        NotifyToolListChanged();
    }

    /// <summary>tools/list filter: the profile of the calling client and the access policy.</summary>
    public McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListFilter(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            if (context.Server is not { } server) return result;
            Remember(server);
            var profile = ProfileFor(server.ClientInfo?.Name);
            result.Tools = result.Tools.Where(t => Visible(t.Name, profile)).ToList();
            return result;
        };

    /// <summary>Call filter: a tool the client's profile hides is refused with how to show it.</summary>
    public McpRequestHandler<CallToolRequestParams, CallToolResult> CallFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) =>
        {
            if (context.Server is { } server && context.Params?.Name is { } name && ToolCatalog.Find(name) is { } tool)
            {
                Remember(server);
                var profile = ProfileFor(server.ClientInfo?.Name);
                if (!profile.Shows(tool.Toolset))
                    return ValueTask.FromResult(new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = HiddenMessage(tool, profile) }],
                    });
            }
            return next(context, cancellationToken);
        };

    internal static string HiddenMessage(ToolInfo tool, EffectiveProfile profile) =>
        $"[Error] {tool.Name} belongs to the toolset '{tool.Toolset.ToString().ToLowerInvariant()}', which the tool " +
        $"profile {profile.Describe()} does not show. Nothing was done. Choose the tool profile in the ptc-xae-mcp " +
        $"window (Settings) or set {ToolProfiles.EnvVar} (e.g. 'all').";

    /// <summary>Above this many remembered sessions the old ones are forgotten: HTTP clients leave without a close,
    /// and a test run opens thousands. A forgotten session only misses a list-changed notice; the call filter
    /// and the access policy still apply to it.</summary>
    private const int MaxRemembered = 256;

    private void Remember(McpServer server)
    {
        if (server.SessionId is not { Length: > 0 } id) return;
        if (_servers.Count >= MaxRemembered && !_servers.ContainsKey(id))
            _servers.Clear();
        _servers[id] = server;
    }

    /// <summary>Tells every session seen so far to reload its tool list. Fire and forget; a gone session is dropped.</summary>
    public void NotifyToolListChanged()
    {
        foreach (var (id, server) in _servers)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _servers.TryRemove(id, out _);
                }
            });
        }
    }
}
