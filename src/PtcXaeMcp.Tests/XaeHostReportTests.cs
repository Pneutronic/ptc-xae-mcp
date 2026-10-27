using Microsoft.CSharp.RuntimeBinder;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.TwinCat.HostSelection;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Which XAE host (32-bit / 64-bit TcXaeShell) a project's TcVersion selects, reported by
/// twincat_open_project, and the host context added to binding failures on the DTE.
/// Registry, ROT and file system are injected, so nothing here depends on this box.
/// </summary>
public class XaeHostReportTests
{
    private const string Exe64 = @"C:\Program Files\Beckhoff\TcXaeShell\Common7\IDE\TcXaeShell.exe";
    private static readonly XaeHostResolver Resolver = new(probeExeForProgId: _ => null);
    private static readonly Func<IReadOnlyList<string>> NothingRunning = () => [];

    private static string NewSolution(string tcVersion, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "xhr_" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(dir, "Proj");
        Directory.CreateDirectory(project);
        string sln = Path.Combine(dir, "Proj.sln");
        File.WriteAllText(sln, "dummy");
        File.WriteAllText(Path.Combine(project, "Proj.tsproj"),
            $"<?xml version=\"1.0\"?><TcSmProject TcVersion=\"{tcVersion}\"><Project/></TcSmProject>");
        return sln;
    }

