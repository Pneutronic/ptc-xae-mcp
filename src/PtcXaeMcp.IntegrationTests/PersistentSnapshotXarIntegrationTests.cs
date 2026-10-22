using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAR — persistent-snapshot against a live PLC runtime. Preconditions as
/// for <see cref="IbnLiveIntegrationTests"/>: the neutral fixture activated on the stage's explicit runtime target
/// and in Run, a --diagnostics MCP server on port 5010 (the human-confirm step has no MCP tool; the live suite uses
/// the diagnostics-only <c>twincat_persistent_snapshot_test_confirm</c>, like <c>twincat_ibn_arm</c>).
/// Every call passes the target as <c>amsNetId</c> (issue #195).
/// </summary>
[Collection("Stage XAR")]
[Trait("Stage", "XAR")]
public class PersistentSnapshotXarIntegrationTests : IAsyncLifetime
{
    private readonly XarRuntimeFixture _xar;
    private McpClient? _client;

    private static readonly string Recipe = FixtureLayout.PersistentGvlName + ".stRecipe";
    private string _snapshotFile = string.Empty;

    public PersistentSnapshotXarIntegrationTests(XarRuntimeFixture xar) => _xar = xar;

    public async Task InitializeAsync()
    {
        _snapshotFile = Path.Combine(Path.GetTempPath(), $"ptc-snapshot-live-{Guid.NewGuid():N}.json");
        if (!_xar.IsReady) return;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(LiveXae.ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch { /* _client stays null; SkipIfNotReady gates tests */ }
    }

    public async Task DisposeAsync()
    {
        try { File.Delete(_snapshotFile); } catch (IOException) { }
        if (_client is not null) await _client.DisposeAsync();
    }

    private void SkipIfNotReady()
    {
        _xar.SkipIfNotReady();
        if (_client is null) throw new SkipException("persistent-snapshot: MCP client could not be created for Stage XAR.");
    }

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        args["amsNetId"] = _xar.AmsNetId;
        var result = await _client!.CallToolAsync(tool, args,
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Console.WriteLine($"[persistent-snapshot] {tool}: {(text.Length > 600 ? text[..600] + "…" : text)}");
        return text;
    }

    private static JsonDocument Json(string text)
    {
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        return JsonDocument.Parse(text);
    }

    [Trait("Description", "list_ads_symbols(persistentOnly) findet die PERSISTENT-Symbole der Fixture; RETAIN ist über ADS nicht markiert.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ListSymbols_PersistentOnly_FindsTheFixtureRoots()
    {
        SkipIfNotReady();
        using var doc = Json(await CallAsync("twincat_list_ads_symbols",
            new() { ["persistentOnly"] = true, ["maxDepth"] = 1 }));

        var paths = doc.RootElement.GetProperty("symbols").EnumerateArray()
            .Select(s => s.GetProperty("path").GetString()!).ToList();
        Assert.Contains(paths, p => p.StartsWith(Recipe, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Equals(FixtureLayout.RetainSymbol, StringComparison.OrdinalIgnoreCase));
    }

    [Trait("Description", "save (roots) → Werte per write_ads_variable ändern → compare meldet genau diese → dry-run-Plan → restore + Test-Confirm → compare gleich; ohne Confirm bleibt alles unverändert.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task SaveChangeCompareRestore_RoundTripsThroughTheHumanGate()
    {
        SkipIfNotReady();
        string count = Recipe + ".stInner.nCount";
        string name = Recipe + ".sName";

        // 1. Save the recipe subtree.
        using (var save = Json(await CallAsync("twincat_persistent_snapshot_save",
                   new() { ["selection"] = "roots", ["roots"] = new[] { Recipe }, ["path"] = _snapshotFile })))
        {
            Assert.Equal(12, save.RootElement.GetProperty("leafCount").GetInt32());
            Assert.Equal("roots", save.RootElement.GetProperty("selectionSource").GetString());
        }
        Assert.True(File.Exists(_snapshotFile));

        // 2. Read the originals, change two leaves.
        string originalCount = await ReadValueAsync(count);
        string originalName = await ReadValueAsync(name);
        string changedCount = originalCount == "77" ? "78" : "77";
        try
        {
            Assert.DoesNotContain("[Error]", await CallAsync("twincat_write_ads_variable", new() { ["symbolPath"] = count, ["value"] = changedCount }));
            Assert.DoesNotContain("[Error]", await CallAsync("twincat_write_ads_variable", new() { ["symbolPath"] = name, ["value"] = "changed" }));

            // 3. Compare names exactly those two.
            using (var cmp = Json(await CallAsync("twincat_persistent_snapshot_compare",
                       new() { ["snapshotPath"] = _snapshotFile, ["tmcPath"] = null })))
            {
                var diffs = cmp.RootElement.GetProperty("differences").EnumerateArray()
                    .Select(d => d.GetProperty("path").GetString()!).OrderBy(p => p, StringComparer.Ordinal).ToList();
                Assert.Equal(new[] { count, name }.OrderBy(p => p, StringComparer.Ordinal).ToList(), diffs);
            }

            // 4. Dry run: plan with a hash, nothing written.
            string planHash;
            using (var dry = Json(await CallAsync("twincat_persistent_snapshot_restore",
                       new() { ["snapshotPath"] = _snapshotFile })))
            {
                Assert.True(dry.RootElement.GetProperty("dryRun").GetBoolean());
                Assert.Equal(2, dry.RootElement.GetProperty("writeCount").GetInt32());
                planHash = dry.RootElement.GetProperty("planHash").GetString()!;
            }
            Assert.Equal("changed", (await ReadValueAsync(name)).Trim('"'));

            // 5. A stale hash is refused.
            Assert.StartsWith("[Error]", await CallAsync("twincat_persistent_snapshot_restore",
                new() { ["snapshotPath"] = _snapshotFile, ["dryRun"] = false, ["planHash"] = "deadbeef" }));

            // 6. Propose: pending, nothing written yet.
            string restoreId;
            using (var proposed = Json(await CallAsync("twincat_persistent_snapshot_restore",
                       new() { ["snapshotPath"] = _snapshotFile, ["dryRun"] = false, ["planHash"] = planHash })))
            {
                Assert.Equal("pending", proposed.RootElement.GetProperty("status").GetString());
                restoreId = proposed.RootElement.GetProperty("restoreId").GetString()!;
            }
            Assert.Equal("changed", (await ReadValueAsync(name)).Trim('"'));

            // 7. The human step (diagnostics-only hook), then the report with read-back values.
            using (var confirmed = Json(await CallAsync("twincat_persistent_snapshot_test_confirm",
                       new() { ["restoreId"] = restoreId })))
                Assert.Equal("executed", confirmed.RootElement.GetProperty("status").GetString());

            using (var status = Json(await CallAsync("twincat_persistent_snapshot_restore_status",
                       new() { ["restoreId"] = restoreId })))
            {
                var report = status.RootElement.GetProperty("report");
                Assert.Equal(2, report.GetProperty("written").GetInt32());
                Assert.Equal(0, report.GetProperty("failed").GetInt32());
            }

            // 8. Restored: compare equal on the saved subtree.
            using var after = Json(await CallAsync("twincat_persistent_snapshot_compare",
                new() { ["snapshotPath"] = _snapshotFile }));
            Assert.Equal(0, after.RootElement.GetProperty("differenceCount").GetInt32());
            Assert.Equal(originalName, await ReadValueAsync(name));
        }
        finally
        {
            // Leave the fixture as found even if an assertion failed half way.
            await CallAsync("twincat_write_ads_variable", new() { ["symbolPath"] = count, ["value"] = originalCount });
            await CallAsync("twincat_write_ads_variable", new() { ["symbolPath"] = name, ["value"] = originalName.Trim('"') });
        }
    }

    [Trait("Description", "RETAIN-Variable GVL_InstanceSettings.nRetainCounter ist über roots[] speicherbar (ADS markiert RETAIN nicht).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Save_RetainVariable_ViaExplicitRoots()
    {
        SkipIfNotReady();
        using var save = Json(await CallAsync("twincat_persistent_snapshot_save",
            new() { ["selection"] = "roots", ["roots"] = new[] { FixtureLayout.RetainSymbol }, ["path"] = _snapshotFile }));
        Assert.Equal(1, save.RootElement.GetProperty("leafCount").GetInt32());
    }

    private async Task<string> ReadValueAsync(string symbol)
    {
        using var doc = Json(await CallAsync("twincat_read_ads_variable", new() { ["symbolPath"] = symbol }));
        return doc.RootElement.GetProperty("value").GetRawText();
    }
}
