using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Synthetic MDP device: values are keyed by IndexOffset, built from the documented layouts.</summary>
internal sealed class FakeMdpDevice : IAdsRawReader
{
    private readonly Dictionary<uint, byte[]> _values = new();
    private int _moduleCount;
    public uint? FailAllWith { get; set; }
    public Exception? ThrowOnRead { get; set; }
    public int ReadCount { get; private set; }
    public HashSet<uint> Offsets { get; } = new();

    public static byte[] B(params byte[] b) => b;
    public static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); return b; }
    public static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    public static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
    public static byte[] Str(string s) => Encoding.Latin1.GetBytes(s + "\0");

    public void Set(int tableIndex, int sub, byte[] data) => _values[MdpAddress.IndexOffset(tableIndex, sub)] = data;

    public FakeMdpDevice WithDevice(string btn, string bic) { Set(0xF9F0, 0, Str(btn)); Set(0xF9F0, 1, Str(bic)); return this; }

    public FakeMdpDevice AddModule(int type, int id, string name)
    {
        int n = ++_moduleCount;
        Set(0xF020, 0, U16(n));
        Set(0xF020, n, U32(((uint)type << 16) | (uint)id));
        Set(0xF060, n, Str(name));
        return this;
    }

    public FakeMdpDevice Table(int moduleId, int tableId, params byte[][] entries)
    {
        int t = MdpAddress.ConfigTable(moduleId, tableId);
        Set(t, 0, U16(entries.Length));
        for (int i = 0; i < entries.Length; i++) Set(t, i + 1, entries[i]);
        return this;
    }

    public FakeMdpDevice AddDisks(int id, params (string Letter, string Label, string Fs, uint Type, ulong Total, ulong Free)[] disks)
    {
        AddModule(MdpLayoutCatalog.TypeDiskManagement, id, "DiskManagement");
        Table(id, 1, disks.Select(d => Str(d.Letter)).ToArray());
        Table(id, 2, disks.Select(d => Str(d.Label)).ToArray());
        Table(id, 3, disks.Select(d => Str(d.Fs)).ToArray());
        Table(id, 4, disks.Select(d => U32(d.Type)).ToArray());
        Table(id, 5, disks.Select(d => U64(d.Total)).ToArray());
        Table(id, 6, disks.Select(d => U64(d.Free)).ToArray());
        return this;
    }

    public FakeMdpDevice AddNic(int id, string mac, string ip, bool dhcp, byte link)
    {
        AddModule(MdpLayoutCatalog.TypeNic, id, "Ethernet " + id);
        Table(id, 1, Str(mac), Str(ip), Str("255.255.255.0"), B(dhcp ? (byte)1 : (byte)0), Str("10.0.0.254"),
            Str("10.0.0.2"), Str("vnic" + id), Str(""), B(link));
        return this;
    }

    public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength)
    {
        ReadCount++;
        Offsets.Add(indexOffset);
        if (ThrowOnRead is not null) throw ThrowOnRead;
        Assert.Equal(MdpAddress.IndexGroup, indexGroup);
        if (FailAllWith is { } code) return AdsRawReadResult.Failure(code, "Err" + code);
        return _values.TryGetValue(indexOffset, out var data)
            ? AdsRawReadResult.Success(data.Length > maxLength ? data[..maxLength] : data)
            : AdsRawReadResult.Failure(1809, "DeviceSymbolNotFound");
    }
}

public class MdpInventoryTests
{
    private const string NetId = "10.0.0.1.1.1";

    private static IpcInventoryResult Run(FakeMdpDevice dev, IReadOnlySet<int>? filter = null, bool raw = true, MdpReadLimits? limits = null) =>
        new MdpInventoryReader(dev, limits).Read(NetId, filter, raw);

    private static FakeMdpDevice Machine() => new FakeMdpDevice().WithDevice("BTN123", "BIC456");

    private static MdpTable TableOf(IpcInventoryResult r, int moduleIndex, int moduleId, int tableId) =>
        r.Modules[moduleIndex].Tables.Single(t => t.TableIndex == MdpAddress.ConfigTable(moduleId, tableId));

    [Fact]
    public void IndexOffset_ComposesTableAndSubIndex()
    {
        Assert.Equal(0xF0200000u + 3, MdpAddress.IndexOffset(0xF020, 3));
        Assert.Equal((5u << 20) | 0x80010001u, MdpAddress.IndexOffset(MdpAddress.ConfigTable(5, 1), 1));
        Assert.Equal(0x8010, MdpAddress.ConfigTable(1, 0));
    }

