using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// persistent-snapshot, Stage XAE: the TMC format
/// that carries PERSISTENT / RETAIN is not documented by Beckhoff, so it is pinned here against the
/// FRESHLY BUILT TMC of the neutral fixture (never the committed one, which is stale): the selector must find
/// exactly the leaves the fixture declares as remanent. If a TwinCAT release changes the TMC, this fails.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public sealed class PersistentSnapshotTmcIntegrationTests : IAsyncLifetime
{
    private McpClient? _client;
    private bool _ready;
    private string _fixtureSln = string.Empty;

    public async Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is not null)
        {
            _fixtureSln = fixture;
            try
            {
                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri("http://localhost:5010/"),
                    ConnectionTimeout = TimeSpan.FromSeconds(5),
                });
                _client = await McpClient.CreateAsync(transport,
                    cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
                await _client.CallToolAsync("twincat_open_project",
                    new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
                _ready = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _ready = false;
            }
        }
        LiveXae.Announce(_ready, _fixtureSln);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    private string FreshTmc()
    {
        string plcDir = Path.Combine(Path.GetDirectoryName(_fixtureSln)!, FixtureLayout.SolutionName, FixtureLayout.PlcName);
        return Path.Combine(plcDir, FixtureLayout.PlcName + ".tmc");
    }

    [Trait("Description", "Frisch gebautes Fixture-TMC: Selektor findet genau die PERSISTENT-Blätter (inkl. FB-lokal, Array-Instanz, deklarierte Indizes) und die RETAIN-Variable.")]
    [SkippableFact(Timeout = 900_000)]
    public async Task FreshTmc_MarksExactlyTheDeclaredRemanentLeaves()
    {
        LiveXae.GuardReady(_ready);

        var build = await _client!.CallToolAsync("twincat_rebuild_plc",
            new Dictionary<string, object?> { ["plcProjectName"] = FixtureLayout.PlcName });
        string buildText = string.Join("\n", (build.Content ?? []).OfType<TextContentBlock>().Select(c => c.Text));
        Console.WriteLine($"[persistent-snapshot] rebuild_plc: {buildText}");
        Assert.False(buildText.StartsWith("[Error]", StringComparison.Ordinal), buildText);

        string tmcPath = FreshTmc();
        Assert.True(File.Exists(tmcPath), $"{tmcPath} not written by the build");
        Assert.True(File.GetLastWriteTimeUtc(tmcPath) > DateTime.UtcNow.AddMinutes(-10), $"{tmcPath} is not from this run's build");
        string xml = File.ReadAllText(tmcPath);

        Assert.True(TmcRemanentSelector.TrySelect(xml, "both", TmcRemanentSelector.DefaultLeafLimit, out var all, out string error), error);
        Console.WriteLine("[persistent-snapshot] remanent leaves in the TMC:\n" +
            string.Join("\n", all.Leaves.Select(l => $"  {l.Kind,-10} {l.Type,-8} {l.Path}")));
        Console.WriteLine("[persistent-snapshot] skipped: " + string.Join("; ", all.Skipped.Select(s => $"{s.Path} ({s.Reason})")));

        // PERSISTENT: exactly the fixture's declaration.
        var persistent = all.Leaves.Where(l => l.Kind == "persistent").Select(l => l.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(FixtureLayout.PersistentLeaves.OrderBy(p => p, StringComparer.Ordinal).ToList(), persistent);

        // Types of the interesting leaves (ENUM, STRING, WSTRING, TIME, DT).
        var byPath = all.Leaves.ToDictionary(l => l.Path, StringComparer.Ordinal);
        var stRecipe = FixtureLayout.PersistentGvlName + ".stRecipe.";
        Assert.True(byPath[stRecipe + "eMode"].IsEnum);
        Assert.Equal("STRING", byPath[stRecipe + "sName"].Type);
        Assert.Equal("WSTRING", byPath[stRecipe + "wsName"].Type);
        Assert.Equal("TIME", byPath[stRecipe + "tDelay"].Type);
        Assert.Equal("DATE_AND_TIME", byPath[stRecipe + "dtStamp"].Type); // the TMC spells DT out

        // RETAIN: the VAR_GLOBAL RETAIN variable sits in a RetainSrc data area.
        Assert.Contains(all.Leaves, l => l.Kind == "retain" && l.Path == FixtureLayout.RetainSymbol);

        // FB-local VAR RETAIN: Beckhoff does not document how the TMC shows it. Whatever it is, the retain leaf of every
        // FB_PersistentCell instance must be found - this is the pin.
        foreach (string instance in FixtureLayout.FbCellInstances)
            Assert.Contains(all.Leaves, l => l.Path == instance + ".nRetainCount");

        // The kind filter splits cleanly.
        Assert.True(TmcRemanentSelector.TrySelect(xml, "persistent", TmcRemanentSelector.DefaultLeafLimit, out var onlyPersistent, out error), error);
        Assert.All(onlyPersistent.Leaves, l => Assert.Equal("persistent", l.Kind));
    }
}
