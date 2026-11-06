using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class IoMappingCoordinatorTests
{
    // ── LinkVariables ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "TIID^Channel 1")]
    [InlineData("   ", "TIID^Channel 1")]
    public void LinkVariables_EmptyV1_ReturnsError(string v1, string v2)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var result = new IoMappingCoordinator(fake).LinkVariables(v1, v2);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("variable1TreePath", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LinkVariables", string.Join(",", fake.CallLog));
    }

    [Theory]
    [InlineData("TIPC^PLC^MAIN.bVar", "")]
    [InlineData("TIPC^PLC^MAIN.bVar", "   ")]
    public void LinkVariables_EmptyV2_ReturnsError(string v1, string v2)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var result = new IoMappingCoordinator(fake).LinkVariables(v1, v2);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("variable2TreePath", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LinkVariables", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void LinkVariables_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };

        var result = new IoMappingCoordinator(fake).LinkVariables("TIPC^PLC^MAIN.bVar", "TIID^Device 1^Ch1");

        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain("LinkVariables", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void LinkVariables_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        var result = new IoMappingCoordinator(fake).LinkVariables("TIPC^PLC^MAIN.bVar", "TIID^Device 1^Ch1");

        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain("LinkVariables", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void LinkVariables_HappyPath_ReturnsSuccess()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var result = new IoMappingCoordinator(fake).LinkVariables(
            "TIPC^PLC^MAIN.bVar", "TIID^Device 1^Ch1");

        Assert.False(result.StartsWith("[Error]", StringComparison.Ordinal), $"Expected success but got: {result}");
        Assert.Contains("linked", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LinkVariables(TIPC^PLC^MAIN.bVar, TIID^Device 1^Ch1)", fake.CallLog);
    }

    [Fact]
    public void LinkVariables_SessionThrows_ReturnsError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            LinkVariablesThrows = new InvalidOperationException("TSM_E_MISMATCHINGITEMS: mismatched task"),
        };

        var result = new IoMappingCoordinator(fake).LinkVariables(
            "TIPC^PLC^MAIN.bVar", "TIID^Device 1^Ch1");

        Assert.StartsWith("[Error]", result);
        Assert.Contains("MISMATCHINGITEMS", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── UnlinkVariables ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UnlinkVariables_EmptyV1_ReturnsError(string v1)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var result = new IoMappingCoordinator(fake).UnlinkVariables(v1);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("variable1TreePath", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UnlinkVariables", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void UnlinkVariables_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };

        var result = new IoMappingCoordinator(fake).UnlinkVariables("TIPC^PLC^MAIN.bVar");

        Assert.StartsWith("[Error]", result);
    }

    [Fact]
    public void UnlinkVariables_SpecificLink_ReturnsSuccess()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var result = new IoMappingCoordinator(fake).UnlinkVariables(
            "TIPC^PLC^MAIN.bVar", "TIID^Device 1^Ch1");

        Assert.False(result.StartsWith("[Error]", StringComparison.Ordinal), $"Expected success but got: {result}");
        Assert.Contains("unlinked", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnlinkVariables(TIPC^PLC^MAIN.bVar, TIID^Device 1^Ch1)", fake.CallLog);
    }

    [Fact]
    public void UnlinkVariables_AllLinks_ReturnsSuccess()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var result = new IoMappingCoordinator(fake).UnlinkVariables("TIPC^PLC^MAIN.bVar");

        Assert.False(result.StartsWith("[Error]", StringComparison.Ordinal), $"Expected success but got: {result}");
        Assert.Contains("unlinked", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnlinkVariables(TIPC^PLC^MAIN.bVar, )", fake.CallLog);
    }

    [Fact]
    public void UnlinkVariables_NoExistingLink_ReturnsNotLinked()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            UnlinkVariablesThrows = new InvalidOperationException("S_FALSE: variables share no connection"),
        };

        var result = new IoMappingCoordinator(fake).UnlinkVariables(
            "TIPC^PLC^MAIN.bVar", "TIID^Device 1^Ch1");

        Assert.StartsWith("[Error]", result);
        Assert.Contains("S_FALSE", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── GetMappingInfo ─────────────────────────────────────────────────────────

    [Fact]
    public void GetMappingInfo_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };

        var result = new IoMappingCoordinator(fake).GetMappingInfo();

        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain("GetMappingInfo", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void GetMappingInfo_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        var result = new IoMappingCoordinator(fake).GetMappingInfo();

        Assert.StartsWith("[Error]", result);
    }

    [Fact]
    public void GetMappingInfo_HappyPath_WritesFileNextToSolution()
    {
        string slnDir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(slnDir);
        try
        {
            string slnPath = Path.Combine(slnDir, "Customer.sln");
            var fake = new FakeTwinCatSession
            {
                IsConnected = true,
                SolutionOpen = true,
                ActiveSlnPath = slnPath,
                MappingInfoXml = "<VarLinks><Link/></VarLinks>",
            };

            var result = new IoMappingCoordinator(fake).GetMappingInfo();

            // Must report the output path, not raw XML
            Assert.False(result.StartsWith("[Error]", StringComparison.Ordinal), $"Expected success but got: {result}");
            Assert.Contains("GetMappingInfo()", fake.CallLog);

            // File must land next to the open solution, not the tool's repo
            string expectedPath = Path.Combine(slnDir, "Mappings.xml");
            Assert.Contains(expectedPath, result);
            Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}");
            Assert.Contains("VarLinks", File.ReadAllText(expectedPath));
        }
        finally
        {
            Directory.Delete(slnDir, recursive: true);
        }
    }

    [Fact]
    public void GetMappingInfo_SessionThrows_ReturnsError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            GetMappingInfoThrows = new InvalidOperationException("ProduceMappingInfo failed: boom"),
        };

        var result = new IoMappingCoordinator(fake).GetMappingInfo();

        Assert.StartsWith("[Error]", result);
        Assert.Contains("ProduceMappingInfo failed", result);
    }

    // ── SetDeviceDisabled ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SetDeviceDisabled_EmptyPath_ReturnsError(string path)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var result = new IoMappingCoordinator(fake).SetDeviceDisabled(path, true);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("treePath", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetDeviceDisabled", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void SetDeviceDisabled_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };

        var result = new IoMappingCoordinator(fake).SetDeviceDisabled("TIID^Device 1", true);

        Assert.StartsWith("[Error]", result);
    }

    [Fact]
    public void SetDeviceDisabled_Disable_ReturnsSuccess()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var result = new IoMappingCoordinator(fake).SetDeviceDisabled("TIID^Device 1", true);

        Assert.False(result.StartsWith("[Error]", StringComparison.Ordinal), $"Expected success but got: {result}");
        Assert.Contains("disabled", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SetDeviceDisabled(TIID^Device 1, True)", fake.CallLog);
    }

    [Fact]
    public void SetDeviceDisabled_Enable_ReturnsSuccess()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };

        var result = new IoMappingCoordinator(fake).SetDeviceDisabled("TIID^Device 1", false);

        Assert.False(result.StartsWith("[Error]", StringComparison.Ordinal), $"Expected success but got: {result}");
        Assert.Contains("enabled", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SetDeviceDisabled(TIID^Device 1, False)", fake.CallLog);
    }

    [Fact]
    public void SetDeviceDisabled_SessionThrows_ReturnsError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            SetDeviceDisabledThrows = new InvalidOperationException("Tree path not found: TIID^Nope"),
        };

        var result = new IoMappingCoordinator(fake).SetDeviceDisabled("TIID^Nope", true);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }
}
