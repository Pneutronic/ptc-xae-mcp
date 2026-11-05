using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Shared "the (re)opened PLC project is usable" wait for the PLC-editing suites. The XAE loads a lot
/// on (re)open — and after activate_config the PLC tree can be locked for a while — so the first
/// listings are often the transient empty/not-ready view. Polls <c>twincat_plc_list</c> until it
/// returns a real POU line (issue #157 item 7: condition polling instead of an initial fixed settle).
/// Returns the last listing; a never-settled project surfaces in the scenario's own assertion.
/// </summary>
internal static class PlcProjectReadiness
{
    /// <summary>Upper bound of the wait; the old fixed-settle loop allowed ~3 s + 18 × 5 s.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(95);

    public static Task<string> AwaitAsync(McpClient client) =>
        Poll.UntilAsync(
            async () =>
            {
                var result = await client.CallToolAsync("twincat_plc_list");
                string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                return result.IsError == true ? "[Error] (protocol error) " + text : text;
            },
            IsReady, Timeout, TimeSpan.FromSeconds(2));

    /// <summary>
    /// True for a listing with at least one POU line (<c>"  POU/PROGRAM  TIPC^…"</c>) and none of the
    /// known not-ready markers.
    /// </summary>
    public static bool IsReady(string listing)
    {
        bool transient =
            listing.Contains("No matching PLC elements found", StringComparison.Ordinal) ||
            listing.StartsWith("[Error]", StringComparison.Ordinal);
        return !transient && System.Text.RegularExpressions.Regex.IsMatch(listing, @"\bPOU(?:/\S+)?\s+TIPC\^");
    }
}
