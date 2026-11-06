using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Shared helper for the one safe, build-reachable mutation target the live
/// suite is allowed to touch: a dedicated <c>PRG_IntegrationScratch</c> PROGRAM
/// that is called once from MAIN via a sentinel-tagged hook line.
///
/// <para><b>Why this exists.</b> A throwaway, task-unassigned POU is never
/// compiled by TwinCAT (feedback_unreferenced_pous_not_compiled), so injecting
/// a build error into it does nothing. The only way to exercise real build
/// failures is via a task-reachable POU. Mutating the real MAIN to do that
/// corrupted a production project (feedback_integration_never_mutate_shared_main).
/// The resolution: a permanent scratch POU with no real logic, reached through
/// exactly one appended, clearly-tagged MAIN line. Tests freely
/// overwrite/restore the scratch POU's body; MAIN is only ever touched by the
/// idempotent one-line hook (append-if-missing), never parsed/round-tripped.</para>
///
/// <para><b>Portability.</b> <see cref="EnsureAsync"/> bootstraps the scratch
/// POU and the MAIN hook if absent, so the suite works across many different
/// fixture projects without per-project manual setup.</para>
/// </summary>
public static class ScratchPou
{
    public static string ParentPath => FixtureLayout.PousFolder;
    public const string Name = "PRG_IntegrationScratch";
    public static string Path => ParentPath + "^" + Name;
    public static string MainPath => FixtureLayout.MainPath;

    public const string SafeDecl = "PROGRAM " + Name + "\nVAR\nEND_VAR";
    public const string SafeImpl =
        "; // integration-test scratch POU. Body is freely overwritten and " +
        "restored by the live test suite. Never put real logic here.";

    private const string HookTag = "[INTEGRATION-TEST-HOOK]";
    private const string HookLine =
        Name + "(); // " + HookTag + " do not remove manually";

    private static string Text(CallToolResult r)
    {
        var c = r.Content;
        if (c is null || c.Count == 0) return string.Empty;
        return string.Join("\n", c.OfType<TextContentBlock>().Select(x => x.Text));
    }

    // The tools report a refused write as "[Error] ..." text, not as a protocol error.
    private static bool Failed(CallToolResult r) =>
        r.IsError == true || Text(r).StartsWith("[Error]", StringComparison.Ordinal);

    private static string Section(string read, string startMarker, string? endMarker)
    {
        int s = read.IndexOf(startMarker, StringComparison.Ordinal);
        if (s < 0) return string.Empty;
        s += startMarker.Length;
        int e = endMarker is null ? read.Length : read.IndexOf(endMarker, s, StringComparison.Ordinal);
        if (e < 0) e = read.Length;
        return read.Substring(s, e - s).Trim('\r', '\n', ' ');
    }

