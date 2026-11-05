using System.Xml.Linq;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// twincat_doctor (every check with its verdict and next step, bounded against a hanging XAE) and the JUnit file of
// twincat_tcunit_results (built from the tool's own JSON).
public class DoctorAndJUnitTests
{
    private static DoctorProbes Probes(
        SystemServiceState service = SystemServiceState.Running, LocalNetIdSource source = LocalNetIdSource.Router,
        int? xae = 1, string? dialog = null) => new(
        () => "2.6.0",
        () => new SystemServiceStatus(service, service == SystemServiceState.NotRunning ? "Stopped" : ""),
        () => new LocalAmsNetId("192.168.1.10.1.1", source, source == LocalNetIdSource.Router ? null : "port open failed"),
        () => xae,
        () => dialog,
        _ => [@"C:\Temp"]);

    private static FakeTwinCatSession Fake(bool open = true) =>
        new() { IsConnected = true, SolutionOpen = open, ActiveSlnPath = open ? @"C:\work\Plant\Plant.sln" : null };

    [Fact]
    public void Doctor_AllHealthy()
    {
        var checks = new DoctorCoordinator(Fake(), Probes()).Run();
        DoctorCoordinator.Format(checks).Should().Be(
            "Doctor: 6 ok, 0 warnings, 0 failures.\n" +
            "  [ok] MCP server — ptc-xae-mcp 2.6.0\n" +
            "  [ok] TwinCAT System Service — running\n" +
            "  [ok] Local AMS NetId — 192.168.1.10.1.1 (from the AMS router)\n" +
            "  [ok] XAE — 1 TcXaeShell running\n" +
            @"  [ok] Solution — C:\work\Plant\Plant.sln" + "\n" +
            @"  [ok] File paths — allowed roots: C:\Temp");
    }

    [Fact]
    public void Doctor_ServiceDown_RouterFallback_NoXae()
    {
        var fake = Fake(open: false);
        var checks = new DoctorCoordinator(fake, Probes(SystemServiceState.NotRunning, LocalNetIdSource.Registry, xae: 0)).Run();
        checks.Single(c => c.Name == "TwinCAT System Service").Status.Should().Be("fail");
        checks.Single(c => c.Name == "Local AMS NetId").Status.Should().Be("warn");
        checks.Single(c => c.Name == "XAE").Should().Be(new DoctorCheck("XAE", "warn", "no TcXaeShell running",
            "twincat_open_project starts the XAE with a solution."));
        checks.Should().NotContain(c => c.Name == "Solution", "without an XAE there is nothing to ask");
        fake.CallLog.Should().NotContain(c => c.StartsWith("Open", StringComparison.Ordinal), "the doctor never starts an XAE");
    }

    [Fact]
    public void Doctor_ABlockingDialog_IsAFailure_AndTheXaeIsNotAsked()
    {
        var fake = Fake();
        fake.HasOpenSolutionThrows = new InvalidOperationException("must not be called");
        var checks = new DoctorCoordinator(fake, Probes(dialog: "TcXaeShell: Datei wurde geändert")).Run();
        checks.Single(c => c.Name == "XAE dialog").Should().Be(new DoctorCheck("XAE dialog", "fail",
            "the XAE is blocked by 'TcXaeShell: Datei wurde geändert'",
            "Answer or close the dialog in the XAE; until then every XAE tool is refused."));
        checks.Should().NotContain(c => c.Name == "Solution");
    }

    [Fact]
    public void Doctor_AnXaeThatDoesNotAnswer_IsReportedWithinTheTimeout()
    {
        var fake = Fake();
        fake.HasOpenSolutionThrows = null;
        using var gate = new ManualResetEventSlim(false);
        fake.HasOpenSolutionHook = () => gate.Wait(TimeSpan.FromSeconds(10));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var check = new DoctorCoordinator(fake, Probes(), TimeSpan.FromMilliseconds(200)).Run().Single(c => c.Name == "Solution");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        gate.Set();
        check.Status.Should().Be("fail");
        check.Detail.Should().StartWith("the XAE did not answer within");
    }

    [Fact]
    public void Doctor_NoSolutionOpen_SaysHowToOpenOne()
    {
        new DoctorCoordinator(Fake(open: false), Probes()).Run().Single(c => c.Name == "Solution")
            .Should().Be(new DoctorCheck("Solution", "warn", "no solution open", "twincat_open_project opens one."));
    }

    // ---- JUnit -----------------------------------------------------------------

    private const string Result = """
        {"success":true,"verdict":"failed","message":"1 of 3 tests failed","totals":{"suites":2,"tests":3,"passed":2,"failed":1,"duration":0.25},
         "suites":[{"name":"PRG_TEST.fbMotorTest","tests":2,"failed":1,"duration":0.2,
                    "cases":[{"name":"Start_SetsRunning","failed":true,"skipped":false,"message":"Expected TRUE"},
                             {"name":"Stop_Clears","failed":false,"skipped":false,"message":""}]},
                   {"name":"PRG_TEST.fbValveTest","tests":1,"failed":0,"duration":0.05,
                    "cases":[{"name":"Later","failed":false,"skipped":true,"message":""}]}],
         "amsNetId":"127.0.0.1.1.1","adsPort":851}
        """;

    [Fact]
    public void JUnit_SuitesCasesFailuresAndSkips()
    {
        var doc = XDocument.Parse(TcUnitJUnit.FromResultJson(Result));
        var root = doc.Root!;
        root.Name.LocalName.Should().Be("testsuites");
        ((string?)root.Attribute("tests")).Should().Be("3");
        ((string?)root.Attribute("failures")).Should().Be("1");
        ((string?)root.Attribute("time")).Should().Be("0.25");

        var suites = root.Elements("testsuite").ToList();
        suites.Select(s => (string?)s.Attribute("name")).Should().Equal("PRG_TEST.fbMotorTest", "PRG_TEST.fbValveTest");
        var failure = suites[0].Elements("testcase").First().Element("failure")!;
        ((string?)failure.Attribute("message")).Should().Be("Expected TRUE");
        suites[0].Elements("testcase").Last().HasElements.Should().BeFalse();
        suites[1].Element("testcase")!.Element("skipped").Should().NotBeNull();
    }

    [Fact]
    public void JUnit_WithJUnitFile_WritesTheFile_AndNamesItInTheJson()
    {
        string file = Path.Combine(Path.GetTempPath(), $"junit-{Guid.NewGuid():N}.xml");
        try
        {
            string json = TcUnitJUnit.WithJUnitFile(Result, file);
            File.Exists(file).Should().BeTrue();
            json.Should().Contain("\"junitFile\":").And.Contain("\"verdict\":\"failed\"");
        }
        finally { File.Delete(file); }
    }
}
