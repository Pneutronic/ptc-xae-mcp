using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>Which toolsets a session sees: environment, then the window, then the client rule, then all.</summary>
public class ToolProfilesTests
{
    [Fact]
    public void TheEnvironment_WinsOverTheWindowAndTheClient()
    {
        var p = ToolProfiles.Resolve("core", "all", "claude-code");
        Assert.Equal(ProfileSource.Environment, p.Source);
        Assert.Equal("core", p.Name);
    }

    [Fact]
    public void TheWindow_WinsOverTheClientRule()
    {
        var p = ToolProfiles.Resolve(null, "core", "claude-code");
        Assert.Equal(ProfileSource.Gui, p.Source);
        Assert.False(p.Shows(Toolset.Hmi));
    }

    [Fact]
    public void Automatic_LeavesTheChoiceToTheClientRule()
    {
        var p = ToolProfiles.Resolve(null, ToolProfiles.Automatic, "Cursor-VSCode");
        Assert.Equal(ProfileSource.ClientRule, p.Source);
        Assert.Equal("core", p.Name);
    }

    [Theory]
    [InlineData("claude-code", "all")]
    [InlineData("Claude Desktop", "all")]
    [InlineData("cursor-vscode", "core")]
    [InlineData("Visual Studio Code", "core")]
    [InlineData("GitHub Copilot", "core")]
    [InlineData("windsurf-client", "core")]
    public void ClientRules_MatchAPartOfTheName(string client, string profile) =>
        Assert.Equal(profile, ToolProfiles.Resolve(null, null, client).Name);

    [Theory]
    [InlineData("testhost")]
    [InlineData("mcptests-harness")]
    [InlineData(null)]
    public void AnUnknownClient_SeesEverything(string? client)
    {
        var p = ToolProfiles.Resolve(null, null, client);
        Assert.Equal(ProfileSource.Default, p.Source);
        Assert.All(Enum.GetValues<Toolset>(), s => Assert.True(p.Shows(s)));
    }

    [Fact]
    public void AToolsetList_AlwaysAddsMeta()
    {
        var p = ToolProfiles.Parse("plc, hmi", ProfileSource.Environment, "test");
        Assert.True(p.Shows(Toolset.Meta));
        Assert.True(p.Shows(Toolset.Plc));
        Assert.True(p.Shows(Toolset.Hmi));
        Assert.False(p.Shows(Toolset.Io));
    }

    [Fact]
    public void CoreInAList_AddsTheCoreToolsets()
    {
        var p = ToolProfiles.Parse("core;hmi", ProfileSource.Gui, "test");
        Assert.True(p.Shows(Toolset.Plc));
        Assert.True(p.Shows(Toolset.Hmi));
        Assert.False(p.Shows(Toolset.Scope));
    }

    [Fact]
    public void AnUnknownToolset_ShowsAll_SoATypoNeverHidesTools()
    {
        var p = ToolProfiles.Parse("plc,hmii", ProfileSource.Environment, "test");
        Assert.Equal("all", p.Name);
        Assert.Contains("hmii", p.Reason);
    }

    [Fact]
    public void Core_HidesTheSpecialistToolsets()
    {
        var p = ToolProfiles.Parse("core", ProfileSource.Default, "test");
        foreach (var s in new[] { Toolset.Hmi, Toolset.Scope, Toolset.Nc, Toolset.Safety, Toolset.Ibn, Toolset.Io, Toolset.License })
            Assert.False(p.Shows(s), s.ToString());
        foreach (var s in new[] { Toolset.Meta, Toolset.Project, Toolset.Build, Toolset.Plc, Toolset.Ads, Toolset.Runtime })
            Assert.True(p.Shows(s), s.ToString());
    }
}
