using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Opens the fixture .sln, adds Tc2_MC2 via twincat_add_library_reference,
/// asserts it appears in list + check_installed, then removes it to restore
/// the fixture. Skips unless PTC_XAE_MCP_FIXTURE_SLN is set and the MCP
/// server is reachable on http://localhost:5010.
///
/// <para><b>Restore in <c>finally</c> (issue #178).</b> Both tests change the fixture's library
/// references, and a reference the compiler cannot resolve leaves the per-run fixture
/// uncompilable — every later Build test then fails as a knock-on. Each test therefore records
/// the reference list and a byte copy of the project files first, and its <c>finally</c> puts the
/// fixture back: through the tools, and when the reference list still differs, by closing the
/// solution without saving, writing the project files back and reopening it. A test whose body
/// passed fails when the fixture could not be restored; a failing body keeps its own failure and
/// the restore outcome is logged.</para>
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class LibraryManageReferencesIntegrationTests : IAsyncLifetime
{
    private McpClient? _client;
    private bool _ready;
    private string _fixtureSln = string.Empty;

    public async Task InitializeAsync()
    {
        await LiveXae.EnsureLiveAsync();
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        _fixtureSln = fixture;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(LiveXae.ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            _ready = true;
        }
        catch { _ready = false; }
        LiveXae.Announce(_ready, _fixtureSln);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    private void SkipIfNotReady() => LiveXae.GuardReady(_ready);

    [Trait("Description", "Bibliotheksreferenz hinzufügen/prüfen/entfernen — Tc2_MC2 add→check→remove, Fixture wird im finally wiederhergestellt.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task AddCheckRemove_Tc2MC2_RestoresFixture()
    {
        SkipIfNotReady();

        const string LibName = "Tc2_MC2";
        const string Company = "Beckhoff Automation GmbH";

        await OpenFixtureAsync();

        // Record the initial state: reference list and the project files on disk.
        string initialList = await ListReferencesAsync();
        bool wasAlreadyReferenced = LibraryReferenceListing.Contains(initialList, LibName);
        var snapshot = ProjectFileSnapshot.Capture(_fixtureSln);

        bool bodyFailed = false;
        string? restoreFailure = null;
        try
        {
            if (!wasAlreadyReferenced)
            {
                // ADD
                var addResult = await _client!.CallToolAsync("twincat_add_library_reference",
                    new Dictionary<string, object?>
                    {
                        ["name"] = LibName,
                        ["version"] = "*",
                        ["company"] = Company,
                    });
                var addText = GetText(addResult);
                Assert.False(addResult.IsError == true, $"add_library_reference failed: {addText}");
                bool addOk = addText.Contains("Added", StringComparison.OrdinalIgnoreCase)
                    || addText.Contains("already referenced", StringComparison.OrdinalIgnoreCase);
                Assert.True(addOk, $"Unexpected add response: {addText}");
            }

            // ASSERT: Tc2_MC2 appears in list_library_references
            var listText = await ListReferencesAsync();
            Assert.True(LibraryReferenceListing.Contains(listText, LibName), $"{LibName} not listed:\n{listText}");

            // ASSERT: check_libraries_installed reports Tc2_MC2 as Installed
            var checkResult = await _client!.CallToolAsync("twincat_check_libraries_installed");
            var checkText = GetText(checkResult);
            // Find the line for Tc2_MC2 — it should be [Installed], not [Missing]
            bool mc2Line = checkText.Contains(LibName, StringComparison.OrdinalIgnoreCase);
            if (mc2Line)
            {
                var lines = checkText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var mc2StatusLine = Array.Find(lines, l =>
                    l.Contains(LibName, StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(mc2StatusLine);
                Assert.Contains("[Installed]", mc2StatusLine, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            bodyFailed = true;
            throw;
        }
        finally
        {
            // RESTORE — whatever the body did or failed to do: remove Tc2_MC2 whenever it is
            // listed now but was not before (an add that reported an error may still have landed).
            restoreFailure = await RestoreFixtureAsync(initialList, snapshot, async () =>
            {
                if (wasAlreadyReferenced) return;
                if (!LibraryReferenceListing.Contains(await ListReferencesAsync(), LibName)) return;
                var removeResult = await _client!.CallToolAsync("twincat_remove_library_reference",
                    new Dictionary<string, object?> { ["name"] = LibName });
                Console.WriteLine($"  remove_library_reference: {GetText(removeResult).Trim()}");
            });
            ReportRestore(restoreFailure, bodyFailed);
        }

        Assert.True(restoreFailure is null, $"Fixture not restored: {restoreFailure}");
    }

    [Trait("Description", "add_library_reference(asPlaceholder, plcProjectName) — Tc2_Utilities als Platzhalter des Fixture-PLC: gelistet und effektiv aufgelöst, danach entfernt; ein unbekannter PLC-Name ist ein [Error].")]
    [SkippableFact(Timeout = 300_000)]
    public async Task AddAsPlaceholder_ForTheNamedPlc_ResolvesAndIsRemoved()
    {
        SkipIfNotReady();

        const string LibName = "Tc2_Utilities";
        await OpenFixtureAsync();

        string unknown = GetText(await _client!.CallToolAsync("twincat_list_library_references",
            new Dictionary<string, object?> { ["plcProjectName"] = "NoSuchPlc" }));
        Assert.StartsWith("[Error] PLC project 'NoSuchPlc' not found", unknown);

        string initialList = await ListReferencesAsync();
        bool wasAlreadyReferenced = LibraryReferenceListing.Contains(initialList, LibName);
        var snapshot = ProjectFileSnapshot.Capture(_fixtureSln);

        bool bodyFailed = false;
        string? restoreFailure = null;
        try
        {
            if (!wasAlreadyReferenced)
            {
                string added = GetText(await _client.CallToolAsync("twincat_add_library_reference", new Dictionary<string, object?>
                {
                    ["name"] = LibName, ["company"] = "Beckhoff Automation GmbH", ["asPlaceholder"] = true,
                    ["plcProjectName"] = FixtureLayout.PlcName,
                }));
                Assert.StartsWith($"Added '{LibName}'", added);
            }

            string listed = GetText(await _client.CallToolAsync("twincat_list_library_references",
                new Dictionary<string, object?> { ["plcProjectName"] = FixtureLayout.PlcName }));
            string? line = listed.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(LibName + ":", StringComparison.Ordinal));
            Assert.True(line is not null, $"{LibName} not listed:\n{listed}");
            Assert.Contains("effective]", line);
        }
        catch
        {
            bodyFailed = true;
            throw;
        }
        finally
        {
            restoreFailure = await RestoreFixtureAsync(initialList, snapshot, async () =>
            {
                if (wasAlreadyReferenced) return;
                if (!LibraryReferenceListing.Contains(await ListReferencesAsync(), LibName)) return;
                var removeResult = await _client!.CallToolAsync("twincat_remove_library_reference",
                    new Dictionary<string, object?> { ["name"] = LibName, ["plcProjectName"] = FixtureLayout.PlcName });
                Console.WriteLine($"  remove_library_reference: {GetText(removeResult).Trim()}");
            });
            ReportRestore(restoreFailure, bodyFailed);
        }

        Assert.True(restoreFailure is null, $"Fixture not restored: {restoreFailure}");
    }

    [Trait("Description", "set_library_version — vorhandene Referenz neu versionieren; Fixture wird im finally wiederhergestellt.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task SetVersion_ExistingReference_RestoresOriginal()
    {
        SkipIfNotReady();

        await OpenFixtureAsync();

        // Get current references — need at least one to run this test.
        var listText = await ListReferencesAsync();
        var refs = LibraryReferenceListing.Parse(listText);
        if (refs.Count == 0)
            throw new SkipException("No library references in fixture — skipping set_version test.");

        // Pick the first referenced library from the list.
        var target = refs[0];
        string originalVersion = string.IsNullOrEmpty(target.Version) ? "*" : target.Version;
        var snapshot = ProjectFileSnapshot.Capture(_fixtureSln);

        bool bodyFailed = false;
        string? restoreFailure = null;
        try
        {
            // Set version to "*" (latest); the finally restores the original version.
            var setResult = await _client!.CallToolAsync("twincat_set_library_version",
                new Dictionary<string, object?>
                {
                    ["name"] = target.Name,
                    ["version"] = "*",
                    ["company"] = target.Publisher,
                });
            var setText = GetText(setResult);
            bool setOk = setText.Contains("Set", StringComparison.OrdinalIgnoreCase)
                || setText.StartsWith("[Error]", StringComparison.Ordinal);
            Assert.True(setOk, $"Unexpected set_library_version response: {setText}");
        }
        catch
        {
            bodyFailed = true;
            throw;
        }
        finally
        {
            restoreFailure = await RestoreFixtureAsync(listText, snapshot, async () =>
            {
                // Nothing to undo when the references are unchanged (the fixture's are all '*'):
                // a redundant set_library_version keeps the XAE busy resolving libraries.
                if (LibraryReferenceListing.Difference(listText, await ListReferencesAsync()) is null) return;
                var r = await _client!.CallToolAsync("twincat_set_library_version",
                    new Dictionary<string, object?>
                    {
                        ["name"] = target.Name,
                        ["version"] = originalVersion,
                        ["company"] = target.Publisher,
                    });
                Console.WriteLine($"  set_library_version (restore {target.Name} → {originalVersion}): {GetText(r).Trim()}");
            });
            ReportRestore(restoreFailure, bodyFailed);
        }

        Assert.True(restoreFailure is null, $"Fixture not restored: {restoreFailure}");
    }

    private async Task OpenFixtureAsync()
    {
        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?>
            {
                ["slnPath"] = _fixtureSln,
                ["closeCurrentFirst"] = true,
            });
        Assert.False(open.IsError == true, GetText(open));
    }

    private async Task<string> ListReferencesAsync() =>
        GetText(await _client!.CallToolAsync("twincat_list_library_references"));

    /// <summary>
    /// Puts the fixture back to <paramref name="initialList"/>. First <paramref name="toolRestore"/>;
    /// when the reference list still differs (or the tool restore threw), the solution is closed
    /// WITHOUT saving, the snapshotted project files are written back and the solution is reopened.
    /// Returns null when the references match the initial list again, otherwise why not.
    /// </summary>
    private async Task<string?> RestoreFixtureAsync(string initialList, ProjectFileSnapshot snapshot, Func<Task> toolRestore)
    {
        string? toolError = null;
        try { await toolRestore(); }
        catch (Exception ex) { toolError = ex.Message; }

        string? diff;
        try { diff = LibraryReferenceListing.Difference(initialList, await ListReferencesAsync()); }
        catch (Exception ex) { diff = $"reference list unreadable: {ex.Message}"; }
        if (diff is null && toolError is null) return null;

        Console.WriteLine($"  ⚠ Tool restore incomplete ({toolError ?? diff}) — restoring the project files.");
        try
        {
            await _client!.CallToolAsync("twincat_close_project",
                new Dictionary<string, object?> { ["saveFirst"] = false });
            var written = snapshot.RestoreChanged();
            Console.WriteLine($"  Project files written back: {written.Count} ({string.Join(", ", written.Select(Path.GetFileName))})");
            await OpenFixtureAsync();
            diff = LibraryReferenceListing.Difference(initialList, await ListReferencesAsync());
        }
        catch (Exception ex)
        {
            return $"file restore failed: {ex.Message}";
        }
        return diff is null ? null : $"references still differ after the file restore: {diff}";
    }

    private static void ReportRestore(string? restoreFailure, bool bodyFailed)
    {
        if (restoreFailure is null)
            Console.WriteLine("  ✓ Fixture restored (library references match the initial state).");
        else
            Console.WriteLine($"  ✗ Fixture NOT restored: {restoreFailure}{(bodyFailed ? " (test body failed as well)" : "")}");
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
