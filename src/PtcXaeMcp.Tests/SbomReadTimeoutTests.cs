using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>A slow or unstable link: timeouts on single reads must not hide the rest. All values invented.</summary>
public class SbomReadTimeoutTests
{
    private const string NetId = "10.20.30.40.2.1";
    private const string Timeout = "ClientSyncTimeOut";

    private static EtherCatProjectedSlave Box(string name, int? address, long product) =>
        new($"TIID^D^{name}", name, 2, product, 1, address);

    private static EtherCatMasterIdentities Read(FakeEtherCatMaster master, params EtherCatProjectedSlave[] boxes) =>
        new EtherCatIdentityReader((_, _) => master).Read(
            new EtherCatMasterConfig("TIID^D", "D", NetId, 0xFFFF, "test", null, boxes), null, null, false);

    private static FakeEtherCatMaster ThreeSlaves() =>
        new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 5), (1002, 0x08, 0, 2, 12, 1, 6), (1003, 0x08, 0, 2, 13, 1, 7));

    private static EtherCatProjectedSlave[] ThreeBoxes() => [Box("A", 1001, 11), Box("B", 1002, 12), Box("C", 1003, 13)];

    // ---- the retry decorator ---------------------------------------------------------------------

    private sealed class Scripted(params AdsRawReadResult[] answers) : IAdsRawReader
    {
        private int _next;
        public int Calls { get; private set; }

        public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength)
        {
            Calls++;
            return answers[Math.Min(_next++, answers.Length - 1)];
        }
    }

    [Fact]
    public void Retry_RepeatsOnceOnAClientTimeout()
    {
        var inner = new Scripted(AdsRawReadResult.Failure(1861, Timeout), AdsRawReadResult.Success([1, 2]));
        var result = new RetryOnTimeoutReader(inner).Read(1, 2, 8);
        Assert.True(result.Ok);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Retry_GivesUpAfterOneRepeat_AndIgnoresOtherErrors()
    {
        var timeouts = new Scripted(AdsRawReadResult.Failure(1861, Timeout));
        Assert.Equal(1861u, new RetryOnTimeoutReader(timeouts).Read(1, 2, 8).AdsError);
        Assert.Equal(2, timeouts.Calls);

        var other = new Scripted(AdsRawReadResult.Failure(1809, "DeviceSymbolNotFound"));
        new RetryOnTimeoutReader(other).Read(1, 2, 8);
        Assert.Equal(1, other.Calls);
    }

    [Fact]
    public void Retry_Disposes_TheInnerReader()
    {
        var inner = new FakeEtherCatMaster();
        new RetryOnTimeoutReader(inner).Dispose();
        Assert.True(inner.Disposed);
    }

    [Fact]
    public void Mdp_ARepeatedTimeout_StillGivesAnInventory()
    {
        var dev = new FakeMdpDevice().WithDevice("BTN-0001", "BIC-0001").AddModule(0x000B, 8, "CPU");
        var flaky = new FlakyOnce(dev);
        var r = new MdpInventoryReader(flaky).Read("10.0.0.1.1.1", null, true);
        Assert.Equal(IpcInventoryStatus.Ok, r.Status);
        Assert.True(flaky.Failed);
    }

    private sealed class FlakyOnce(IAdsRawReader inner) : IAdsRawReader
    {
        public bool Failed { get; private set; }

        public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength)
        {
            if (Failed) return inner.Read(indexGroup, indexOffset, maxLength);
            Failed = true;
            return AdsRawReadResult.Failure(1861, Timeout);
        }
    }

    // ---- EtherCAT: failed list reads do not stop the per-slave reads ---------------------------------

    [Fact]
    public void StateAndLinkTimeout_StillReadsEverySlave_AndMakesTheMasterPartial()
    {
        var master = ThreeSlaves().Fail(0x9, 0, 1861, Timeout);

        var r = Read(master, ThreeBoxes());

        Assert.Equal(EtherCatMasterStatus.Partial, r.Status);
        Assert.Equal(3, r.Slaves.Count(s => s.Online is not null));
        Assert.All(r.Slaves, s =>
        {
            Assert.Null(s.State);
            Assert.Null(s.Link);
            Assert.Equal(EtherCatReadStatus.Partial, s.ReadStatus);
            Assert.Contains(s.ReadErrors!, e => e.StartsWith("IG 0x9", StringComparison.Ordinal));
        });
        Assert.Equal(2, master.Reads.Count(x => x.Group == 0x9)); // read, then repeated once
        Assert.Contains(r.Warnings, w => w.Contains("3 of 3 slaves could not be read completely (addresses 1001, 1002, 1003)", StringComparison.Ordinal));
        Assert.False(r.SafetyIdentityComplete);
    }

    [Fact]
    public void FixedAddressTimeout_FallsBackToTheProjectedAddresses()
    {
        var master = ThreeSlaves().Fail(0x7, 0, 1861, Timeout);

        var r = Read(master, ThreeBoxes());

        Assert.Equal(EtherCatMasterStatus.Partial, r.Status);
        Assert.Equal([1001, 1002, 1003], r.Slaves.Where(s => s.Online is not null).Select(s => s.Address).ToArray());
        Assert.Contains(r.Warnings, w => w.Contains("using the 3 projected fixed addresses", StringComparison.Ordinal));
        Assert.All(r.Slaves, s => Assert.Null(s.State));
    }

    [Fact]
    public void FixedAddressTimeout_WithoutProjectedAddresses_ReadsNothing_ButSaysSo()
    {
        var master = ThreeSlaves().Fail(0x7, 0, 1861, Timeout);
        var r = Read(master, Box("A", null, 11));
        Assert.Equal(EtherCatMasterStatus.Partial, r.Status);
        Assert.DoesNotContain(r.Slaves, s => s.Online is not null);
        Assert.Contains(r.Warnings, w => w.Contains("no slave could be read", StringComparison.Ordinal));
        Assert.Equal(3, r.SlaveCount);
    }

    [Fact]
    public void SelectedSlaveTimeout_FailsOnlyThatSlave()
    {
        var master = ThreeSlaves().Fail(0x11, 1002, 1861, Timeout);

        var r = Read(master, ThreeBoxes());

        Assert.Equal(EtherCatMasterStatus.Partial, r.Status);
        var failed = r.Slaves.Single(s => s.Address == 1002);
        Assert.Equal(EtherCatReadStatus.Failed, failed.ReadStatus);
        Assert.Null(failed.Online);
        Assert.Contains("IG 0x11", failed.ReadErrors![0], StringComparison.Ordinal);
        Assert.Equal(EtherCatReadStatus.Ok, r.Slaves.Single(s => s.Address == 1001).ReadStatus);
        Assert.Equal(EtherCatReadStatus.Ok, r.Slaves.Single(s => s.Address == 1003).ReadStatus);
        Assert.Equal(2, master.Reads.Count(x => x.Group == 0x11 && x.Offset == 1002));
        Assert.Contains(r.Warnings, w => w.Contains("1 of 3 slaves could not be read completely (addresses 1002)", StringComparison.Ordinal));

        var summary = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [r]).Summary;
        Assert.Equal(3, summary.Slaves);
        Assert.Equal(2, summary.SlavesRead);
        Assert.Equal(1, summary.SlavesFailed);
    }

    [Fact]
    public void FullyReadMaster_IsOk_WithAllSlavesOk()
    {
        var r = Read(ThreeSlaves(), ThreeBoxes());
        Assert.Equal(EtherCatMasterStatus.Ok, r.Status);
        Assert.All(r.Slaves, s => Assert.Equal(EtherCatReadStatus.Ok, s.ReadStatus));
        Assert.True(new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [r]).Success);
    }

    [Fact]
    public void FewerAddressesThanSlaves_IsPartial()
    {
        var master = ThreeSlaves().Set(0x7, 0, [0xE9, 0x03, 0xEA, 0x03]);
        var r = Read(master, ThreeBoxes());
        Assert.Equal(EtherCatMasterStatus.Partial, r.Status);
        Assert.Equal(3, r.SlaveCount);
        Assert.Equal(2, r.Slaves.Count(s => s.Online is not null));
    }

    // ---- CoE timeouts ------------------------------------------------------------------------------

    private static EtherCatMasterIdentities ReadSlaves(Dictionary<int, FakeDeviceSlave> slaves, Dictionary<int, string>? names = null)
    {
        var master = new FakeEtherCatMaster().WithSlaves(slaves.Keys.Order().Select(a => (a, (byte)0x08, (byte)0x00, 2u, (uint)a, 1u, 0u)).ToArray());
        var boxes = slaves.Keys.Order().Select(a => new EtherCatProjectedSlave($"TIID^D^B{a}", names?.GetValueOrDefault(a) ?? $"Box {a}", 2, a, 1, a)).ToArray();
        var reader = new EtherCatIdentityReader((_, port) => port == 0xFFFF ? master : slaves[port]);
        return reader.Read(new EtherCatMasterConfig("TIID^D", "D", NetId, 0xFFFF, "test", null, boxes), null, null, false, true, true);
    }

    [Fact]
    public void CoeNameTimeout_MakesTheSlavePartial()
    {
        var slave = new FakeDeviceSlave().Terminal("EL1008", 0).Fail(0xF302, 0x10080000, 1861, Timeout);
        var rec = ReadSlaves(new() { [1001] = slave }).Slaves.Single();
        Assert.Equal(EtherCatReadStatus.Partial, rec.ReadStatus);
        Assert.Equal(EtherCatCoeStatus.Error, rec.Coe!.Status);
        Assert.Contains(rec.ReadErrors!, e => e.StartsWith("CoE 0x1008", StringComparison.Ordinal));
        Assert.Equal(2, slave.Log.Count(l => l.Offset == 0x10080000)); // retried once
    }

    [Fact]
    public void AbsentObjects_AreNoReadErrors()
    {
        var rec = ReadSlaves(new() { [1001] = new FakeDeviceSlave().Terminal("EL1008", 0) }).Slaves.Single();
        Assert.Equal(EtherCatReadStatus.Ok, rec.ReadStatus);
        Assert.Empty(rec.ReadErrors!);
    }

    // ---- safety: unknown is not missing ------------------------------------------------------------------

    [Fact]
    public void SafetyDevice_WhoseF980TimesOut_IsUnknown_NotMissing()
    {
        var slaves = new Dictionary<int, FakeDeviceSlave>
        {
            [1001] = new FakeDeviceSlave().Terminal("EL1008", 0),
            [1004] = new FakeDeviceSlave().Terminal("EL1918", 0)
                .Fail(0xF302, 0xF9800001, 1861, Timeout).Fail(0xF302, 0xF9800002, 1861, Timeout),
            [1005] = new FakeDeviceSlave().Terminal("EL6910", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0),
        };

        var master = ReadSlaves(slaves);
        var unknown = master.Slaves.Single(s => s.Address == 1004);
        var missing = master.Slaves.Single(s => s.Address == 1005);

        Assert.True(unknown.SafetyIdentityUnknown);
        Assert.False(unknown.SafetySerialMissing);
        Assert.Contains("0xF980", unknown.SafetyIdentityUnknownReason, StringComparison.Ordinal);
        Assert.Contains("1861", unknown.SafetyIdentityUnknownReason, StringComparison.Ordinal);
        Assert.True(missing.SafetySerialMissing);
        Assert.False(missing.SafetyIdentityUnknown);

        Assert.Contains(master.Warnings, w => w.StartsWith("Safety identity could not be read for (1)", StringComparison.Ordinal) && w.Contains("address 1004", StringComparison.Ordinal));
        Assert.Contains(master.Warnings, w => w.StartsWith("Safety devices without a unique identifier (1)", StringComparison.Ordinal) && w.Contains("address 1005", StringComparison.Ordinal));
        Assert.False(master.SafetyIdentityComplete);

        var summary = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [master]).Summary;
        Assert.Equal(2, summary.SafetyDevices);
        Assert.Equal(0, summary.SafetyWithSerial);
        Assert.Equal(1, summary.SafetyUnknown);
    }

    [Fact]
    public void SafetyDevice_WhoseCoeNameTimesOut_IsKnownByItsProjectedName_AndUnknown()
    {
        var slaves = new Dictionary<int, FakeDeviceSlave>
        {
            [1004] = new FakeDeviceSlave().Terminal("EL1918", 0).Fail(0xF302, 0x10080000, 1861, Timeout),
        };
        var rec = ReadSlaves(slaves, new() { [1004] = "Term 4 (EL1918)" }).Slaves.Single();
        Assert.True(rec.Details!.Safety!.IsSafetyDevice);
        Assert.True(rec.SafetyIdentityUnknown);
        Assert.False(rec.SafetySerialMissing);
    }

    [Fact]
    public void SafetyDevice_WithATimeoutButAnIdentifier_IsKnown_AndTheSlavePartial()
    {
        var slave = new FakeDeviceSlave().Terminal("EL1918", 0).CoeU16(0xF980, 1, 5).CoeU32(0xF980, 2, 4321)
            .Fail(0xF302, 0x100A0000, 1861, Timeout);
        var rec = ReadSlaves(new() { [1001] = slave }).Slaves.Single();
        Assert.False(rec.SafetyIdentityUnknown);
        Assert.False(rec.SafetySerialMissing);
        Assert.Equal("4321", rec.SerialNo);
        Assert.Equal(EtherCatReadStatus.Partial, rec.ReadStatus);
    }

    // ---- timeoutMs ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(50)]
    [InlineData(30001)]
    [InlineData(-1)]
    public void TimeoutOutOfRange_IsAnError(int timeoutMs)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        Assert.StartsWith("[Error]", new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, null, false, true, true, timeoutMs), StringComparison.Ordinal);
        Assert.StartsWith("[Error]", new IpcInventoryCoordinator(fake).ReadInventory("1.2.3.4.1.1", null, true, timeoutMs), StringComparison.Ordinal);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Timeout_DefaultsTo5000_AndIsPassedOn()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, null, false);
        new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, null, false, true, true, 12000);
        new IpcInventoryCoordinator(fake).ReadInventory("1.2.3.4.1.1", null, true);
        new IpcInventoryCoordinator(fake).ReadInventory("1.2.3.4.1.1", null, true, 30000);
        Assert.Equal(["ReadEtherCatIdentities(,,,False,True,True,5000)", "ReadEtherCatIdentities(,,,False,True,True,12000)",
            "ReadIpcInventory(1.2.3.4.1.1,all,True,5000)", "ReadIpcInventory(1.2.3.4.1.1,all,True,30000)"], fake.CallLog.ToArray());
    }

    // ---- JSON ------------------------------------------------------------------------------------------------

    [Fact]
    public void Json_CarriesReadStatusUnknownAndTheNewSummary()
    {
        var master = ThreeSlaves().Fail(0x11, 1002, 1861, Timeout);
        var result = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [Read(master, ThreeBoxes())]);
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, FakeEtherCatIdentitiesResult = result };

        using var doc = JsonDocument.Parse(new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, null, false));

        Assert.Equal("partial", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("safetyIdentityComplete").GetBoolean());
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("slaves").GetInt32());
        Assert.Equal(2, summary.GetProperty("slavesRead").GetInt32());
        Assert.Equal(1, summary.GetProperty("slavesFailed").GetInt32());
        Assert.Equal(0, summary.GetProperty("safetyUnknown").GetInt32());
        Assert.Equal("partial", doc.RootElement.GetProperty("masters")[0].GetProperty("status").GetString());
        var slave = doc.RootElement.GetProperty("masters")[0].GetProperty("slaves").EnumerateArray().Single(s => s.GetProperty("address").GetInt32() == 1002);
        Assert.Equal("failed", slave.GetProperty("readStatus").GetString());
        Assert.NotEmpty(slave.GetProperty("readErrors").EnumerateArray());
        Assert.False(slave.GetProperty("safetyIdentityUnknown").GetBoolean());
    }
}
