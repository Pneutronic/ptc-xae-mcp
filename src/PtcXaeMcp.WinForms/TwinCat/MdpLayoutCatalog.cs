using System.Buffers.Binary;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed record MdpField(string Name, string Type);

/// <summary>A table of scalar fields (<see cref="Fields"/>, subindex 1..n) or of one repeated element (<see cref="Element"/>).</summary>
internal sealed record MdpTableLayout(string Name, IReadOnlyList<MdpField>? Fields, MdpField? Element, bool Skip = false)
{
    public MdpField? FieldAt(int subIndex)
    {
        if (Element is not null) return subIndex >= 1 ? Element : null;
        return Fields is not null && subIndex >= 1 && subIndex <= Fields.Count ? Fields[subIndex - 1] : null;
    }
}

/// <summary>Parallel-array tables that form the columns of one row set.</summary>
internal sealed record MdpRowGroup(string Name, IReadOnlyList<(int Table, string Column)> Columns);

/// <summary>Module types and table layouts of the inventory-relevant MDP modules (Beckhoff Device Manager documentation).</summary>
internal static class MdpLayoutCatalog
{
    public const int TypeNic = 0x0002;
    public const int TypeTwinCat = 0x0008;
    public const int TypeSoftware = 0x000A;
    public const int TypeCpu = 0x000B;
    public const int TypeMemory = 0x000C;
    public const int TypeOperatingSystem = 0x0018;
    public const int TypeMainboard = 0x001C;
    public const int TypeDiskManagement = 0x001D;

    private static readonly Dictionary<int, string> TypeNames = new()
    {
        [TypeNic] = "NIC", [TypeTwinCat] = "TwinCAT", [TypeSoftware] = "Software", [TypeCpu] = "CPU",
        [TypeMemory] = "Memory", [TypeOperatingSystem] = "OperatingSystem", [TypeMainboard] = "Mainboard",
        [TypeDiskManagement] = "DiskManagement",
    };

    // Known types whose tables have no layout here: named, but their tables stay raw.
    private static readonly Dictionary<int, string> NamedOnlyTypes = new() { [0x0013] = "Display", [0x001B] = "Fan" };

    public static string TypeName(int moduleType) =>
        TypeNames.TryGetValue(moduleType, out var n) ? n : NamedOnlyTypes.TryGetValue(moduleType, out var named) ? named : "unknown";

    public static bool IsKnown(int moduleType) => TypeNames.ContainsKey(moduleType);

    private static MdpField F(string name, string type) => new(name, type);

    private static MdpTableLayout Scalars(string name, params MdpField[] fields) => new(name, fields, null);

    private static MdpTableLayout ArrayOf(string name, string element, string type) => new(name, null, F(element, type));

    private static MdpField[] Pins(string prefix) => Enumerable.Range(0, 8).Select(i => F($"{prefix}Pin{i}", "U8")).ToArray();

    /// <summary>Module header (table 0), identical for every module type.</summary>
    public static readonly MdpTableLayout Header = Scalars("Module header",
        F("address", "U32"), F("type", "STRING"), F("name", "STRING"), F("devType", "U32"));