    [Fact]
    public void Decoder_UnsignedSignedBoolAndStrings()
    {
        Assert.True(MdpValueDecoder.TryDecode("U16", FakeMdpDevice.B(0x34, 0x12), out var u16)); Assert.Equal(0x1234u, u16);
        Assert.True(MdpValueDecoder.TryDecode("U8", FakeMdpDevice.B(7), out var u8)); Assert.Equal(7u, u8);
        Assert.True(MdpValueDecoder.TryDecode("U32", FakeMdpDevice.U32(1), out var u32)); Assert.Equal(1u, u32);
        Assert.True(MdpValueDecoder.TryDecode("S16", FakeMdpDevice.B(0xFF, 0xFF), out var s16)); Assert.Equal(-1, s16);
        Assert.True(MdpValueDecoder.TryDecode("S32", FakeMdpDevice.B(0xFE, 0xFF, 0xFF, 0xFF), out var s32)); Assert.Equal(-2, s32);
        Assert.True(MdpValueDecoder.TryDecode("BOOL", FakeMdpDevice.B(1), out var b)); Assert.Equal(true, b);
        Assert.True(MdpValueDecoder.TryDecode("U64", FakeMdpDevice.U64(1UL << 40), out var u64)); Assert.Equal(1UL << 40, u64);
        Assert.Equal("abc", MdpValueDecoder.Text(Encoding.ASCII.GetBytes("abc\0junk")));
        Assert.Equal("", MdpValueDecoder.Text([]));
        Assert.Equal("", MdpValueDecoder.Text(FakeMdpDevice.B(0)));
        Assert.Equal("abc", MdpValueDecoder.Text(Encoding.ASCII.GetBytes("abc")));
        Assert.False(MdpValueDecoder.TryDecode("U32", Encoding.ASCII.GetBytes("Not Supported"), out _));
    }

    [Fact]
    public void Read_DeviceAndModuleList_AreDecoded()
    {
        var r = Run(Machine().AddModule(0x0008, 3, "TwinCAT"));
        Assert.Equal(IpcInventoryStatus.Ok, r.Status);
        Assert.Equal("BTN123", r.Device!.Btn);
        Assert.Equal("BIC456", r.Device.Bic);
        var m = Assert.Single(r.Modules);
        Assert.Equal(3, m.ModuleId);
        Assert.Equal(0x0008, m.ModuleType);
        Assert.Equal("TwinCAT", m.TypeName);
        Assert.Equal("TwinCAT", m.Name);
    }

    [Fact]
    public void TwinCat_Table1_DecodesVersionNetIdAndSystemId()
    {
        var dev = Machine().AddModule(0x0008, 1, "TwinCAT")
            .Table(1, 1, FakeMdpDevice.U16(3), FakeMdpDevice.U16(1), FakeMdpDevice.U16(4026), FakeMdpDevice.Str("10.0.0.1.1.1"),
                FakeMdpDevice.U32(0), FakeMdpDevice.U16(5), FakeMdpDevice.U16(0), FakeMdpDevice.U16(0), FakeMdpDevice.U32(0),
                FakeMdpDevice.Str(""), FakeMdpDevice.Str("AAAA-BBBB"), FakeMdpDevice.U16(22), FakeMdpDevice.U64(99));
        var d = Run(dev).Modules.Single().Decoded!;
        Assert.Equal("3.1.4026.22", d["version"]);
        Assert.Equal("10.0.0.1.1.1", d["amsNetId"]);
        Assert.Equal("AAAA-BBBB", d["systemId"]);
        Assert.Equal(22u, d["revision"]);
    }

    [Fact]
    public void TwinCat_LogfileTable_IsNeverRead()
    {
        var dev = Machine().AddModule(0x0008, 1, "TwinCAT");
        dev.Set(MdpAddress.ConfigTable(1, 8), 0, Encoding.ASCII.GetBytes("log text that is no length"));
        var r = Run(dev);
        Assert.DoesNotContain(MdpAddress.IndexOffset(MdpAddress.ConfigTable(1, 8), 0), dev.Offsets);
        Assert.Contains(r.Warnings, w => w.Contains("not read"));
    }

