using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Projected configuration: relative master NetId, one walk per box, passive boxes, SoE serial, safety roles.</summary>
public class EtherCatProjectionTests
{
    // ---- relative master NetId ------------------------------------------------------------

    [Fact]
    public void RelativeNetId_TakesTheFirstFourOctetsOfTheTarget()
    {
        Assert.Equal("192.0.2.10.2.1", EtherCatProjection.ApplyTargetPrefix("0.0.0.0.2.1", "192.0.2.10.1.1", out bool relative));
        Assert.True(relative);
    }

    [Fact]
    public void AbsoluteNetId_IsKept()
    {
        Assert.Equal("10.1.2.3.2.1", EtherCatProjection.ApplyTargetPrefix("10.1.2.3.2.1", "192.0.2.10.1.1", out bool relative));
        Assert.False(relative);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void RelativeNetId_WithoutATarget_IsNull(string? target)
    {
        Assert.Null(EtherCatProjection.ApplyTargetPrefix("0.0.0.0.2.1", target, out bool relative));
        Assert.True(relative);
    }

    [Fact]
    public void MasterWithoutNetId_IsAddressNotFound_AndNamesTheSource()
    {
        var config = new EtherCatMasterConfig("TIID^D", "D", null, 0xFFFF, "//AmsNetId (relative NetId 0.0.0.0.2.1, but no target NetId is known)", "<x/>", []);
        var r = new EtherCatIdentityReader((_, _) => new FakeEtherCatMaster()).Read(config, null, null, false);
        Assert.Equal(EtherCatMasterStatus.AddressNotFound, r.Status);
        Assert.Contains("no target NetId is known", r.Message, StringComparison.Ordinal);
    }

    // ---- one walk per box ---------------------------------------------------------------------

    private sealed class Node(string name, string path, string xml, params Node[] children) : EtherCatProjection.IProjectionNode
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public string Xml { get; } = xml;
        public IEnumerable<EtherCatProjection.IProjectionNode> Children { get; } = children;
    }

    private static string Info(int? address, int product) =>
        $"<TreeItem><EtherCAT><Slave><Info><VendorId>2</VendorId><ProductCode>{product}</ProductCode><RevisionNo>1</RevisionNo>" +
        (address is null ? string.Empty : $"<PhysAddr>{address}</PhysAddr>") + "</Info></Slave></EtherCAT></TreeItem>";

    private static Node Box(string name, int? address, int product, params Node[] children) =>
        new(name, $"TIID^D^{name}", Info(address, product), children);

