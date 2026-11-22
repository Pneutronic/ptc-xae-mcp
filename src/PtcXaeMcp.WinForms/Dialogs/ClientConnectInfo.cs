using System.Text.Json;
using System.Text.Json.Nodes;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Security;

namespace PtcXaeMcp.WinForms.Dialogs;

/// <summary>
/// What an engineer needs to connect an MCP client to this server (issue #273): the
/// <c>.mcp.json</c> snippet and the <c>claude mcp add</c> command. Pure text generation.
/// The bearer token is NEVER put into any text — when one is configured the snippet only gets a
/// header with the placeholder <see cref="TokenPlaceholder"/>, which the engineer fills in.
/// </summary>
internal static class ClientConnectInfo
{
    public const string ServerName = "ptc-xae";

    /// <summary>Stands in for the token value in generated text.</summary>
    public const string TokenPlaceholder = "<your PTC_XAE_MCP_TOKEN>";

    public static string Url(int port) => $"http://localhost:{port}/";

    /// <summary>The <c>.mcp.json</c> content; with <paramref name="tokenSet"/> it carries an
    /// <c>Authorization: Bearer</c> header with a placeholder instead of the token.</summary>
    public static string McpJsonSnippet(int port, bool tokenSet)
    {
        var server = new JsonObject
        {
            ["url"] = Url(port),
        };
        if (tokenSet)
            server["headers"] = new JsonObject { ["Authorization"] = "Bearer " + TokenPlaceholder };
        var root = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = server } };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The Claude Code CLI command; with <paramref name="tokenSet"/> it adds the header option.</summary>
    public static string ClaudeMcpAddCommand(int port, bool tokenSet) =>
        $"claude mcp add --transport http {ServerName} {Url(port)}"
        + (tokenSet ? $" --header \"Authorization: Bearer {TokenPlaceholder}\"" : string.Empty);

    /// <summary>True when the server currently requires a bearer token (the value is never read out).</summary>
    public static bool TokenIsSet() => LocalhostGuard.AuthRequired();

    /// <summary>One-line outcome of a <c>/health</c> probe for the dialog.</summary>
    public static string DescribeHealth(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? string.Empty);
            var r = doc.RootElement;
            string status = r.TryGetProperty("status", out var s) ? s.ToString() : "?";
            string version = r.TryGetProperty("version", out var v) ? v.ToString() : "?";
            string auth = r.TryGetProperty("authRequired", out var a) && a.ValueKind == JsonValueKind.True
                ? Loc.Get("connect.health.yes") : Loc.Get("connect.health.no");
            return Loc.Format("connect.health.ok", status, version, auth);
        }
        // InvalidOperationException: TryGetProperty on a root that is not an object.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Loc.Get("connect.health.unexpected");
        }
    }
}