    [Fact]
    public void Disks_OneVersusTwoInstancesAndRowZipping()
    {
        var one = Machine().AddDisks(4, ("C:", "OS", "NTFS", 1, 1000, 400));
        var two = Machine()
            .AddDisks(4, ("C:", "OS", "NTFS", 1, 1000, 400))
            .AddDisks(5, ("D:", "Data", "FAT32", 2, 2000, 1500));

        var rows1 = (List<Dictionary<string, object?>>)Run(one).Modules.Single().Decoded!["disks"]!;
        Assert.Single(rows1);
        Assert.Equal("C:", rows1[0]["driveLetter"]);
        Assert.Equal(1000UL, rows1[0]["totalBytes"]);

        var r2 = Run(two);
        Assert.Equal(2, r2.Modules.Count);
        var rows = (List<Dictionary<string, object?>>)r2.Modules[1].Decoded!["disks"]!;
        Assert.Equal("D:", rows[0]["driveLetter"]);
        Assert.Equal("FAT32", rows[0]["fileSystem"]);
        Assert.Equal(1500UL, rows[0]["freeBytes"]);
        Assert.Empty(r2.Warnings);
    }

    [Fact]
    public void Disks_UnequalLen_PadsWithNullAndWarns()
    {
        var dev = Machine().AddModule(0x001D, 4, "DiskManagement");
        dev.Table(4, 1, FakeMdpDevice.Str("C:"), FakeMdpDevice.Str("D:"));
        dev.Table(4, 2, FakeMdpDevice.Str("OS"));
        var r = Run(dev);
        var rows = (List<Dictionary<string, object?>>)r.Modules.Single().Decoded!["disks"]!;
        Assert.Equal(2, rows.Count);
        Assert.Equal("OS", rows[0]["label"]);
        Assert.Null(rows[1]["label"]);
        Assert.Null(rows[1]["totalBytes"]);
        Assert.Contains(r.Warnings, w => w.Contains("column lengths differ"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Nics_AreEnumeratedPerInstance(int nics)
    {
        var dev = Machine();
        for (int i = 0; i < nics; i++) dev.AddNic(10 + i, $"00:01:05:00:00:0{i}", $"10.0.{i}.5", i == 0, (byte)(i % 2));
        var r = Run(dev);
        Assert.Equal(nics, r.Modules.Count(m => m.TypeName == "NIC"));
        var last = r.Modules[^1].Decoded!;
        Assert.Equal($"10.0.{nics - 1}.5", last["ipv4Address"]);
        Assert.Equal(nics == 1, last["dhcp"]);
        Assert.Equal((uint)((nics - 1) % 2), last["linkState"]);
    }

    [Fact]
    public void Software_ZeroEntries_IsEmptyList_AndTwoEntriesZip()
    {
        var empty = Machine().AddModule(0x000A, 2, "Software").Table(2, 1).Table(2, 2).Table(2, 3).Table(2, 4);
        Assert.Empty((List<Dictionary<string, object?>>)Run(empty).Modules.Single().Decoded!["software"]!);

        var dev = Machine().AddModule(0x000A, 2, "Software")
            .Table(2, 1, FakeMdpDevice.Str("TwinCAT XAR"), FakeMdpDevice.Str("TwinCAT PLC"))
            .Table(2, 2, FakeMdpDevice.Str("Vendor"), FakeMdpDevice.Str("Vendor"))
            .Table(2, 3, FakeMdpDevice.Str("2026-01-01"), FakeMdpDevice.Str("2026-01-02"))
            .Table(2, 4, FakeMdpDevice.Str("3.1.4026.22"), FakeMdpDevice.Str("3.1.4026.23"));
        var rows = (List<Dictionary<string, object?>>)Run(dev).Modules.Single().Decoded!["software"]!;
        Assert.Equal(2, rows.Count);
        Assert.Equal("3.1.4026.23", rows[1]["version"]);
        Assert.Equal("TwinCAT XAR", rows[0]["name"]);
    }

    [Fact]
    public void OsMainboardCpuMemory_Decode()
    {
        var dev = Machine()
            .AddModule(0x0018, 6, "OS")
            .Table(6, 1, FakeMdpDevice.U32(10), FakeMdpDevice.U32(0), FakeMdpDevice.U32(19045), FakeMdpDevice.Str(""), FakeMdpDevice.Str("10.0.19045"))
            .AddModule(0x001C, 7, "Mainboard")
            .Table(7, 1, FakeMdpDevice.Str("CB1234"), FakeMdpDevice.Str("MB-SERIAL"), FakeMdpDevice.Str("2024-05-01"))
            .Table(7, 2, FakeMdpDevice.B(3), FakeMdpDevice.B(1), FakeMdpDevice.B(2), FakeMdpDevice.Str("BIOS 1.2"))
            .AddModule(0x000B, 8, "CPU0")
            .Table(8, 1, FakeMdpDevice.U32(2400), FakeMdpDevice.U16(12), FakeMdpDevice.B(0xE2, 0xFF))
            .AddModule(0x000C, 9, "Memory")
            .Table(9, 1, Encoding.ASCII.GetBytes("Not Supported\0"), Encoding.ASCII.GetBytes("Not Supported\0"),
                FakeMdpDevice.U32(0), FakeMdpDevice.U32(0), FakeMdpDevice.U32(0), FakeMdpDevice.U64(8UL << 30), FakeMdpDevice.U64(5UL << 30));
        var r = Run(dev);
        Assert.Equal("10.0.19045", r.Modules[0].Decoded!["version"]);
        Assert.Equal("MB-SERIAL", r.Modules[1].Decoded!["serialNumber"]);
        Assert.Equal("BIOS 1.2", r.Modules[1].Decoded!["biosVersion"]);
        Assert.Equal(2400u, r.Modules[2].Decoded!["frequency"]);
        Assert.Equal(-30, r.Modules[2].Decoded!["temperatureCelsius"]);
        Assert.Equal(8UL << 30, r.Modules[3].Decoded!["programMemoryAllocated"]);
        Assert.False(r.Modules[3].Decoded!.ContainsKey("programMemoryAllocated32"));
    }

    [Fact]
    public void UnknownModuleType_KeepsRawAndHasNoDecodedView()
    {
        var dev = Machine().AddModule(0x7777, 9, "Mystery").Table(9, 1, FakeMdpDevice.B(1, 2, 3));
        var r = Run(dev, raw: false);
        var m = r.Modules.Single();
        Assert.Equal("unknown", m.TypeName);
        Assert.Null(m.Decoded);
        var entry = TableOf(r, 0, 9, 1).Entries.Single(e => e.SubIndex == 1);
        Assert.Null(entry.Value);
        Assert.Equal("010203", entry.Raw);
    }

    [Fact]
    public void MissingTable_IsSimplyAbsent_AndDecodeStillWorks()
    {
        var m = Run(Machine().AddModule(0x000B, 8, "CPU0")).Modules.Single();
        Assert.Empty(m.Tables);
        Assert.Empty(m.Decoded!);
    }

    [Fact]
    public void PerEntryAdsError_IsRecordedInPlace()
    {
        var dev = Machine().AddModule(0x000B, 8, "CPU0");
        int t = MdpAddress.ConfigTable(8, 1);
        dev.Set(t, 0, FakeMdpDevice.U16(3));
        dev.Set(t, 1, FakeMdpDevice.U32(2400));
        dev.Set(t, 3, FakeMdpDevice.B(0, 0));
        var entries = TableOf(Run(dev), 0, 8, 1).Entries;
        Assert.Contains("DeviceSymbolNotFound", entries.Single(e => e.SubIndex == 2).AdsError);
        Assert.Equal(2400u, entries.Single(e => e.SubIndex == 1).Value);
    }

    [Fact]
    public void ModuleTypeFilter_ReadsOnlyMatchingModules()
    {
        var dev = Machine().AddModule(0x000B, 8, "CPU0").AddModule(0x000C, 9, "Memory");
        var r = Run(dev, new HashSet<int> { 0x000C });
        Assert.Equal(0x000C, r.Modules.Single().ModuleType);
    }

    [Fact]
    public void RawIsKeptForDecodedValuesOnlyWhenRequested()
    {
        var dev = Machine().AddModule(0x000B, 8, "CPU0").Table(8, 1, FakeMdpDevice.U32(2400));
        Assert.Equal("60090000", TableOf(Run(dev, raw: true), 0, 8, 1).Entries[1].Raw);
        Assert.Null(TableOf(Run(dev, raw: false), 0, 8, 1).Entries[1].Raw);
    }

    [Fact]
    public void MdpUnavailable_WhenPortAnswersButIndexGroupFails()
    {
        var r = Run(new FakeMdpDevice { FailAllWith = 1792 });
        Assert.Equal(IpcInventoryStatus.MdpUnavailable, r.Status);
        Assert.Equal(1792u, r.AdsErrorCode);
        Assert.False(r.Success);
    }

    [Fact]
    public void TargetUnreachable_OnTimeoutAndOnConnectException()
    {
        Assert.Equal(IpcInventoryStatus.TargetUnreachable, Run(new FakeMdpDevice { FailAllWith = 1861 }).Status);
        var r = Run(new FakeMdpDevice { ThrowOnRead = new InvalidOperationException("no route") });
        Assert.Equal(IpcInventoryStatus.TargetUnreachable, r.Status);
        Assert.Contains("no route", r.Message);
    }

    [Fact]
    public void MdpNotReady_WhenModuleCountIsZero()
    {
        var dev = Machine();
        dev.Set(0xF020, 0, FakeMdpDevice.U16(0));
        Assert.Equal(IpcInventoryStatus.MdpNotReady, Run(dev).Status);
    }

    [Fact]
    public void ReadBudget_CutsOffAndReturnsPartialResult()
    {
        var dev = Machine().AddModule(0x000B, 8, "CPU0").Table(8, 1, FakeMdpDevice.U32(1), FakeMdpDevice.U16(1), FakeMdpDevice.B(0, 0));
        var r = Run(dev, limits: new MdpReadLimits(MaxReads: 6));
        Assert.Equal(IpcInventoryStatus.Ok, r.Status);
        Assert.Contains(r.Warnings, w => w.Contains("budget"));
        Assert.True(dev.ReadCount <= 7);
    }

    [Fact]
    public void LenAboveCap_IsCappedWithWarning()
    {
        var dev = Machine().AddModule(0x000A, 2, "Software");
        dev.Set(MdpAddress.ConfigTable(2, 1), 0, FakeMdpDevice.U16(9999));
        var r = Run(dev, limits: new MdpReadLimits(MaxLen: 3));
        Assert.Contains(r.Warnings, w => w.Contains("exceeds the limit"));
        Assert.Equal(4, r.Modules.Single().Tables.Single().Entries.Count);
    }

    [Fact]
    public void MissingBtn_IsAWarningNotAFailure()
    {
        var r = Run(new FakeMdpDevice().AddModule(0x000B, 8, "CPU0"));
        Assert.Equal(IpcInventoryStatus.Ok, r.Status);
        Assert.Null(r.Device!.Btn);
        Assert.Contains(r.Warnings, w => w.StartsWith("BTN", StringComparison.Ordinal));
    }

    // ---- coordinator ---------------------------------------------------------

    [Fact]
    public void Coordinator_BadModuleType_IsAnError_AndDoesNotCallTheSession()
    {
        var fake = new FakeTwinCatSession();
        string r = new IpcInventoryCoordinator(fake).ReadInventory(null, ["0xZZ"], true);
        Assert.StartsWith("[Error]", r, StringComparison.Ordinal);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Coordinator_PassesFilterAndNetId_AndShapesJson()
    {
        var fake = new FakeTwinCatSession();
        string json = new IpcInventoryCoordinator(fake).ReadInventory(" 1.2.3.4.1.1 ", ["0x0008", "10"], false);
        Assert.Equal("ReadIpcInventory(1.2.3.4.1.1,8|10,False,5000)", fake.CallLog.Single());
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("0x0000F302", doc.RootElement.GetProperty("indexGroup").GetString());
    }

    [Fact]
    public void Coordinator_UnavailableTarget_IsStructuredNotAnError()
    {
        var fake = new FakeTwinCatSession
        {
            FakeIpcInventoryResult = new IpcInventoryResult(IpcInventoryStatus.MdpUnavailable, NetId, 10000,
                DateTimeOffset.UnixEpoch, "no device manager", 1792, "DeviceServiceNotSupported", null, [], []),
        };
        string json = new IpcInventoryCoordinator(fake).ReadInventory(NetId, null, true);
        Assert.False(McpErrors.IsError(json));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("mdpUnavailable", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1792, doc.RootElement.GetProperty("adsError").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Coordinator_SessionException_BecomesError()
    {
        var fake = new FakeTwinCatSession { ReadIpcInventoryThrows = new InvalidOperationException("no TargetNetId") };
        Assert.StartsWith("[Error]", new IpcInventoryCoordinator(fake).ReadInventory(null, null, true), StringComparison.Ordinal);
    }
}