    [Fact]
    public void CollectSlaves_WalksEachBoxOnce_InTreeOrder()
    {
        // The same boxes are reachable below several couplers of the chain (each box 2-4 times in the live tree).
        var term1 = Box("Term 1 (EL1008)", 1004, 4);
        var term2 = Box("Term 2 (EL2008)", 1005, 5);
        var ek1100 = Box("Box 3 (EK1100)", 1003, 3, term1, term2);
        var ek1122 = Box("Box 2 (EK1122)", 1002, 2, ek1100, term1, term2);
        var ek1200 = Box("Box 1 (EK1200)", 1001, 1, ek1122, ek1100, term1, term2);
        var ax5805 = Box("Box 5 (AX5805)", 1007, 7);
        var ax5206 = Box("Box 4 (AX5206)", 1006, 6, ax5805);
        var channel = new Node("Channel 1", "TIID^D^Channel 1", "<TreeItem/>");
        var master = new Node("D", "TIID^D", "<TreeItem/>", ek1200, ax5206, channel);

        var slaves = EtherCatProjection.CollectSlaves(master);

        Assert.Equal(["Box 1 (EK1200)", "Box 2 (EK1122)", "Box 3 (EK1100)", "Term 1 (EL1008)", "Term 2 (EL2008)", "Box 4 (AX5206)", "Box 5 (AX5805)"],
            slaves.Select(s => s.Name).ToArray());
        Assert.Equal([1001, 1002, 1003, 1004, 1005, 1006, 1007], slaves.Select(s => s.FixedAddress).ToArray());
        Assert.Equal(slaves.Count, slaves.Select(s => s.TreePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CollectSlaves_StopsAtTheDepthLimit()
    {
        var deep = Box("C", 3, 3);
        var master = new Node("D", "TIID^D", "<TreeItem/>", Box("A", 1, 1, Box("B", 2, 2, deep)));
        Assert.Equal(2, EtherCatProjection.CollectSlaves(master, maxDepth: 2).Count);
    }

    // ---- passive boxes --------------------------------------------------------------------------

    [Fact]
    public void PassiveBoxes_EndCapAndEbusCoupler_AreMarked()
    {
        var master = new Node("D", "TIID^D", "<TreeItem/>",
            new Node("Term 9 (EL9011)", "TIID^D^Term 9", "<TreeItem/>"),
            Box("Box 0 (EK1200-5000)", null, 9),
            Box("Term 3 (EL1008)", null, 3),
            Box("Term 4 (EL9011)", 1009, 4));

        var slaves = EtherCatProjection.CollectSlaves(master);

        Assert.True(slaves.Single(s => s.Name == "Term 9 (EL9011)").Passive);
        Assert.True(slaves.Single(s => s.Name == "Box 0 (EK1200-5000)").Passive);
        Assert.False(slaves.Single(s => s.Name == "Term 3 (EL1008)").Passive);
        Assert.False(slaves.Single(s => s.Name == "Term 4 (EL9011)").Passive); // it has an address
    }

    [Fact]
    public void PassiveBoxes_AreListedButNeverJoined_NorCountedInTheOrderJoin()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 0), (1002, 0x08, 0, 2, 12, 1, 0));
        var boxes = new[]
        {
            new EtherCatProjectedSlave("TIID^D^CX", "Box 0 (EK1200-5000)", null, null, null, null, Passive: true),
            new EtherCatProjectedSlave("TIID^D^A", "A", 2, 11, 1, null),
            new EtherCatProjectedSlave("TIID^D^B", "B", 2, 12, 1, null),
            new EtherCatProjectedSlave("TIID^D^End", "Term 9 (EL9011)", null, null, null, null, Passive: true),
        };

        var r = new EtherCatIdentityReader((_, _) => master).Read(
            new EtherCatMasterConfig("TIID^D", "D", "10.20.30.40.2.1", 0xFFFF, "test", null, boxes), null, null, false);

        Assert.Equal(["A", "B"], r.Slaves.Where(s => s.Online is not null).Select(s => s.Name).ToArray());
        var passive = r.Slaves.Where(s => s.Projected is { Passive: true }).ToList();
        Assert.Equal(2, passive.Count);
        Assert.All(passive, s => { Assert.Null(s.Online); Assert.Null(s.JoinedBy); });
        Assert.DoesNotContain(r.Warnings, w => w.Contains("unreliable", StringComparison.Ordinal));
    }

    // ---- AX5000 serial ------------------------------------------------------------------------------

    private static EtherCatMasterIdentities ReadSlaves(Dictionary<int, FakeDeviceSlave> slaves) => ReadMaster(slaves);

    private static EtherCatMasterIdentities ReadMaster(Dictionary<int, FakeDeviceSlave> slaves)
    {
        var master = new FakeEtherCatMaster().WithSlaves(slaves.Keys.Order().Select(a => (a, (byte)0x08, (byte)0x00, 2u, (uint)a, 1u, 0u)).ToArray());
        var boxes = slaves.Keys.Order().Select(a => new EtherCatProjectedSlave($"TIID^D^B{a}", $"Box {a}", 2, a, 1, a)).ToArray();
        var reader = new EtherCatIdentityReader((_, port) => port == 0xFFFF ? master : slaves[port]);
        return reader.Read(new EtherCatMasterConfig("TIID^D", "D", "10.20.30.40.2.1", 0xFFFF, "test", null, boxes), null, null, false, true, true);
    }

    [Fact]
    public void Ax5000_SoeSerial_FillsTheSlaveSerial_WhenNoOtherSerialExists()
    {
        var drive = new FakeDeviceSlave("UnknownAmsCommand").Channel(0, "000001111", 0, [0, 0, 0, 0]).Channel(1, "000001111", 0, [0, 0, 0, 0]);
        var slave = ReadSlaves(new() { [1001] = drive }).Slaves.Single();
        Assert.Equal("000001111", slave.SerialNo);
        Assert.Equal("soe:S-0-0432", slave.SerialSource);
        Assert.True(slave.SerialAvailable);
    }

    [Fact]
    public void Ax5000_CoeSerial_StillWinsOverSoe()
    {
        var drive = new FakeDeviceSlave().Terminal("AX5103-0000", 4242).Channel(0, "000001111", 0, [0, 0, 0, 0]);
        var slave = ReadSlaves(new() { [1001] = drive }).Slaves.Single();
        Assert.Equal("4242", slave.SerialNo);
        Assert.Equal(EtherCatSerialSource.Coe, slave.SerialSource);
    }

    // ---- safety wording ---------------------------------------------------------------------------------

