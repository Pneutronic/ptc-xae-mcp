using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// twincat_plc_static_analysis: the SARIF 2.1.0 reader (sample written for these tests, not a vendor file), the fallback
// to the Error List when the XAE cannot export SARIF, and the compact, paged report.
public class StaticAnalysisTests
{
    private const string Sln = @"C:\work\Plant\Plant.sln";

    private const string Sarif = """
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "TwinCAT Static Analysis", "rules": [
              { "id": "SA0033", "shortDescription": { "text": "Unused variables" } } ] } },
            "results": [
              { "ruleId": "SA0033", "level": "warning", "message": { "text": "Unused variable 'nX'" },
                "locations": [{ "physicalLocation": {
                  "artifactLocation": { "uri": "file:///C:/work/Plant/Plc/POUs/FB_Motor.TcPOU" },
                  "region": { "startLine": 12, "startColumn": 5 } } }] },
              { "ruleId": "SA0035", "level": "error", "message": { "text": "Unused output 'bDone'" },
                "locations": [{ "physicalLocation": {
                  "artifactLocation": { "uri": "Plc/POUs/MAIN.TcPOU" },
                  "region": { "startLine": 3 } } }] },
              { "ruleId": "SA0033", "level": "note", "message": { "text": "Unused variable 'nY'" } },
              { "ruleId": "SA0101", "message": { "text": "Name too long" } }
            ]
          }]
        }
        """;

    // ---- SARIF -----------------------------------------------------------------

    [Fact]
    public void Sarif_ReadsRuleLevelMessageFileAndLine()
    {
        var findings = SarifReader.Parse(Sarif, Path.GetDirectoryName(Sln));

        findings.Should().HaveCount(4);
        findings[0].Should().Be(new StaticAnalysisFinding("SA0033", "warning", "Unused variable 'nX'", @"Plc\POUs\FB_Motor.TcPOU", 12, 5));
        findings[1].Should().Be(new StaticAnalysisFinding("SA0035", "error", "Unused output 'bDone'", "Plc/POUs/MAIN.TcPOU", 3, null));
        findings[2].File.Should().BeNull();
        findings[3].Level.Should().Be("warning", "SARIF's default level is warning");
    }

    [Fact]
    public void Sarif_Broken_IsAFormatException()
    {
        var act = () => SarifReader.Parse("{ not json", null);
        act.Should().Throw<FormatException>().WithMessage("The SARIF file cannot be read:*");
    }

    [Fact]
    public void ErrorList_StaticAnalysisEntriesOnly_WithTheirRule()
    {
        var findings = SarifReader.FromErrorList(
        [
            new BuildError("Warning", "SA0033: Unbenutzte Variable 'nX'", @"C:\work\Plant\Plc\POUs\FB_Motor.TcPOU", 12),
            new BuildError("Error", "C0077: Unknown type 'FOO'", null, 4),
            new BuildError("Message", "SA0101: Name too long", null, null),
        ], Path.GetDirectoryName(Sln));

        findings.Should().Equal(
            new StaticAnalysisFinding("SA0033", "warning", "Unbenutzte Variable 'nX'", @"Plc\POUs\FB_Motor.TcPOU", 12, null),
            new StaticAnalysisFinding("SA0101", "note", "Name too long", null, null, null));
    }

    // ---- coordinator -----------------------------------------------------------

