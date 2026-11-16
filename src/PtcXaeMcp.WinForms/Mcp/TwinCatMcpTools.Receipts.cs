using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.Mcp;
using System.ComponentModel;
using System.Text.Json;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    // Reads the receipt store only: it must answer while another call runs, so it takes no dispatcher lock.
    [McpServerTool(Name = "twincat_receipt_get", ReadOnly = true)]
    [Description("Show the receipt of a request that carried a requestKey. " +
                 "It names the tool, the start and finish time, the target, the outcome (running, ok, error or unknown) and " +
                 "the stored result. Use it after a timeout: running means the " +
                 "host is still at it, unknown means it never saw the call finish (look at the live state before you run it " +
                 "again with a new key). Returns JSON { requestKey, tool, targetNetId, startedUtc, finishedUtc, outcome, " +
                 "resultText } or [Error] when there is no receipt (never used, older than 7 days, or refused before it ran).")]
    public string ReceiptGet(
        [Description("The requestKey of the call, e.g. the UUID passed to twincat_activate_config.")] string requestKey,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_receipt_get", new { requestKey }, () =>
        {
            if (string.IsNullOrWhiteSpace(requestKey))
                return "[Error] requestKey is required.";
            if (bridge.Receipts.Get(requestKey.Trim()) is not { } receipt)
                return $"[Error] No receipt for requestKey '{requestKey.Trim()}': the key was never used, its receipt is older than " +
                       $"{(int)ReceiptStore.Retention.TotalDays} days, or the call was refused before it ran.";
            return JsonSerializer.Serialize(new
            {
                requestKey = receipt.RequestKey,
                tool = receipt.Tool,
                targetNetId = receipt.TargetNetId,
                startedUtc = receipt.StartedUtc,
                finishedUtc = receipt.FinishedUtc,
                outcome = Receipt.Name(receipt.Outcome),
                resultText = receipt.ResultText,
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }, cancellationToken);
}
