using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_plc_task_create, live against the neutral fixture (TIRT has PlcTask, priority 20, 10 ms): refusals change nothing;
/// a time-stamped task with 10 ms is created, its cycle time and priority read back, the PLC task reference linked and a
/// scratch PROGRAM assigned. The task and its reference are removed afterwards through the DTE (no MCP tool deletes them).
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class PlcTaskIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";

    private McpClient? _client;
    private bool _ready;
    private string _fixtureSln = string.Empty;

    public async Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        _fixtureSln = fixture;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            _ready = true;
        }
        catch { _ready = false; }
        LiveXae.Announce(_ready, _fixtureSln);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    /// <param name="expectError">true: must be an error; false: must not; null: either (cleanup).</param>
    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args, bool? expectError = false)
    {
        var result = await _client!.CallToolAsync(tool, args);
        string text = string.Join("\n", (result.Content ?? []).OfType<TextContentBlock>().Select(c => c.Text));
        Console.WriteLine($"[plc_task] {tool}: {text}");
        if (expectError == true)
            Assert.StartsWith("[Error]", text);
        else if (expectError == false)
            Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), $"{tool} failed: {text}");
        return text;
    }

    [Trait("Description", "Ungültiger Name, belegte Priorität (PlcTask 20) und vorhandener Name abgelehnt ohne Änderung; Task mit 10 ms anlegen → Zykluszeit/Priorität zurückgelesen, PLC-Task-Referenz verknüpft, Programm zugewiesen; danach Task und Referenz entfernt.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task CreateTask_ReadsBackTiming_LinksTheReference_AndAssignsTheProgram()
    {
        LiveXae.GuardReady(_ready);

        await CallAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        await PlcProjectReadiness.AwaitAsync(_client!);

        string stamp = DateTimeOffset.UtcNow.ToString("HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        string task = $"Task_{stamp}";
        string prg = $"PRG_Task_{stamp}";
        string prgPath = $"{FixtureLayout.PousFolder}^{prg}";
        string referencePath = $"{FixtureLayout.PlcRoot}^{task}";
        var plc = new Dictionary<string, object?> { ["plcProjectName"] = FixtureLayout.PlcName };

        // Refusals: nothing is created.
        string text = await CallAsync("twincat_plc_task_create", new(plc) { ["name"] = "1 bad", ["cycleTimeMs"] = 10.0 }, expectError: true);
        Assert.Contains("Task name", text);
        text = await CallAsync("twincat_plc_task_create", new(plc) { ["name"] = task, ["cycleTimeMs"] = 10.0, ["priority"] = 20 }, expectError: true);
        Assert.Contains("priority 20 is already used by PlcTask", text);
        text = await CallAsync("twincat_plc_task_create", new(plc) { ["name"] = "PlcTask", ["cycleTimeMs"] = 10.0 }, expectError: true);
        Assert.Contains("already exists", text);
        text = await CallAsync("twincat_plc_task_create", new(plc) { ["name"] = task, ["cycleTimeMs"] = 10.0, ["program"] = prg }, expectError: true);
        Assert.Contains($"has no PROGRAM '{prg}'", text);
        Assert.DoesNotContain(referencePath, await CallAsync("twincat_plc_list", new() { ["kind"] = "TASKREF" }));

        bool created = false;
        try
        {
            await CallAsync("twincat_plc_create", new()
            {
                ["kind"] = "POU", ["subtype"] = "PROGRAM", ["parentPath"] = FixtureLayout.PousFolder, ["name"] = prg,
            });

            created = true;
            text = await CallAsync("twincat_plc_task_create", new(plc) { ["name"] = task, ["cycleTimeMs"] = 10.0, ["program"] = prg });
            Assert.Contains($"Created real-time task 'TIRT^{task}' (read-back: cycle time 10 ms (100000 x 100 ns), priority ", text);
            Assert.Contains($"linked to 'TIRT^{task}'", text);
            Assert.Contains($"The task calls PROGRAM '{prg}'", text);
            Assert.Contains("Project saved.", text);
            Assert.DoesNotContain("[Warn] The XAE set", text);

            // The priority is a free one: PlcTask keeps 20.
            var priority = Regex.Match(text, @"priority (\d+)\)");
            Assert.True(priority.Success, text);
            Assert.NotEqual("20", priority.Groups[1].Value);

            // The PLC tree shows the reference and its program call.
            string list = await CallAsync("twincat_plc_list", new() { ["kind"] = "TASKREF" });
            Assert.Matches(new Regex($@"TASKREF/TASK\s+{Regex.Escape(referencePath)}"), list);
            Assert.Matches(new Regex($@"TASKREF/PROGRAM\s+{Regex.Escape(referencePath)}\^{Regex.Escape(prg)}"), list);

            // The same name again is refused now.
            text = await CallAsync("twincat_plc_task_create", new(plc) { ["name"] = task, ["cycleTimeMs"] = 10.0 }, expectError: true);
            Assert.Contains("already exists", text);
        }
        finally
        {
            if (created)
            {
                try { Console.WriteLine($"[plc_task] cleanup: {XaeDte.DeleteTaskAndReference(FixtureLayout.PlcRoot, task)}"); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    Console.WriteLine($"[plc_task] cleanup failed: {ex.Message}");
                }
                await CallAsync("twincat_plc_delete", new() { ["path"] = prgPath, ["kind"] = "POU" }, expectError: null);
                await CallAsync("twincat_save_project", new(), expectError: null);
            }
        }
    }
}
