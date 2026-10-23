using System.Text;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Add-box failure hint: an ESI on disk that the XAE has not loaded (reload device descriptions) versus no ESI at
/// all. Uses a per-test temp ESI directory, like the catalog scanner tests.
/// </summary>
public sealed class EsiInstallHintTests : IDisposable
{
    private const string Master = "TIID^Device 1 (EtherCAT)";

    private readonly string _dir;

    public EsiInstallHintTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ptc-esi-hint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void WriteEsi(string fileName, string type, string productCode = "#x03f03052", string revision = "#x00110000")
    {
        string xml =
            "<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>\r\n<EtherCATInfo Version=\"1.2\">" +
            "<Vendor><Id>#x00000999</Id><Name>Acme</Name></Vendor><Descriptions><Devices>" +
            $"<Device Physics=\"KK\"><Type ProductCode=\"{productCode}\" RevisionNo=\"{revision}\">{type}</Type>" +
            $"<Name LcId=\"1033\">{type} test device</Name></Device></Devices></Descriptions></EtherCATInfo>";
        File.WriteAllBytes(Path.Combine(_dir, fileName), Encoding.Latin1.GetBytes(xml));
    }

    [Fact]
    public void For_TypeIsInAnEsiOnDisk_SaysTheXaeHasNotLoadedIt()
    {
        WriteEsi("Acme.xml", "ACM1000");

        string? hint = EsiInstallHint.For("ACM1000", _dir);

        Assert.NotNull(hint);
        Assert.Contains("The ESI for ACM1000 is installed (Acme.xml)", hint);
        Assert.Contains("but the XAE has not loaded it: run twincat_reload_device_descriptions, then retry.", hint);
    }

    [Fact]
    public void For_RevisionedVInfoMatchesItsBaseType()
    {
        WriteEsi("Acme.xml", "ACM1000");

        string? hint = EsiInstallHint.For("ACM1000-0000-0099", _dir);

        Assert.Contains("The ESI for ACM1000 is installed", hint);
    }

    [Fact]
    public void For_NoEsiDescribesTheType_SaysToPlaceOne()
    {
        WriteEsi("Acme.xml", "ACM1000");

        string? hint = EsiInstallHint.For("ZZZ9999", _dir);

        Assert.NotNull(hint);
        Assert.Equal(
            $"No ESI describes ZZZ9999 under {_dir}: place the vendor's ESI there, then run twincat_reload_device_descriptions.",
            hint);
    }

    [Fact]
    public void For_EsiDirectoryMissing_AddsNothing()
    {
        Assert.Null(EsiInstallHint.For("ACM1000", Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void AddBox_RefusedType_ErrorCarriesTheHintAndKeepsTheRevisionHint()
    {
        WriteEsi("Acme.xml", "ACM1000");
        var tree = new FakeSysManTree();
        tree.Add("TIID");
        tree.Add(Master, itemType: 2, subType: 111);
        tree.RejectedVInfos.Add("ACM1000");
        var adder = new EtherCatNodeAdder(tree, (_, _, _, _) => { }, vInfo => EsiInstallHint.For(vInfo, _dir));

        var ex = Assert.Throws<InvalidOperationException>(
            () => adder.AddBox(tree.Get(Master), new EtherCatBoxSpec("ACM1000", "Term 1 (ACM1000)"), null));
        Assert.Contains("The ESI for ACM1000 is installed (Acme.xml) but the XAE has not loaded it", ex.Message);
        Assert.Contains("TwinCAT accepts only the base vInfo without a revision suffix", ex.Message);
    }

    [Fact]
    public void AddBox_RefusedType_WithoutHintProvider_KeepsTodaysMessage()
    {
        var tree = new FakeSysManTree();
        tree.Add("TIID");
        tree.Add(Master, itemType: 2, subType: 111);
        tree.RejectedVInfos.Add("ACM1000");
        var adder = new EtherCatNodeAdder(tree, (_, _, _, _) => { });

        var ex = Assert.Throws<InvalidOperationException>(
            () => adder.AddBox(tree.Get(Master), new EtherCatBoxSpec("ACM1000", "Term 1 (ACM1000)"), null));
        Assert.DoesNotContain("twincat_reload_device_descriptions", ex.Message);
        Assert.Contains("HINT: the ESI/device description for this vInfo may be missing", ex.Message);
    }
}
