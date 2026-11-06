using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// MCP resources: context a user pulls into the chat by URI (in Claude Code with <c>@ptc-xae:…</c>) without a tool
/// call. Each read goes through the matching tool, so the access policy, the single-operation lock and the activity
/// log apply exactly as for that tool.
/// </summary>
[McpServerResourceType]
internal sealed class McpResources(ServerHost host, McpBridge bridge)
{
    public const string Scheme = "ptc-xae://";
    public const string ElementTemplate = Scheme + "plc/element/{path}";

    private TwinCatMcpTools Tools => new(host, bridge);

    [McpServerResource(UriTemplate = Scheme + "status", Name = "status", Title = "TwinCAT status", MimeType = "text/plain")]
    [Description("XAE, open project and PLC, TwinCAT System Service, access policy and the tool profile of this client.")]
    public string Status(McpServer server) =>
        Tools.GetStatus() + $"\nTool profile: {bridge.Surface.ProfileFor(server.ClientInfo?.Name).Describe()}";

    [McpServerResource(UriTemplate = Scheme + "plc/elements", Name = "plc-elements", Title = "PLC elements", MimeType = "text/plain")]
    [Description("Every POU, INTERFACE, DUT, GVL and folder of the open PLC project with its tree path (as twincat_plc_list).")]
    public string PlcElements() => Tools.PlcList();

    [McpServerResource(UriTemplate = ElementTemplate, Name = "plc-element", Title = "PLC element", MimeType = "text/plain")]
    [Description("Declaration, implementation and revision of one PLC element (as twincat_plc_read); path is its caret-separated " +
                 "tree path, URI-encoded, e.g. ptc-xae://plc/element/TIPC%5EMyPlc%5EMyPlc%20Project%5EPOUs%5EMAIN.")]
    public string PlcElement([Description("Caret-separated tree path of the element, URI-encoded.")] string path)
    {
        string treePath = DecodePath(path);
        string? kind = KindAt(treePath, out string? error);
        return error ?? Tools.PlcRead(treePath, kind!);
    }

    [McpServerResource(UriTemplate = Scheme + "build/errors", Name = "build-errors", Title = "Error List", MimeType = "text/plain")]
    [Description("Errors and warnings of the XAE Error List (as twincat_get_error_list with minLevel='warning').")]
    public string BuildErrors() => Tools.GetErrorList(minLevel: "warning");

    [McpServerResource(UriTemplate = Scheme + "toolsets", Name = "toolsets", Title = "Toolsets", MimeType = "text/plain")]
    [Description("Which tools belong to which toolset, and which toolsets the tool profile of this client shows.")]
    public string Toolsets(McpServer server) => DescribeToolsets(bridge.Surface.ProfileFor(server.ClientInfo?.Name));

    /// <summary>A template argument arrives percent-encoded ('^' as %5E, a space as %20); a decoded one passes unchanged.</summary>
    internal static string DecodePath(string path) => Uri.UnescapeDataString(path ?? string.Empty).Trim();

    // The element's kind from its node, so the resource needs no kind argument. Under the single-operation lock like
    // a tool; the read that follows is the traced one (twincat_plc_read), so the probe is not logged a second time.
    private string? KindAt(string treePath, out string? error)
    {
        error = null;
        try
        {
            string kindOrError = bridge.Dispatcher.Run(() => Describe(bridge.Dispatcher.Session.ProbePlcNode(treePath)));
            if (McpErrors.IsError(kindOrError)) { error = kindOrError; return null; }
            return kindOrError;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = McpErrors.EnsureError(ComCall.Describe(ex));
            return null;
        }

        string Describe(PlcNodeInfo info) =>
            !info.Exists ? McpErrors.WithNext($"PLC tree path not found: {treePath}", PlcKindResolver.NotFoundHint)
            : info.ItemType is int t && PlcKindResolver.KindOf(t) is { } kind ? kind
            : $"[Error] '{treePath}' is no PLC element that can be read.";
    }

    internal static string DescribeToolsets(EffectiveProfile profile)
    {
        var sb = new StringBuilder();
        sb.Append("Tool profile: ").AppendLine(profile.Describe());
        foreach (var group in ToolCatalog.Tools.Values.GroupBy(t => t.Toolset).OrderBy(g => g.Key))
        {
            string state = profile.Shows(group.Key) ? "shown" : "hidden";
            string tools = string.Join(", ", group.Select(t => t.Name).Order(StringComparer.Ordinal));
            sb.AppendLine(CultureInfo.InvariantCulture, $"{group.Key.ToString().ToLowerInvariant()} ({state}, {group.Count()} tools): {tools}");
        }
        return sb.ToString().TrimEnd();
    }
}