    [Fact]
    public void ForSolution_Below4026_With64BitInstalled_NamesHostAndWarns()
    {
        string sln = NewSolution("3.1.4024.55", out var dir);
        try
        {
            var report = XaeHostReport.ForSolution(sln, Resolver, p => p == Exe64, NothingRunning);

            Assert.NotNull(report);
            Assert.Contains("32-bit TcXaeShell (TcXaeShell.DTE.15.0)", report!.Info);
            Assert.Contains("3.1.4024.55", report.Info);
            var warn = Assert.Single(report.Warnings);
            Assert.StartsWith("[Warn]", warn);
            Assert.Contains("32-bit XAE although the 64-bit XAE is installed", warn);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ForSolution_Below4026_Without64BitInstalled_DoesNotWarn()
    {
        string sln = NewSolution("3.1.4024.55", out var dir);
        try
        {
            var report = XaeHostReport.ForSolution(sln, Resolver, _ => false, NothingRunning);

            Assert.Empty(report!.Warnings);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ForSolution_4026_SelectsThe64BitHost_NoWarning()
    {
        string sln = NewSolution("3.1.4026.22", out var dir);
        try
        {
            var report = XaeHostReport.ForSolution(sln, Resolver, _ => true, () => [XaeHostResolver.ProgId64]);

            Assert.Contains("64-bit TcXaeShell (TcXaeShell.DTE.17.0)", report!.Info);
            Assert.Empty(report.Warnings);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ForSolution_RunningHostDiffersFromSelected_Warns()
    {
        string sln = NewSolution("3.1.4026.22", out var dir);
        try
        {
            var report = XaeHostReport.ForSolution(sln, Resolver, _ => true, () => [XaeHostResolver.ProgId32]);

            var warn = Assert.Single(report!.Warnings);
            Assert.Contains("selects the 64-bit TcXaeShell", warn);
            Assert.Contains("running XAE is 32-bit TcXaeShell", warn);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ForSolution_NoTsproj_ReturnsNullWithoutProbingTheRunningHosts()
    {
        string sln = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sln");
        File.WriteAllText(sln, "dummy");
        try
        {
            var report = XaeHostReport.ForSolution(sln, Resolver, _ => true,
                () => throw new InvalidOperationException("must not probe"));

            Assert.Null(report);
        }
        finally { File.Delete(sln); }
    }

    // ── twincat_open_project ────────────────────────────────────────────────

    [Fact]
    public async Task OpenProject_ReportsTheHost_AndWarnsOnA32BitSelection()
    {
        string sln = NewSolution("3.1.4024.55", out var dir);
        try
        {
            var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
            var coord = new LifecycleCoordinator(fake)
            {
                HostResolver = Resolver,
                ExeExists = p => p == Exe64,
                RunningHosts = NothingRunning,
            };

            string result = await coord.OpenProjectAsync(sln, false, CancellationToken.None);

            Assert.StartsWith("[Warn]", result);
            Assert.Contains("32-bit XAE although the 64-bit XAE is installed", result);
            Assert.Contains("Opened", result);
            Assert.Contains("Host: 32-bit TcXaeShell (TcXaeShell.DTE.15.0), selected by TcVersion 3.1.4024.55.", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OpenProject_64BitProject_AddsOnlyTheHostSentence()
    {
        string sln = NewSolution("3.1.4026.22", out var dir);
        try
        {
            var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
            var coord = new LifecycleCoordinator(fake)
            {
                HostResolver = Resolver,
                ExeExists = _ => true,
                RunningHosts = () => [XaeHostResolver.ProgId64],
            };

            string result = await coord.OpenProjectAsync(sln, false, CancellationToken.None);

            Assert.StartsWith("Opened", result);
            Assert.Matches(@"Active project: \S+\. Host: 64-bit", result);
            Assert.EndsWith("Host: 64-bit TcXaeShell (TcXaeShell.DTE.17.0), selected by TcVersion 3.1.4026.22.", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── host override ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("auto", true, null)]
    [InlineData(" AUTO ", true, null)]
    [InlineData("64", true, XaeHostClass.Shell64)]
    [InlineData("32", true, XaeHostClass.Shell32)]
    [InlineData("128", false, null)]
    public void TryParseOverride_AcceptsAuto64And32Only(string? text, bool ok, XaeHostClass? expected)
    {
        Assert.Equal(ok, XaeHostReport.TryParseOverride(text, out var hostClass));
        Assert.Equal(expected, hostClass);
    }

    [Fact]
    public void ForSolution_OverrideDiffersFromTcVersion_NamesBothAndWarns()
    {
        string sln = NewSolution("3.1.4024.55", out var dir);
        try
        {
            var report = XaeHostReport.ForSolution(sln, Resolver, p => p == Exe64, NothingRunning, XaeHostClass.Shell64);

            Assert.Equal("Host: 64-bit TcXaeShell (TcXaeShell.DTE.17.0), chosen by host=64 " +
                         "(TcVersion 3.1.4024.55 selects the 32-bit TcXaeShell (TcXaeShell.DTE.15.0)).", report!.Info);
            var warn = Assert.Single(report.Warnings);
            Assert.Contains("TcVersion in the .tsproj was not changed", warn);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ForSolution_OverrideMatchesTcVersion_DoesNotWarn()
    {
        string sln = NewSolution("3.1.4026.22", out var dir);
        try
        {
            var report = XaeHostReport.ForSolution(sln, Resolver, _ => true, NothingRunning, XaeHostClass.Shell64);

            Assert.Equal("Host: 64-bit TcXaeShell (TcXaeShell.DTE.17.0), chosen by host=64.", report!.Info);
            Assert.Empty(report.Warnings);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OpenProject_Host64_PassesTheOverrideToTheSession()
    {
        string sln = NewSolution("3.1.4024.55", out var dir);
        try
        {
            var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
            var coord = new LifecycleCoordinator(fake)
            {
                HostResolver = Resolver,
                ExeExists = p => p == Exe64,
                RunningHosts = NothingRunning,
            };

            string result = await coord.OpenProjectAsync(sln, false, CancellationToken.None, host: "64");

            Assert.Contains(fake.CallLog, c => c.Contains("host=Shell64"));
            Assert.Contains("Host: 64-bit TcXaeShell (TcXaeShell.DTE.17.0), chosen by host=64", result);
            Assert.Contains("[Warn] Opened in the 64-bit TcXaeShell", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OpenProject_Host64_WhileThe32BitXaeRuns_IsRefusedBeforeAnythingHappens()
    {
        string sln = NewSolution("3.1.4024.55", out var dir);
        try
        {
            var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
            var coord = new LifecycleCoordinator(fake)
            {
                HostResolver = Resolver,
                ExeExists = p => p == Exe64,
                RunningHosts = () => [XaeHostResolver.ProgId32],
            };

            string result = await coord.OpenProjectAsync(sln, false, CancellationToken.None, host: "64");

            Assert.StartsWith("[Error]", result);
            Assert.Contains("the running XAE is 32-bit TcXaeShell", result);
            Assert.Empty(fake.CallLog);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OpenProject_Host64_WithoutThe64BitXaeInstalled_IsRefused()
    {
        string sln = NewSolution("3.1.4024.55", out var dir);
        try
        {
            var fake = new FakeTwinCatSession { IsConnected = true };
            var coord = new LifecycleCoordinator(fake) { HostResolver = Resolver, ExeExists = _ => false, RunningHosts = NothingRunning };

            string result = await coord.OpenProjectAsync(sln, false, CancellationToken.None, host: "64");

            Assert.StartsWith("[Error]", result);
            Assert.Contains("is not installed", result);
            Assert.Empty(fake.CallLog);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OpenProject_UnknownHost_IsRefused()
    {
        string sln = NewSolution("3.1.4026.22", out var dir);
        try
        {
            string result = await new LifecycleCoordinator(new FakeTwinCatSession())
                .OpenProjectAsync(sln, false, CancellationToken.None, host: "128");

            Assert.Equal("[Error] host must be auto, 64 or 32 (got '128').", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── twincat_get_status host line ────────────────────────────────────────

    private static TcVersion Version(string text)
    {
        Assert.True(TcVersion.TryParse(text, out var v));
        return v!;
    }

    [Fact]
    public void StatusLine_NamesTheAttachedHost_NotTheOneTheTcVersionSelects()
    {
        // A 4024 project opened on purpose in the 64-bit XAE: the 64-bit shell is what is attached.
        string? line = XaeHostReport.StatusLine([XaeHostResolver.ProgId64], Version("3.1.4024.55"));

        Assert.Equal("Host: TcXaeShell.DTE.17.0 (64-bit, TcVersion 3.1.4024.55), but TcVersion selects the 32-bit XAE", line);
    }

    [Fact]
    public void StatusLine_HostMatchesTcVersion_HasNoMismatchNote() =>
        Assert.Equal("Host: TcXaeShell.DTE.17.0 (64-bit, TcVersion 3.1.4026.22)",
            XaeHostReport.StatusLine([XaeHostResolver.ProgId64], Version("3.1.4026.22")));

    [Fact]
    public void StatusLine_WithoutReadableTcVersion_StillNamesTheHost() =>
        Assert.Equal("Host: TcXaeShell.DTE.15.0 (32-bit)", XaeHostReport.StatusLine([XaeHostResolver.ProgId32], null));

    [Fact]
    public void StatusLine_NoXaeRegistered_IsNull_SoAnExitedShellIsNeverReportedAsHost() =>
        Assert.Null(XaeHostReport.StatusLine([], Version("3.1.4026.22")));

    [Fact]
    public void StatusLine_BothShellsRunning_NamesBoth() =>
        Assert.Equal("Host: TcXaeShell.DTE.17.0 (64-bit) and TcXaeShell.DTE.15.0 (32-bit)",
            XaeHostReport.StatusLine([XaeHostResolver.ProgId64, XaeHostResolver.ProgId32], null));

    // ── binding failures at the tool boundary ───────────────────────────────

    private const string BinderMessage =
        "'PtcXaeMcp.WinForms.TwinCat.TwinCatComSession' does not contain a definition for 'RequireSystemManager'";

    [Fact]
    public void BindingFailureContext_NamesAttachedHost_AndTheUsualCause()
    {
        string text = XaeHostReport.BindingFailureContext([XaeHostResolver.ProgId32], installed64: true);

        Assert.Contains("attached XAE 32-bit TcXaeShell (TcXaeShell.DTE.15.0)", text);
        Assert.Contains("64-bit XAE installed", text);
        Assert.Contains("wrong bitness", text);
        Assert.Contains("twincat_resolve_host", text);
    }

    [Fact]
    public void BindingFailureContext_NoHostRunning_SaysNotDetected() =>
        Assert.Contains("attached XAE not detected; 64-bit XAE not installed",
            XaeHostReport.BindingFailureContext([], installed64: false));

    [Fact]
    public void Describe_BinderException_AppendsTheHostContext()
    {
        string text = ComCall.Describe(new RuntimeBinderException(BinderMessage), bindingContext: () => "Host context: X.");

        Assert.Equal(BinderMessage + " Host context: X.", text);
    }

    [Fact]
    public void Describe_BinderExceptionInsideAnotherException_AppendsTheHostContext()
    {
        var wrapped = new InvalidOperationException("outer", new RuntimeBinderException(BinderMessage));

        Assert.EndsWith(" Host context: X.", ComCall.Describe(wrapped, bindingContext: () => "Host context: X."));
    }

    [Fact]
    public void Describe_BinderException_ContextProbeThrows_KeepsTheOriginalMessage() =>
        Assert.Equal(BinderMessage,
            ComCall.Describe(new RuntimeBinderException(BinderMessage),
                bindingContext: () => throw new InvalidOperationException("probe failed")));

    [Fact]
    public void ListNcTasksAxes_BinderException_KeepsTheMessage_AndAddsTheHostContext()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ListNcTasksAxesThrows = new RuntimeBinderException(BinderMessage),
        };

        var (summary, _) = new NcAxesCoordinator(fake).ListNcTasksAxes();

        Assert.StartsWith("[Error] " + BinderMessage, summary);
        Assert.Contains("Host context:", summary);
    }

    [Fact]
    public void Describe_OtherException_IsUnchanged() =>
        Assert.Equal("boom",
            ComCall.Describe(new InvalidOperationException("boom"), bindingContext: () => "Host context: X."));
}