    private static readonly Dictionary<(int Type, int Table), MdpTableLayout> Layouts = new()
    {
        [(TypeTwinCat, 1)] = Scalars("TcMisc",
            F("majorVersion", "U16"), F("minorVersion", "U16"), F("buildVersion", "U16"), F("amsNetId", "STRING"),
            F("registerLevel", "U32"), F("status", "U16"), F("runAsDevice", "U16"), F("showTargetVisu", "U16"),
            F("logFileSize", "U32"), F("logFilePath", "STRING"), F("systemId", "STRING"), F("revision", "U16"),
            F("secondsSinceStatusChange", "U64")),
        [(TypeTwinCat, 2)] = ArrayOf("RouteName", "routeName", "STRING"),
        [(TypeTwinCat, 3)] = ArrayOf("RouteAddress", "routeAddress", "STRING"),
        [(TypeTwinCat, 4)] = ArrayOf("RouteAmsAddress", "routeAmsAddress", "STRING"),
        [(TypeTwinCat, 5)] = ArrayOf("RouteFlags", "routeFlags", "U32"),
        [(TypeTwinCat, 6)] = ArrayOf("RouteTimeout", "routeTimeout", "U32"),
        [(TypeTwinCat, 7)] = ArrayOf("RouteTransport", "routeTransport", "U16"),
        // The logfile table has no Len at subindex 0 (the subindex 0 value is the whole log text): never read it.
        [(TypeTwinCat, 8)] = new MdpTableLayout("Logfile", null, null, Skip: true),
        [(TypeTwinCat, 9)] = Scalars("RouterInformation",
            F("routerMemoryMax", "U64"), F("routerMemoryAvailable", "U64"), F("registeredPorts", "U32"),
            F("registeredDrivers", "U32"), F("registeredTransports", "U32"), F("debugWindow", "BOOL"),
            F("mailboxSize", "U32"), F("mailboxUsedEntries", "U32")),
        [(TypeTwinCat, 10)] = Scalars("TcOsExtended", F("heapMemoryMax", "U64"), F("heapMemoryAvailable", "U64")),

        [(TypeSoftware, 1)] = ArrayOf("Name", "name", "STRING"),
        [(TypeSoftware, 2)] = ArrayOf("Company", "company", "STRING"),
        [(TypeSoftware, 3)] = ArrayOf("Date", "date", "STRING"),
        [(TypeSoftware, 4)] = ArrayOf("Version", "version", "STRING"),

        [(TypeOperatingSystem, 1)] = Scalars("OsVersion",
            F("majorVersion", "U32"), F("minorVersion", "U32"), F("buildNumber", "U32"), F("servicePack", "STRING"),
            F("version", "STRING")),
        [(TypeOperatingSystem, 2)] = Scalars("Uptime", F("uptimeSeconds", "U64")),

        [(TypeMainboard, 1)] = Scalars("MainboardInformation",
            F("mainboardType", "STRING"), F("serialNumber", "STRING"), F("productionDate", "STRING"),
            F("bootCount", "U32"), F("operatingTimeMinutes", "U32"), F("minBoardTemperature", "S32"),
            F("maxBoardTemperature", "S32"), F("minInputVoltageMv", "S32"), F("maxInputVoltageMv", "S32"),
            F("mainboardTemperature", "S16")),
        [(TypeMainboard, 2)] = Scalars("VersionInformation",
            F("mainboardRevision", "U8"), F("biosMajorVersion", "U8"), F("biosMinorVersion", "U8"), F("biosVersion", "STRING")),
        [(TypeMainboard, 3)] = ArrayOf("VoltageName", "voltageName", "STRING"),
        [(TypeMainboard, 4)] = ArrayOf("VoltageLocation", "voltageLocation", "S16"),
        [(TypeMainboard, 5)] = ArrayOf("Voltage", "voltageMv", "S16"),
        [(TypeMainboard, 6)] = ArrayOf("NominalVoltage", "nominalVoltageMv", "S16"),
        [(TypeMainboard, 7)] = Scalars("Gpio1State", Pins("gpio1State")),
        [(TypeMainboard, 8)] = Scalars("Gpio1InOut", Pins("gpio1InOut")),

        [(TypeDiskManagement, 1)] = ArrayOf("DriveLetter", "driveLetter", "STRING"),
        [(TypeDiskManagement, 2)] = ArrayOf("VolumeLabel", "label", "STRING"),
        [(TypeDiskManagement, 3)] = ArrayOf("FileSystem", "fileSystem", "STRING"),
        [(TypeDiskManagement, 4)] = ArrayOf("DriveType", "driveType", "U32"),
        [(TypeDiskManagement, 5)] = ArrayOf("TotalSize", "totalBytes", "U64"),
        [(TypeDiskManagement, 6)] = ArrayOf("FreeSpace", "freeBytes", "U64"),

        [(TypeNic, 1)] = Scalars("NicProperties",
            F("macAddress", "STRING"), F("ipv4Address", "STRING"), F("ipv4SubnetMask", "STRING"), F("dhcp", "BOOL"),
            F("ipv4DefaultGateway", "STRING"), F("ipv4DnsServers", "STRING"), F("virtualDeviceName", "STRING"),
            F("ipv4DnsServersActive", "STRING"), F("linkState", "U8")),

        [(TypeCpu, 1)] = Scalars("CpuProperties",
            F("frequency", "U32"), F("usagePercent", "U16"), F("temperatureCelsius", "S16")),

        [(TypeMemory, 1)] = Scalars("MemoryProperties",
            F("programMemoryAllocated32", "U32"), F("programMemoryAvailable32", "U32"),
            F("storageMemoryAllocated", "U32"), F("storageMemoryAvailable", "U32"), F("memoryDivision", "U32"),
            F("programMemoryAllocated", "U64"), F("programMemoryAvailable", "U64")),
    };

