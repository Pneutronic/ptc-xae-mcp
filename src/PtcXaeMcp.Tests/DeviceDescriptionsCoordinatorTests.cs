using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class DeviceDescriptionsCoordinatorTests
{
    [Fact]
    public void Reload_CommandAvailable_ReportsRuntime()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            FakeReloadDeviceDescriptionsResult =
                new DeviceDescriptionReloadResult(true, "Device descriptions reloaded.", TimeSpan.FromSeconds(2.34)),
        };

        string result = new DeviceDescriptionsCoordinator(fake).Reload();

        Assert.Equal("Device descriptions reloaded in 2.3 s.", result);
        Assert.Contains("ReloadDeviceDescriptions()", fake.CallLog);
    }

    [Fact]
    public void Reload_CommandNotAvailable_IsAnErrorWithTheReasonAndTheMenuPath()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            FakeReloadDeviceDescriptionsResult = new DeviceDescriptionReloadResult(
                false, "The XAE reports the Reload Device Descriptions command as unavailable.", TimeSpan.Zero),
        };

        string result = new DeviceDescriptionsCoordinator(fake).Reload();

        Assert.StartsWith("[Error]", result);
        Assert.Contains("unavailable", result);
        Assert.Contains("TwinCAT > EtherCAT Devices > Reload Device Descriptions", result);
    }

    [Fact]
    public void Reload_DialogBlocksTheXae_NamesTheDialogAndAnswersNothing()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ReloadDeviceDescriptionsThrows =
                new XaeBlockedException("XAE is blocked by dialog 'Reload'.", "Reload", null),
        };

        string result = new DeviceDescriptionsCoordinator(fake).Reload();

        Assert.StartsWith("[Error]", result);
        Assert.Contains("Blocking dialog: 'Reload'", result);
        Assert.Contains("Nothing was answered", result);
        Assert.Contains("TwinCAT > EtherCAT Devices > Reload Device Descriptions", result);
    }

    [Fact]
    public void Reload_SessionThrows_IsAnError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ReloadDeviceDescriptionsThrows = new InvalidOperationException("No active XAE."),
        };

        string result = new DeviceDescriptionsCoordinator(fake).Reload();

        Assert.Equal("[Error] No active XAE.", result);
    }

    [Fact]
    public void Reload_NoSolution_IsAnErrorAndTheCommandIsNeverRaised()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        string result = new DeviceDescriptionsCoordinator(fake).Reload();

        Assert.StartsWith("[Error]", result);
        Assert.DoesNotContain("ReloadDeviceDescriptions()", fake.CallLog);
    }

    [Fact]
    public void Command_IsAddressedByIdentityNotByLocalizedName()
    {
        Assert.Equal("{40EE08E0-8FB4-46E9-BAAB-100E60019B7B}", DeviceDescriptionReloadCommand.Guid);
        Assert.Equal(4520, DeviceDescriptionReloadCommand.Id);
    }
}