    private static (FakeTwinCatSession Fake, string Dir) OpenFake(string? sarif = Sarif)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, ActiveSlnPath = Sln };
        fake.StaticAnalysisSarif = sarif;
        return (fake, Path.Combine(Path.GetTempPath(), $"sa-{Guid.NewGuid():N}"));
    }

    [Fact]
    public void Run_ReportsCountsRulesAndFindings_FromSarif()
    {
        var (fake, dir) = OpenFake();
        try
        {
            string result = new StaticAnalysisCoordinator(fake, dir).Run();

            string sarifPath = Path.Combine(dir, "Plant.static-analysis.sarif.json");
            fake.CallLog.Should().Contain($"RunStaticAnalysis(checkAll=True, settings=<none>, sarif={sarifPath})");
            result.Should().Be(
                $"Static analysis (check all objects): 1 error, 2 warnings, 1 note — read from SARIF {sarifPath}.\n" +
                "Rules: SA0033 ×2, SA0035 ×1, SA0101 ×1\n" +
                "  [warning] SA0033 Plc\\POUs\\FB_Motor.TcPOU L12:5 — Unused variable 'nX'\n" +
                "  [error] SA0035 Plc/POUs/MAIN.TcPOU L3 — Unused output 'bDone'\n" +
                "  [warning] SA0101 — Name too long\n" +
                "Showing 1–3 of 3 (minLevel warning).");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Run_MinLevelAndPaging()
    {
        var (fake, dir) = OpenFake();
        try
        {
            var c = new StaticAnalysisCoordinator(fake, dir);
            c.Run(minLevel: "error").Should().EndWith("  [error] SA0035 Plc/POUs/MAIN.TcPOU L3 — Unused output 'bDone'\nShowing 1–1 of 1 (minLevel error).");
            c.Run(minLevel: "note", limit: 2).Should().EndWith("Showing 1–2 of 4 (minLevel note); more with offset=2.");
            c.Run(minLevel: "note", limit: 2, offset: 2).Should().Contain("Unused variable 'nY'");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Run_NoFindings_IsClean()
    {
        var (fake, dir) = OpenFake("""{ "version": "2.1.0", "runs": [ { "results": [] } ] }""");
        try
        {
            new StaticAnalysisCoordinator(fake, dir).Run(checkAll: false).Should().StartWith(
                "Static analysis (objects in use): no findings — clean. Read from SARIF ");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Run_WithoutSarif_FallsBackToTheErrorList_AndSaysWhy()
    {
        var (fake, dir) = OpenFake(sarif: null);
        fake.StaticAnalysisSarifNote = "ExportToSarif is not offered by this XAE (needs TwinCAT 3.1.4026.20)";
        fake.FakeErrorList.Add(new BuildError("Warning", "SA0033: Unused variable 'nX'", null, 12));
        try
        {
            new StaticAnalysisCoordinator(fake, dir).Run().Should().Be(
                "Static analysis (check all objects): 0 errors, 1 warning, 0 notes — read from the Error List " +
                "(no SARIF: ExportToSarif is not offered by this XAE (needs TwinCAT 3.1.4026.20)).\n" +
                "Rules: SA0033 ×1\n" +
                "  [warning] SA0033 L12 — Unused variable 'nX'\n" +
                "Showing 1–1 of 1 (minLevel warning).");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Run_AStaleSarifFromAnEarlierRun_IsNeverRead()
    {
        var (fake, dir) = OpenFake(sarif: null);
        fake.StaticAnalysisSarifNote = "export failed";
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Plant.static-analysis.sarif.json"), Sarif);
        try
        {
            new StaticAnalysisCoordinator(fake, dir).Run().Should().StartWith(
                "Static analysis (check all objects): no findings — clean. Read from the Error List (no SARIF: export failed).");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Run_Refusals()
    {
        var (fake, dir) = OpenFake();
        var c = new StaticAnalysisCoordinator(fake, dir);
        c.Run(minLevel: "info").Should().Be("[Error] Unknown minLevel 'info'. Expected error, warning or note.");
        c.Run(limit: 0).Should().Be("[Error] limit must be between 1 and 1000.");
        c.Run(offset: -1).Should().Be("[Error] offset must not be negative.");

        fake.RunStaticAnalysisThrows = new InvalidOperationException("RunStaticAnalysis failed");
        c.Run().Should().Be("[Error] The static analysis could not run: RunStaticAnalysis failed");

        fake.SolutionOpen = false;
        c.Run().Should().StartWith("[Error]");
        fake.CallLog.Should().NotContain(e => e.StartsWith("Save", StringComparison.Ordinal));
    }
}
