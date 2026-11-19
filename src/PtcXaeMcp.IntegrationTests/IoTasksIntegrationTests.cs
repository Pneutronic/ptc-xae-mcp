using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Real-time tasks, I/O task, Profinet device and sync task, live against the neutral fixture (PlcTask, NC SAF/SVB, a disabled
/// EtherCAT master with an EK1100). Lists the tasks with their kinds, refuses colliding priorities and ports, creates an I/O
/// task with image, changes priority, cycle time and the disabled flag (also of the NC SVB task) with read-back, adds an
/// EL6631-0010 with a Profinet device, a module and the I/O task as sync task, and switches the sync task. Everything it
/// adds is removed afterwards and every changed value restored, so the per-run fixture copy stays as it was.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class IoTasksIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private const string Ek1100 = FixtureLayout.EtherCatMasterPath + "^Term 1 (EK1100)";
    private const string Svb = "TINC^NC-Task 1 SAF^NC-Task 1 SVB";

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
        Console.WriteLine($"[io_tasks] {tool}: {text}");
        if (expectError == true)
            Assert.StartsWith("[Error]", text);
        else if (expectError == false)
            Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), $"{tool} failed: {text}");
        return text;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text[text.IndexOf('{')..]).RootElement;

    private async Task<List<JsonElement>> TasksAsync() =>
        Json(await CallAsync("twincat_task_list", new())).GetProperty("tasks").EnumerateArray().ToList();

    private static JsonElement FindTask(List<JsonElement> tasks, string path) =>
        tasks.Single(t => t.GetProperty("path").GetString() == path);

    [Trait("Description", "Task-Liste (PLC/NC), Prioritäts- und Port-Kollisionen abgelehnt; I/O-Task mit Image anlegen und ändern (auch NC-SVB-Zykluszeit, disabled); EL6631-0010 + Profinet-Device + Modul + Sync-Task setzen/zurücksetzen; alles danach zurückgebaut.")]
    [SkippableFact(Timeout = 900_000)]
    public async Task Tasks_IoTask_ProfinetDevice_AndSyncTask_RoundTrip()
    {
        LiveXae.GuardReady(_ready);

        await CallAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        await PlcProjectReadiness.AwaitAsync(_client!);

        string stamp = DateTimeOffset.UtcNow.ToString("HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        string ioTask = $"IoTask_{stamp}";
        string ioTaskPath = $"TIRT^{ioTask}";
        string device = $"PN_{stamp}";
        string devicePath = $"TIID^{device}";
        string el6631Name = $"Term 9 (EL6631-0010) {stamp}";
        string el6631Path = $"{Ek1100}^{el6631Name}";

        // ---- the list: PLC and NC tasks ----
        var tasks = await TasksAsync();
        var plcTask = tasks.Single(t => t.GetProperty("name").GetString() == "PlcTask");
        Assert.Equal("plc", plcTask.GetProperty("kind").GetString());
        var svb = FindTask(tasks, Svb);
        Assert.Equal("nc", svb.GetProperty("kind").GetString());
        int plcPriority = plcTask.GetProperty("priority").GetInt32();
        int svbPriority = svb.GetProperty("priority").GetInt32();
        uint svbCycle = svb.GetProperty("cycleTime100ns").GetUInt32();
        int plcPort = plcTask.GetProperty("amsPort").GetInt32();

        // ---- refusals: nothing changes ----
        string text = await CallAsync("twincat_io_task_create", new() { ["name"] = ioTask, ["cycleTimeMs"] = 2.0, ["priority"] = svbPriority }, expectError: true);
        Assert.Contains($"priority {svbPriority} is already used by NC-Task 1 SVB", text);
        text = await CallAsync("twincat_io_task_create", new() { ["name"] = ioTask, ["cycleTimeMs"] = 2.0, ["amsPort"] = plcPort }, expectError: true);
        Assert.Contains($"AMS port {plcPort} is already used by PlcTask", text);
        text = await CallAsync("twincat_task_configure", new() { ["task"] = "PlcTask", ["priority"] = svbPriority }, expectError: true);
        Assert.Contains("already used by NC-Task 1 SVB", text);
        Assert.DoesNotContain(tasks, t => t.GetProperty("path").GetString() == ioTaskPath);

        bool taskCreated = false, boxAdded = false, deviceCreated = false, svbChanged = false;
        try
        {
            // ---- I/O task with image ----
            text = await CallAsync("twincat_io_task_create", new() { ["name"] = ioTask, ["cycleTimeMs"] = 2.0, ["amsPort"] = 301 });
            taskCreated = true;
            Assert.Contains($"Created I/O task '{ioTaskPath}' with process image (read-back: cycle time 2 ms (20000 x 100 ns), priority ", text);
            Assert.Contains("AMS port 301)", text);
            Assert.DoesNotContain("[Warn]", text);

            tasks = await TasksAsync();
            var io = FindTask(tasks, ioTaskPath);
            Assert.Equal("io", io.GetProperty("kind").GetString());
            Assert.True(io.GetProperty("image").GetBoolean());
            int ioPriority = io.GetProperty("priority").GetInt32();
            Assert.NotEqual(1, ioPriority); // the XAE's own default for a new image task

            // ---- configure: priority, cycle, disabled; the NC SVB cycle ----
            int free = Enumerable.Range(2, 60).First(p => tasks.All(t => t.GetProperty("priority").GetInt32() != p));
            text = await CallAsync("twincat_task_configure", new() { ["task"] = ioTask, ["priority"] = free, ["cycleTimeMs"] = 4.0 });
            Assert.Contains($"priority {ioPriority} -> {free}; cycle time 2 ms (20000 x 100 ns) -> 4 ms (40000 x 100 ns)", text);
            Assert.DoesNotContain("[Warn]", text);
            text = await CallAsync("twincat_task_configure", new() { ["task"] = ioTaskPath, ["disabled"] = true });
            Assert.Contains("disabled false -> true", text);
            Assert.True(FindTask(await TasksAsync(), ioTaskPath).GetProperty("disabled").GetBoolean());
            await CallAsync("twincat_task_configure", new() { ["task"] = ioTaskPath, ["disabled"] = false });

            svbChanged = true;
            text = await CallAsync("twincat_task_configure", new() { ["task"] = Svb, ["cycleTimeMs"] = 2.0 });
            Assert.Contains("cycle time", text);
            Assert.Equal(20000u, FindTask(await TasksAsync(), Svb).GetProperty("cycleTime100ns").GetUInt32());

            // ---- EL6631-0010 and the Profinet device ----
            await CallAsync("twincat_add_ethercat_box", new() { ["parentPath"] = Ek1100, ["vInfo"] = "EL6631-0010", ["name"] = el6631Name });
            boxAdded = true;

            text = await CallAsync("twincat_profinet_device_add", new() { ["name"] = device, ["boxPath"] = FixtureLayout.El1008Path }, expectError: true);
            Assert.Contains("is not an EL6631-0010", text);
            text = await CallAsync("twincat_profinet_device_add", new() { ["name"] = device, ["boxPath"] = el6631Path, ["syncTask"] = "PlcTask" }, expectError: true);
            Assert.Contains("has no process image", text);

            text = await CallAsync("twincat_profinet_device_add", new() { ["name"] = device, ["boxPath"] = el6631Path, ["syncTask"] = ioTask });
            deviceCreated = true;
            Assert.Contains($"Created Profinet device (RT) '{devicePath}' on the EL6631-0010 '{el6631Path}' (read-back).", text);
            Assert.Contains($"Profinet box '{devicePath}^", text); // the XAE names the box itself
            Assert.Contains($"Sync task: '{ioTaskPath}' (AMS port 301, read back).", text);

            text = await CallAsync("twincat_profinet_device_add", new() { ["name"] = device + "b", ["boxPath"] = el6631Path }, expectError: true);
            Assert.Contains($"is already used by the I/O device '{devicePath}'", text);

            // ---- modules ----
            text = await CallAsync("twincat_profinet_list_modules", new() { ["device"] = devicePath });
            var listed = Json(text);
            Assert.All(listed.GetProperty("placed").EnumerateArray(), m => Assert.Equal(0, m.GetProperty("slot").GetInt32())); // only the DAP
            Assert.Contains(listed.GetProperty("catalog").EnumerateArray(), m => m.GetProperty("module").GetString() == "BYTE_I4");

            text = await CallAsync("twincat_profinet_module_add", new() { ["device"] = devicePath, ["module"] = "BYTE_I4", ["slot"] = 1 });
            Assert.Matches(new Regex(@"^Placed module 'BYTE_I4' \(.+, 4 byte\(s\) in, 0 byte\(s\) out\) in slot 1:"), text);
            text = await CallAsync("twincat_profinet_module_add", new() { ["device"] = devicePath, ["module"] = "BYTE_O4", ["slot"] = 1 }, expectError: true);
            Assert.Contains("slot 1 already holds", text);
            var placed = Json(await CallAsync("twincat_profinet_list_modules", new() { ["device"] = devicePath })).GetProperty("placed").EnumerateArray()
                .Where(m => m.GetProperty("slot").GetInt32() != 0).ToList();
            Assert.Single(placed);
            Assert.Equal(1, placed[0].GetProperty("slot").GetInt32());
            Assert.Equal("BYTE_I4", placed[0].GetProperty("module").GetString());

            // ---- sync task: read, back to default, set again; the module survives the re-import ----
            text = await CallAsync("twincat_io_device_sync_task", new() { ["device"] = devicePath });
            Assert.Contains($"Sync task of '{devicePath}': '{ioTaskPath}' (AMS port 301).", text);
            text = await CallAsync("twincat_io_device_sync_task", new() { ["device"] = devicePath, ["task"] = "default" });
            Assert.Contains("default (via mapping)", text);
            text = await CallAsync("twincat_io_device_sync_task", new() { ["device"] = devicePath, ["task"] = ioTaskPath });
            Assert.Contains($"'{ioTaskPath}' (AMS port 301)", text);
            text = await CallAsync("twincat_io_device_sync_task", new() { ["device"] = devicePath, ["task"] = "PlcTask" }, expectError: true);
            Assert.Contains("has no process image", text);
            text = await CallAsync("twincat_io_device_sync_task", new() { ["device"] = FixtureLayout.EtherCatMasterPath, ["task"] = ioTask }, expectError: true);
            Assert.Contains("has no sync task setting", text);

            placed = Json(await CallAsync("twincat_profinet_list_modules", new() { ["device"] = devicePath })).GetProperty("placed").EnumerateArray()
                .Where(m => m.GetProperty("slot").GetInt32() != 0).ToList();
            Assert.Single(placed);
            string topology = await CallAsync("twincat_get_fieldbus_topology", new());
            Assert.Contains(device, topology);
        }
        finally
        {
            if (deviceCreated) Console.WriteLine($"[io_tasks] cleanup: {XaeDte.DeleteChild("TIID", device)}");
            if (boxAdded) await CallAsync("twincat_remove_ethercat_box", new() { ["treePath"] = el6631Path }, expectError: null);
            if (taskCreated) Console.WriteLine($"[io_tasks] cleanup: {XaeDte.DeleteChild("TIRT", ioTask)}");
            if (svbChanged)
                await CallAsync("twincat_task_configure", new()
                {
                    ["task"] = Svb, ["cycleTimeMs"] = svbCycle / 10_000.0,
                }, expectError: null);
            await CallAsync("twincat_save_project", new(), expectError: null);
        }

        tasks = await TasksAsync();
        Assert.DoesNotContain(tasks, t => t.GetProperty("path").GetString() == ioTaskPath);
        Assert.Equal(svbCycle, FindTask(tasks, Svb).GetProperty("cycleTime100ns").GetUInt32());
        Assert.Equal(plcPriority, tasks.Single(t => t.GetProperty("name").GetString() == "PlcTask").GetProperty("priority").GetInt32());
    }
}
