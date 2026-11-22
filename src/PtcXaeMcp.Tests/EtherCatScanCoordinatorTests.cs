using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class EtherCatScanCoordinatorTests
{
    private const string Master = "TIID^Device 1 (EtherCAT)";
    private static EtherCatScanBox Box(int pos, string name, long product, long rev = 1) => new(pos, name, 2, product, rev);

    private static FakeTwinCatSession Session() => new() { IsConnected = true, SolutionOpen = true };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPath_ErrorBeforeAnySessionCall(string path)
    {
        var fake = Session();
        string result = new EtherCatScanCoordinator(fake).ScanCompare(path);
        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ScanEtherCatMaster", StringComparison.Ordinal));
    }

    [Fact]
    public void NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Assert.StartsWith("[Error]", new EtherCatScanCoordinator(fake).ScanCompare(Master));
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ScanEtherCatMaster", StringComparison.Ordinal));
    }

    [Fact]
    public void Identical_ReportsIdenticalAndNoDifferences()
    {
        var fake = Session();
        var chain = new[] { Box(1, "Term 1 (EK1100)", 10), Box(2, "Term 2 (EL1008)", 20) };
        fake.FakeScanData = new(Master, chain, chain);

        var doc = JsonDocument.Parse(new EtherCatScanCoordinator(fake).ScanCompare(Master)).RootElement;
        Assert.True(doc.GetProperty("identical").GetBoolean());
        Assert.Equal(0, doc.GetProperty("differences").GetArrayLength());
        Assert.Equal(2, doc.GetProperty("configured").GetArrayLength());
        Assert.Equal(2, doc.GetProperty("scanned").GetArrayLength());
        Assert.Equal(Master, doc.GetProperty("treePath").GetString());
    }

    [Fact]
    public void PulledTerminal_IsReportedAsMissingWithItsIdentity()
    {
        var fake = Session();
        fake.FakeScanData = new(Master,
            [Box(1, "Term 1 (EK1100)", 10), Box(2, "Term 2 (EL1008)", 20)],
            [Box(1, "Term 1 (EK1100)", 10)]);

        var doc = JsonDocument.Parse(new EtherCatScanCoordinator(fake).ScanCompare(Master)).RootElement;
        Assert.False(doc.GetProperty("identical").GetBoolean());
        var d = doc.GetProperty("differences")[0];
        Assert.Equal("missing", d.GetProperty("kind").GetString());
        Assert.Equal(2, d.GetProperty("position").GetInt32());
        Assert.Equal("Term 2 (EL1008)", d.GetProperty("configured").GetProperty("name").GetString());
        Assert.Equal(20, d.GetProperty("configured").GetProperty("productCode").GetInt64());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("scanned").ValueKind);
    }

    [Fact]
    public void SaysTheProjectWasNotChanged()
    {
        var fake = Session();
        string result = new EtherCatScanCoordinator(fake).ScanCompare(Master);
        Assert.Contains("unchanged", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fake.CallLog, c => c.Contains("Save", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionThrows_OneErrorPrefixAndTheConfigModeHint()
    {
        var fake = Session();
        fake.ScanEtherCatMasterThrows = new InvalidOperationException("ScanBoxes was refused");
        string result = new EtherCatScanCoordinator(fake).ScanCompare(Master);

        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain("[Error] [Error]", result);
        Assert.Contains("ScanBoxes was refused", result);
        Assert.Contains("twincat_set_system_mode", result);
    }

    [Fact]
    public void LeftoverTemporaryDevice_IsNamedInTheError()
    {
        var fake = Session();
        fake.ScanEtherCatMasterThrows = new InvalidOperationException(
            "The temporary device 'PtcScanTmp_ab12cd34' could not be removed: busy. Delete it by hand.");
        string result = new EtherCatScanCoordinator(fake).ScanCompare(Master);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("PtcScanTmp_ab12cd34", result);
    }
}
