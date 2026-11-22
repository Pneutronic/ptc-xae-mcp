using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Live round trip of twincat_variant_create / twincat_variant_set_item / twincat_variant_delete on the
/// neutral fixture: create Machine and Simulation, disable a box in Simulation only, read the value back per
/// variant, delete Simulation. The run works on a per-run copy of the fixture; the last variant (Machine, the
/// active one) cannot be deleted, so it stays in that copy.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class VariantEditingIntegrationTests : IAsyncLifetime
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
                ConnectionTimeout = TimeSpan.FromSeconds(5)
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

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client!.CallToolAsync(tool, args,
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    private async Task<JsonElement> StatusAsync()
    {
        string json = await CallAsync("twincat_get_variant_status", []);
        Assert.False(json.StartsWith("[Error]", StringComparison.Ordinal), json);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static List<string> Variants(JsonElement status) =>
        status.GetProperty("availableVariants").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();

    [SkippableFact]
    public async Task CreateSetItemDelete_RoundTrip()
    {
        LiveXae.GuardReady(_ready);

        // Own variant names, so the test does not depend on what another test of the class left in the per-run copy
        // (the axis test below creates "Simulation" and keeps it).
        const string machine = "McpMachine";
        const string sim = "McpSimulation";
        var before = Variants(await StatusAsync());
        Skip.If(before.Contains(machine) || before.Contains(sim), "the fixture copy still holds this test's variants");

        string r1 = await CallAsync("twincat_variant_create", new() { ["variantName"] = machine });
        Assert.DoesNotContain("[Error]", r1, StringComparison.Ordinal);
        string r2 = await CallAsync("twincat_variant_create", new() { ["variantName"] = sim });
        Assert.DoesNotContain("[Error]", r2, StringComparison.Ordinal);

        var created = await StatusAsync();
        Assert.Equal(before.Concat([machine, sim]).Order().ToArray(), Variants(created).Order().ToArray());
        string original = created.GetProperty("activeVariant").GetString() ?? string.Empty;

        string duplicate = await CallAsync("twincat_variant_create", new() { ["variantName"] = machine.ToLowerInvariant() });
        Assert.StartsWith("[Error]", duplicate, StringComparison.Ordinal);

        string set = await CallAsync("twincat_variant_set_item", new()
        {
            ["treePath"] = FixtureLayout.El1008Path,
            ["variant"] = sim,
            ["disabled"] = true,
        });
        Assert.DoesNotContain("[Error]", set, StringComparison.Ordinal);
        var rows = JsonDocument.Parse(set).RootElement.GetProperty("variants").EnumerateArray()
            .ToDictionary(r => r.GetProperty("variant").GetString()!, r => r.GetProperty("disabled").GetBoolean());
        Assert.True(rows[sim]);
        Assert.False(rows[machine]);

        // The original variant is active again after the call.
        Assert.Equal(original, (await StatusAsync()).GetProperty("activeVariant").GetString());

        // The active variant cannot be deleted. In a project without variants that is the first one created here and it
        // stays; otherwise the variant that was active before stays and both of ours go.
        string[] ours = [machine, sim];
        var toDelete = ours.Where(v => v != original).ToArray();
        if (ours.Contains(original))
        {
            string refused = await CallAsync("twincat_variant_delete", new() { ["variantName"] = original });
            Assert.StartsWith("[Error]", refused, StringComparison.Ordinal);
        }
        foreach (string v in toDelete)
        {
            string deleted = await CallAsync("twincat_variant_delete", new() { ["variantName"] = v });
            Assert.DoesNotContain("[Error]", deleted, StringComparison.Ordinal);
        }
        var expected = ours.Contains(original) ? before.Concat([original]) : before;
        Assert.Equal(expected.Order().ToArray(), Variants(await StatusAsync()).Order().ToArray());
    }

    [SkippableFact]
    public async Task AxisSimulation_IsRefusedWhenTheXaeDoesNotExposeIt_AndChangesNothing()
    {
        LiveXae.GuardReady(_ready);

        var variants = Variants(await StatusAsync());
        if (!variants.Contains("Simulation"))
        {
            string created = await CallAsync("twincat_variant_create", new() { ["variantName"] = "Simulation" });
            Assert.DoesNotContain("[Error]", created, StringComparison.Ordinal);
        }

        string list = await CallAsync("twincat_list_nc_tasks_axes", []);
        var task = System.Text.RegularExpressions.Regex.Match(list, @"TINC\^[^
,]*?SAF").Value;
        Skip.If(task.Length == 0, "the fixture has no NC task");
        string axis = task + "^Axes^Axis 1";
        if (!list.Contains("Axis 1", StringComparison.Ordinal))
            await CallAsync("twincat_create_axis", new() { ["ncTaskPath"] = task, ["axisName"] = "Axis 1" });

        string before = (await StatusAsync()).GetProperty("activeVariant").GetString() ?? string.Empty;
        string result = await CallAsync("twincat_variant_set_item", new()
        {
            ["treePath"] = axis,
            ["variant"] = "Simulation",
            ["axisSimulation"] = true,
        });
        Assert.StartsWith("[Error]", result, StringComparison.Ordinal);
        Assert.Contains("simulation flag", result, StringComparison.Ordinal);
        Assert.Equal(before, (await StatusAsync()).GetProperty("activeVariant").GetString());
    }
}