    private static readonly Dictionary<int, MdpRowGroup[]> RowGroups = new()
    {
        [TypeSoftware] = [new("software", [(1, "name"), (2, "company"), (3, "date"), (4, "version")])],
        [TypeDiskManagement] = [new("disks", [(1, "driveLetter"), (2, "label"), (3, "fileSystem"), (4, "driveType"), (5, "totalBytes"), (6, "freeBytes")])],
        [TypeTwinCat] = [new("routes", [(2, "name"), (3, "address"), (4, "amsNetId"), (5, "flags"), (6, "timeout"), (7, "transport")])],
        [TypeMainboard] = [new("voltages", [(3, "name"), (4, "location"), (5, "voltageMv"), (6, "nominalVoltageMv")])],
    };

    /// <summary>Layout of a table (0 = header); null when the module type or table is not in the catalog.</summary>
    public static MdpTableLayout? Find(int moduleType, int tableId)
    {
        if (tableId == 0) return Header;
        return Layouts.TryGetValue((moduleType, tableId), out var l) ? l : null;
    }

    public static IReadOnlyList<MdpRowGroup> GroupsOf(int moduleType) => RowGroups.TryGetValue(moduleType, out var g) ? g : [];
}

/// <summary>Decoding of raw MDP buffers into typed values.</summary>
internal static class MdpValueDecoder
{
    /// <summary>
    /// Decodes <paramref name="data"/> as <paramref name="type"/> (U8 U16 U32 U64 S16 S32 BOOL STRING). Numbers require the
    /// exact byte count so that a text answer such as "Not Supported" is not misread as a number.
    /// </summary>
    public static bool TryDecode(string type, ReadOnlySpan<byte> data, out object? value)
    {
        value = null;
        switch (type)
        {
            case "STRING": value = Text(data); return true;
            case "U8" when data.Length == 1: value = (uint)data[0]; return true;
            case "BOOL" when data.Length == 1: value = data[0] != 0; return true;
            case "U16" when data.Length == 2: value = (uint)BinaryPrimitives.ReadUInt16LittleEndian(data); return true;
            case "S16" when data.Length == 2: value = (int)BinaryPrimitives.ReadInt16LittleEndian(data); return true;
            case "U32" when data.Length == 4: value = BinaryPrimitives.ReadUInt32LittleEndian(data); return true;
            case "S32" when data.Length == 4: value = BinaryPrimitives.ReadInt32LittleEndian(data); return true;
            case "U64" when data.Length == 8: value = BinaryPrimitives.ReadUInt64LittleEndian(data); return true;
            default: return false;
        }
    }

    /// <summary>VISIBLE STRING: bytes up to the first NUL (or the end), Latin-1; empty text stays "".</summary>
    public static string Text(ReadOnlySpan<byte> data)
    {
        int nul = data.IndexOf((byte)0);
        if (nul >= 0) data = data[..nul];
        return Encoding.Latin1.GetString(data);
    }
}