    /// <summary>
    /// True for the transient COM/DTE condition on a just-reopened project:
    /// the XAE editor is not yet fully loaded so the <c>DeclarationText</c>
    /// dispatch-id lookup fails (<c>0x8001010A</c> /
    /// <c>RPC_E_SERVERCALL_RETRYLATER</c>). An immediate retry succeeds — this
    /// is a timing artifact, not a missing/wrong node (issue #76, #66 family).
    /// A genuine "not found" error does NOT match, so the create path below
    /// still runs when the scratch POU truly does not exist yet.
    /// </summary>
    private static bool IsEditorNotReady(string read) =>
        read.StartsWith("[Error]", StringComparison.Ordinal) &&
        (read.Contains("0x8001010A", StringComparison.OrdinalIgnoreCase) ||
         read.Contains("Could not get dispatch ID", StringComparison.OrdinalIgnoreCase) ||
         read.Contains("not fully loaded", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>twincat_plc_read(kind=POU)</c> with a bounded retry on the post-reopen
    /// editor-not-ready transient (issue #76). Returns the last response so a
    /// genuine error (or a never-settled editor) still surfaces to the caller.
    /// </summary>
    private static async Task<string> ReadPouResilient(
        McpClient client, string treePath, int maxAttempts = 6)
    {
        string read = string.Empty;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            read = Text(await client.CallToolAsync("twincat_plc_read",
                new Dictionary<string, object?> { ["path"] = treePath, ["kind"] = "POU" }));
            if (!IsEditorNotReady(read))
                return read;
            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }
        return read;
    }

    /// <summary>
    /// Ensure the scratch POU exists with its safe body AND that MAIN calls it
    /// via the tagged hook line. Idempotent: only appends the hook if missing,
    /// never rewrites MAIN's declaration or existing body.
    /// </summary>
    public static async Task EnsureAsync(McpClient client)
    {
        // 1. Scratch POU — create if missing, always (re)set to the safe body.
        var read = await ReadPouResilient(client, Path);
        if (read.StartsWith("[Error]", StringComparison.Ordinal))
        {
            var create = await client.CallToolAsync("twincat_plc_create",
                new Dictionary<string, object?>
                {
                    ["kind"] = "POU", ["subtype"] = "PROGRAM", ["parentPath"] = ParentPath, ["name"] = Name,
                });
            Assert.False(create.IsError == true || Text(create).StartsWith("[Error]", StringComparison.Ordinal),
                Text(create));
        }
        await ResetAsync(client);

        // 2. MAIN hook — append the single tagged line only if not present.
        var mainRead = await ReadPouResilient(client, MainPath);
        Assert.False(mainRead.StartsWith("[Error]", StringComparison.Ordinal),
            $"read MAIN failed: {mainRead}");
        if (!mainRead.Contains(HookTag, StringComparison.Ordinal))
        {
            var impl = Section(mainRead, "--- Implementation ---", null);
            var w = await client.CallToolAsync("twincat_plc_write",
                new Dictionary<string, object?>
                {
                    ["path"] = MainPath,
                    ["kind"] = "POU",
                    ["implementation"] = impl.TrimEnd() + "\r\n\r\n" + HookLine,
                });
            Assert.False(Failed(w), $"MAIN hook append failed: {Text(w)}");
        }
    }

    /// <summary>Restore the scratch POU to its harmless safe body.</summary>
    public static async Task ResetAsync(McpClient client)
    {
        var w = await client.CallToolAsync("twincat_plc_write",
            new Dictionary<string, object?>
            {
                ["path"] = Path,
                ["kind"] = "POU",
                ["declaration"] = SafeDecl,
                ["implementation"] = SafeImpl,
            });
        Assert.False(Failed(w), $"scratch reset failed: {Text(w)}");
    }

    /// <summary>
    /// Deterministically return the scratch POU to a clean, building state and
    /// verify it. Resets the safe body, clears the XAE Error List, then
    /// save+builds with a bounded retry, returning the final build text for the
    /// caller to assert <c>Build succeeded</c>.
    ///
    /// <para><b>Why (issue #70, same state-bleed family as #66 / #68).</b> The
    /// post-injection restore-rebuild flaked in full-suite order: a populated
    /// Error List left by a prior failed build — this test's or a sibling's —
    /// was read back as the verdict for the now-clean project, and a
    /// reopen/save-timing window could make the first restore build report
    /// <c>Build failed</c> even though the body is safe. Clearing the Error
    /// List removes the stale-bleed; the bounded retry rides out the transient
    /// settle window. Order-independent, so it cannot inherit sibling state.</para>
    /// </summary>
    public static async Task<string> RestoreAndVerifyBuildAsync(
        McpClient client, int maxAttempts = 4)
    {
        string build = string.Empty;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await ResetAsync(client);

            // Best-effort: the rebuild below is the real gate, so a clear that
            // finds nothing (or transiently can't run) must not fail the test.
            try { await client.CallToolAsync("twincat_clear_error_list"); }
            catch { /* advisory only */ }

            build = await SaveAndBuildAsync(client);
            if (build.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase))
                return build;

            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(3));
        }
        return build;
    }

    /// <summary>Overwrite the scratch POU's declaration + implementation.</summary>
    public static async Task WriteAsync(McpClient client, string decl, string impl)
    {
        var w = await client.CallToolAsync("twincat_plc_write",
            new Dictionary<string, object?>
            {
                ["path"] = Path, ["kind"] = "POU", ["declaration"] = decl, ["implementation"] = impl,
            });
        Assert.False(Failed(w), $"scratch write failed: {Text(w)}");
    }

    /// <summary>
    /// Save the project, then build the PLC, returning the build text.
    /// Saving first is required: twincat_build_plc compiles the on-disk
    /// project, so unsaved tree edits (a just-broken scratch POU) would
    /// otherwise build stale and the expected error would not appear
    /// (per user guidance 2026-05-16 — "save the project before building").
    /// </summary>
    public static async Task<string> SaveAndBuildAsync(McpClient client)
    {
        var save = await client.CallToolAsync("twincat_save_project",
            new Dictionary<string, object?>());
        Assert.False(save.IsError == true, $"save before build failed: {Text(save)}");
        return Text(await client.CallToolAsync("twincat_build_plc",
            new Dictionary<string, object?>()));
    }
}
