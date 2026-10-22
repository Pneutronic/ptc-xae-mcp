using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Decision 2026-10-19 (issue #272): IBN Live is armed only by a human in the host window.
/// The production MCP surface may propose and stop, never arm; <c>twincat_ibn_arm</c> survives
/// only as test support on the diagnostics surface, which a released client never sees.
/// </summary>
public sealed class IbnArmSurfaceTests
{
    private static Dictionary<string, MethodInfo> Tools(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attr?.Name is not null)
            .ToDictionary(x => x.Attr!.Name!, x => x.Method, StringComparer.Ordinal);

    [Fact]
    public void ProductionSurface_HasNoArmTool()
    {
        Tools(typeof(TwinCatMcpTools)).Keys.Should().NotContain("twincat_ibn_arm",
            "arming via MCP is refused by not existing: only the Approvals view arms IBN Live");
    }

    [Fact]
    public void ProductionSurface_KeepsProposeAndTheRiskReducingTools()
    {
        Tools(typeof(TwinCatMcpTools)).Keys.Should().Contain(
        [
            "twincat_ibn_propose", "twincat_ibn_get_proposal_status", "twincat_list_active_chains",
            "twincat_ibn_disarm", "twincat_ibn_abort", "twincat_ibn_release_all",
        ]);
    }

    [Fact]
    public void ArmTool_LivesOnlyOnTheDiagnosticsSurface_MarkedAsTestSupport()
    {
        var diagnostics = Tools(typeof(TwinCatMcpToolsDiagnostics));
        diagnostics.Should().ContainKey("twincat_ibn_arm", "the live suite (a --diagnostics host) still arms the fixture probe");

        string description = diagnostics["twincat_ibn_arm"].GetCustomAttribute<DescriptionAttribute>()!.Description;
        description.Should().StartWith("TEST SUPPORT ONLY");
        description.Should().Contain("never registered for released clients");
    }

    [Fact]
    public void NeitherSurface_HasAConfirmOrExecuteTool()
    {
        var names = Tools(typeof(TwinCatMcpTools)).Keys.Concat(Tools(typeof(TwinCatMcpToolsDiagnostics)).Keys);
        names.Where(n => n.Contains("ibn", StringComparison.OrdinalIgnoreCase)
                         && (n.Contains("confirm", StringComparison.OrdinalIgnoreCase)
                             || n.Contains("execute", StringComparison.OrdinalIgnoreCase)
                             || n.Contains("trigger", StringComparison.OrdinalIgnoreCase)))
            .Should().BeEmpty("a proposal executes only after a human clicks the host popup");
    }

    [Fact]
    public void NoToolSource_CallsConfirmProposal()
    {
        // ConfirmProposal writes the command bit; it is host-only (the Approvals view's popup).
        // The one exception: the persistent-snapshot live-suite hook on the diagnostics surface (never registered
        // for released clients, pinned by PersistentSnapshotSurfaceTests). The IBN ConfirmProposal has no exception.
        const string snapshotHook = "SnapshotRestore.ConfirmProposal(";
        var offenders = Directory.EnumerateFiles(FindMcpSourceDirectory(), "TwinCatMcpTools*.cs")
            .Where(f =>
            {
                string text = File.ReadAllText(f);
                if (Path.GetFileName(f) == "TwinCatMcpTools.Diagnostics.cs")
                    text = text.Replace(snapshotHook, string.Empty, StringComparison.Ordinal);
                return text.Contains("ConfirmProposal(", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();
        offenders.Should().BeEmpty();
    }

    private static string FindMcpSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PtcXaeMcp.WinForms", "Mcp");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "TwinCatMcpTools.*.cs").Any())
                return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate src/PtcXaeMcp.WinForms/Mcp from " + AppContext.BaseDirectory);
    }

    /// <summary>A stop must never be refused by the TcSysSrv pre-check (#272 review).</summary>
    [Theory]
    [InlineData("twincat_ibn_abort")]
    [InlineData("twincat_ibn_release_all")]
    [InlineData("twincat_ibn_disarm")]
    public void IbnStopTools_AreNotBlockedByTheSystemServiceCheck(string tool)
    {
        Assert.DoesNotContain(tool, PtcXaeMcp.WinForms.TwinCat.TwinCatSystemService.AdsTools);
    }

    /// <summary>
    /// Issue #1: a stop must not wait for the dispatcher lock — while a build or an activation
    /// holds it, a dispatched ABORT only answers "busy". The stop tools bypass the dispatcher and
    /// the test gate; the coordinator's own I/O gate serialises them against the other IBN writes.
    /// </summary>
    [Theory]
    [InlineData("twincat_ibn_abort", "Abort")]
    [InlineData("twincat_ibn_release_all", "ReleaseAll")]
    [InlineData("twincat_ibn_disarm", "Disarm")]
    public void IbnStopTools_BypassTheDispatcherAndTheGate(string tool, string method)
    {
        string source = File.ReadAllText(Path.Combine(FindMcpSourceDirectory(), "TwinCatMcpTools.IbnLive.cs"));
        int start = source.IndexOf($"Name = \"{tool}\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        int next = source.IndexOf("[McpServerTool(", start, StringComparison.Ordinal);
        string body = next < 0 ? source[start..] : source[start..next];

        body.Should().Contain($"bridge.LiveCoordinator.{method}()");
        body.Should().NotContain("Dispatcher.Run", "a stop must not answer 'busy' behind a build or an activation");
        body.Should().Contain("bypassGate: true");
    }

    [Fact]
    public void ApprovalsView_StopButtons_DoNotUseTheDispatcher()
    {
        string view = File.ReadAllText(Path.Combine(
            Directory.GetParent(FindMcpSourceDirectory())!.FullName, "Views", "ApprovalsView.cs"));
        int start = view.IndexOf("private async Task RunIbnAsync(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        int end = view.IndexOf("\n    }", start, StringComparison.Ordinal);
        string body = view[start..end];

        body.Should().NotContain("RunGuiComActionAsync", "GUI Disarm / ABORT must not wait for the dispatcher lock");
        body.Should().Contain("Task.Run(action)");
    }
}