    [Fact]
    public void Safety_El6695Bridge_IsABlackChannel_NotASafetyDevice()
    {
        var slave = ReadSlaves(new() { [1001] = new FakeDeviceSlave().Terminal("EL6695-1001", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0) }).Slaves.Single();
        var safety = slave.Details!.Safety!;
        Assert.False(safety.IsSafetyDevice);
        Assert.Equal("blackChannel", safety.Role);
        Assert.Equal("F980", safety.Detection);
        Assert.Equal("passes FSoE data through (black channel); no safety serial required", safety.Note);
        Assert.False(slave.SafetySerialMissing);
    }

    [Fact]
    public void Safety_Pattern_WithZeroF980_StaysASafetyDevice_AndIsFlagged()
    {
        var slave = ReadSlaves(new() { [1001] = new FakeDeviceSlave().Terminal("EL6910", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0) }).Slaves.Single();
        Assert.True(slave.Details!.Safety!.IsSafetyDevice);
        Assert.Equal("article pattern", slave.Details.Safety.Detection);
        Assert.Contains("verify", slave.Details.Safety.Note, StringComparison.Ordinal);
        Assert.True(slave.SafetySerialMissing);
    }

    [Fact]
    public void Safety_NonZeroFsoeAddressAlone_IsASafetyDeviceByF980()
    {
        var slave = ReadSlaves(new() { [1001] = new FakeDeviceSlave().Terminal("Custom", 0).CoeU16(0xF980, 1, 7).CoeU32(0xF980, 2, 0) }).Slaves.Single();
        Assert.True(slave.Details!.Safety!.IsSafetyDevice);
        Assert.Equal("F980", slave.Details.Safety.Detection);
        Assert.Null(slave.Details.Safety.Role);
        Assert.True(slave.SafetySerialMissing);
    }

    [Fact]
    public void Safety_BridgeIsNotCountedAsASafetyDevice()
    {
        var slaves = new Dictionary<int, FakeDeviceSlave>
        {
            [1001] = new FakeDeviceSlave().Terminal("EL6695-1001", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0),
            [1002] = new FakeDeviceSlave().Terminal("EL1918", 0).CoeU16(0xF980, 1, 3).CoeU32(0xF980, 2, 555),
        };
        var result = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [ReadMaster(slaves)]);
        Assert.Equal(1, result.Summary.SafetyDevices);
        Assert.Equal(1, result.Summary.SafetyWithSerial);
        Assert.True(result.SafetyIdentityComplete);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Safety_NameOnly_IsDetectedByArticlePattern_WithoutNote()
    {
        var slave = ReadSlaves(new() { [1001] = new FakeDeviceSlave().Terminal("EL1904", 0) }).Slaves.Single();
        Assert.Equal("article pattern", slave.Details!.Safety!.Detection);
        Assert.Null(slave.Details.Safety.Note);
    }

    [Fact]
    public void Safety_WithSerial_HasNoNote()
    {
        var slave = ReadSlaves(new() { [1001] = new FakeDeviceSlave().Terminal("EL1918", 0).CoeU16(0xF980, 1, 5).CoeU32(0xF980, 2, 99) }).Slaves.Single();
        Assert.Equal("F980", slave.Details!.Safety!.Detection);
        Assert.Null(slave.Details.Safety.Note);
    }

    // ---- masters that were not read ---------------------------------------------------------------------------

    [Fact]
    public void UnreadMaster_MakesSafetyIdentityIncomplete_WithAWarning()
    {
        var unreachable = new EtherCatIdentityReader((_, _) => new FakeEtherCatMaster()).Read(
            new EtherCatMasterConfig("TIID^D1", "D1", "10.20.30.40.2.1", 0xFFFF, "test", null, []), null, null, false);
        var noAddress = new EtherCatIdentityReader((_, _) => new FakeEtherCatMaster()).Read(
            new EtherCatMasterConfig("TIID^D2", "D2", null, 0xFFFF, "test", "<x/>", []), null, null, false);

        Assert.False(unreachable.SafetyIdentityComplete);
        Assert.False(noAddress.SafetyIdentityComplete);
        Assert.Contains(unreachable.Warnings, w => w.Contains("Safety identity could not be checked", StringComparison.Ordinal));
        Assert.Contains(noAddress.Warnings, w => w.Contains("D2", StringComparison.Ordinal));

        var result = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [unreachable]);
        Assert.False(result.SafetyIdentityComplete);
    }

    [Fact]
    public void ReadMasterWithoutSafetyDevices_IsComplete()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 0));
        var r = new EtherCatIdentityReader((_, _) => master).Read(
            new EtherCatMasterConfig("TIID^D", "D", "10.20.30.40.2.1", 0xFFFF, "test", null, []), null, null, false);
        Assert.True(r.SafetyIdentityComplete);
    }
}
