namespace PtcXaeMcp.WinForms;

/// <summary>
/// Central error-detection contract for the MCP string protocol.
/// All tools and coordinators use IsError() rather than scattered
/// StartsWith("[Error]") literals so the predicate lives in one place.
/// </summary>
internal static class McpErrors
{
    private const string Prefix = "[Error]";

    /// <summary>
    /// Canonical message for a call that was cancelled — either parked at the
    /// test gate or cancelled mid-flight. Always carries the <see cref="Prefix"/>
    /// so <see cref="IsError"/> treats cancellation as a failure, never success.
    /// </summary>
    internal const string CancelledMessage = "[Error] Cancelled";

    /// <summary>Builds a cancelled-call result with an explanatory suffix.</summary>
    internal static string Cancelled(string reason) => $"{CancelledMessage}: {reason}";

    /// <summary>Returns true when <paramref name="result"/> is an MCP error string.</summary>
    internal static bool IsError(string? result)
        => result?.StartsWith(Prefix, StringComparison.Ordinal) ?? false;

    /// <summary>Returns true when <paramref name="result"/> is a successful MCP string.</summary>
    internal static bool IsOk(string? result) => !IsError(result);

    /// <summary>The line that names the next step after an error.</summary>
    internal const string NextPrefix = "Next: ";

    /// <summary>
    /// <c>[Error] message</c> plus a second line <c>Next: hint</c> with the concrete next step, as text so every
    /// client reads it. A message that already has a Next line keeps it.
    /// </summary>
    internal static string WithNext(string error, string hint)
    {
        string text = EnsureError(error);
        return text.Contains("\n" + NextPrefix, StringComparison.Ordinal) ? text : $"{text}\n{NextPrefix}{hint}";
    }

    /// <summary>
    /// Ensures <paramref name="result"/> carries the [Error] prefix exactly once.
    /// Pass-through when already prefixed (including a result that already
    /// starts with a repeated prefix, which is collapsed to a single one);
    /// wraps bare messages otherwise.
    /// </summary>
    internal static string EnsureError(string result)
    {
        if (string.IsNullOrEmpty(result))
            return $"{Prefix} {result}";

        string trimmed = result;
        bool hadPrefix = false;
        while (trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            hadPrefix = true;
            trimmed = trimmed[Prefix.Length..].TrimStart();
        }

        return hadPrefix ? $"{Prefix} {trimmed}" : $"{Prefix} {result}";
    }
}
