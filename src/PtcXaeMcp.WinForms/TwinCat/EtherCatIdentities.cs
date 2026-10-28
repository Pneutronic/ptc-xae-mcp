using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>Status values of one EtherCAT master in an identity read.</summary>
internal static class EtherCatMasterStatus
{
    public const string Ok = "ok";
    /// <summary>The master did not answer over ADS (configuration not active on the target, or no route).</summary>
    public const string MasterUnreachable = "masterUnreachable";
    /// <summary>The device XML carries no AMS NetId / ADS port that could be read.</summary>
    public const string AddressNotFound = "addressNotFound";
    /// <summary>The master answered, but not everything could be read (timeouts, ADS errors on single slaves).</summary>
    public const string Partial = "partial";
}

internal static class EtherCatReadStatus
{
    public const string Ok = "ok";
    public const string Partial = "partial";
    public const string Failed = "failed";
}

/// <summary>Identity of a configured box as the device XML projects it (ESI based).</summary>
internal sealed record EtherCatProjectedSlave(
    string TreePath, string Name, long? VendorId, long? ProductCode, long? RevisionNo, int? FixedAddress, bool Passive = false);

/// <summary>An EtherCAT master of the project with its projected boxes. <see cref="RawSnippet"/> is set only when no address was found.</summary>
internal sealed record EtherCatMasterConfig(
    string TreePath, string Name, string? NetId, int? Port, string? AddressSource, string? RawSnippet,
    IReadOnlyList<EtherCatProjectedSlave> Slaves);

internal sealed record EtherCatIdentity(long VendorId, long ProductCode, long RevisionNo, long SerialNo);

internal sealed record EtherCatCrc(long PortA, long PortB, long PortC, long PortD);

/// <summary>Status values of the CoE device info of one slave.</summary>
internal static class EtherCatCoeStatus
{
    public const string Ok = "ok";
    /// <summary>The slave has no mailbox (couplers, simple terminals): the master answers PortNotConnected.</summary>
    public const string NoMailbox = "noMailbox";
    /// <summary>The slave has a mailbox but no CoE (for example SoE drives): the master answers UnknownAmsCommand.</summary>
    public const string NotCoe = "notCoe";
    public const string Error = "error";
}

/// <summary>CoE device info of a slave: 0x1008 name, 0x1009 hardware version, 0x100A firmware version, 0x1018:04 serial.</summary>
internal sealed record EtherCatCoe(
    string Status, string? DeviceName, string? HardwareVersion, string? FirmwareVersion, long? SerialNo, string? AdsError);

internal static class EtherCatSerialSource
{
    public const string Master = "master";
    public const string Coe = "coe";
    public const string Soe = "soe:S-0-0432";
}

internal sealed record EtherCatSlaveRecord(
    int? Address, string? TreePath, string? Name,
    EtherCatProjectedSlave? Projected, EtherCatIdentity? Online,
    int? State, string? StateName, int? Link, IReadOnlyList<string> LinkFlags,
    bool IdentityInvalid, bool RevisionDiffers, bool ProductDiffers, bool VendorDiffers, bool SerialAvailable,
    bool Missing, bool ExtraLink, EtherCatCrc? Crc, string? AdsError,
    string NetId, int Port, uint? IndexGroup, uint? IndexOffset,
    EtherCatCoe? Coe = null, string? SerialNo = null, string? SerialSource = null,
    EtherCatDeviceDetails? Details = null, bool SafetySerialMissing = false, string? JoinedBy = null,
    string? ReadStatus = null, IReadOnlyList<string>? ReadErrors = null, bool SafetyIdentityUnknown = false,
    string? SafetyIdentityUnknownReason = null);

internal sealed record EtherCatMasterIdentities(
    string TreePath, string Name, string Status, string? Message, string? NetId, int? Port,
    string? AddressSource, string? RawSnippet, uint? AdsErrorCode, string? AdsErrorName,
    int? MasterState, string? MasterStateName, int? SlaveCount, IReadOnlyList<EtherCatSlaveRecord> Slaves,
    IReadOnlyList<string> Warnings)
{
    /// <summary>False when a safety device of this master has no unique identifier, or when the master was not read at all.</summary>
    public bool SafetyIdentityComplete =>
        Status == EtherCatMasterStatus.Ok && Slaves.All(s => !s.SafetySerialMissing && !s.SafetyIdentityUnknown);
}

internal sealed record EtherCatIdentitiesResult(DateTimeOffset ReadAt, IReadOnlyList<EtherCatMasterIdentities> Masters)
{
    public bool Success => Masters.Count > 0 && Masters.All(m => m.Status == EtherCatMasterStatus.Ok);

    /// <summary>"partial" when any master is not fully read; "ok" otherwise ("noEtherCatMaster" is the coordinator's).</summary>
    public string Status => Masters.Count == 0 ? "noEtherCatMaster" : Success ? "ok" : "partial";

    public bool SafetyIdentityComplete => Masters.All(m => m.SafetyIdentityComplete);

    public IReadOnlyList<string> Warnings => Masters.SelectMany(m => m.Warnings).ToList();

    public EtherCatSummary Summary
    {
        get
        {
            var slaves = Masters.SelectMany(m => m.Slaves).Where(s => s.IndexGroup is not null).ToList();
            var safety = slaves.Where(s => s.Details?.Safety?.IsSafetyDevice == true).ToList();
            var motors = slaves.SelectMany(s =>
            {
                var list = new List<string?>();
                if (s.Details?.Motor is { } m) list.Add(m.SerialNo);
                if (s.Details?.Soe is { } channels) list.AddRange(channels.Where(c => c.Motor is not null).Select(c => c.Motor!.SerialNo));
                return list;
            }).ToList();
            return new EtherCatSummary(slaves.Count, safety.Count,
                safety.Count(s => !s.SafetySerialMissing && !s.SafetyIdentityUnknown),
                motors.Count, motors.Count(x => !string.IsNullOrEmpty(x)),
                slaves.Count(s => s.ReadStatus == EtherCatReadStatus.Ok),
                slaves.Count(s => s.ReadStatus == EtherCatReadStatus.Failed),
                safety.Count(s => s.SafetyIdentityUnknown),
                slaves.Count(s => s.ReadStatus == EtherCatReadStatus.Partial));
        }
    }
}

/// <summary>Reads the projected identity and the master address out of device XML (ProduceXml).</summary>
internal static class EtherCatProjection
{
    // The master's AMS NetId sits in the device XML (the project file stores it as the AmsNetId attribute of the Device
    // element); these XPath candidates are tried in order and the first usable value wins. The ADS port is NOT taken
    // from the XML: the Device's AmsPort attribute (0x7001) is not the master's service port, which answers on 0xFFFF
    // (the device's own AmsPort, e.g. 0x7001, answers TargetPortNotFound).
    internal static readonly string[] NetIdCandidates = ["//DeviceDef/AmsNetId", "//AmsNetId", "//*/@AmsNetId"];
    internal const int MasterServicePort = 0xFFFF;

    internal const int SnippetLength = 2000;

    public static long? ParseNumber(string? text)
    {
        string s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("#x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long h) ? h : null;
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long d) ? d : null;
    }

    private static XmlDocument? Load(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc;
        }
        catch (XmlException) { return null; }
    }

    /// <summary>The projected identity of a box, or null when the XML has no EtherCAT slave info.</summary>
    // Boxes that never get a fixed address: end caps (EL9xxx) and the E-bus coupler of a CX (EK1200-5xxx).
    private static readonly Regex PassiveName = new(@"(?<![A-Z0-9])(?:EL9\d{3}|EK1200-5\d{3})", RegexOptions.CultureInvariant);

    public static bool LooksPassive(string? name) => name is not null && PassiveName.IsMatch(name);

    /// <summary>
    /// The projected identity of a box, or null when the node is no box. A node without slave info is a (passive) box only
    /// when its name says so. A box is passive when it has no fixed address and is an end cap / E-bus coupler.
    /// </summary>
    public static EtherCatProjectedSlave? ParseSlave(string? xml, string treePath, string name)
    {
        var info = Load(xml)?.SelectSingleNode("//EtherCAT/Slave/Info");
        if (info is null)
            return LooksPassive(name) ? new EtherCatProjectedSlave(treePath, name, null, null, null, null, Passive: true) : null;
        long? phys = ParseNumber(info["PhysAddr"]?.InnerText);
        return new EtherCatProjectedSlave(treePath, name,
            ParseNumber(info["VendorId"]?.InnerText), ParseNumber(info["ProductCode"]?.InnerText),
            ParseNumber(info["RevisionNo"]?.InnerText),
            phys is > 0 and <= 0xFFFF ? (int)phys : null,
            Passive: phys is not > 0 && LooksPassive(name));
    }

    /// <summary>The AMS NetId of a master device and the master service port (always 0xFFFF); both null when no NetId is found.</summary>
    public static (string? NetId, int? Port, string? Source) ParseMasterAddress(string? xml)
    {
        var doc = Load(xml);
        if (doc is null) return (null, null, null);

        string? netId = null, source = null;
        foreach (string xpath in NetIdCandidates)
        {
            string? text = doc.SelectSingleNode(xpath)?.InnerText?.Trim();
            if (AmsNetIdText.IsUsable(text)) { netId = text; source = xpath; break; }
        }

        return netId is null ? (null, null, null) : (netId, MasterServicePort, source);
    }

    /// <summary>
    /// The ADS address of a box as its device XML carries it (<c>EtherCAT/AmsAddress</c>): the master's AMS NetId and the
    /// box's EtherCAT address as port. NetId may be relative to the target (see <see cref="ApplyTargetPrefix"/>); both
    /// null when the XML has no usable address.
    /// </summary>
    public static (string? NetId, int? Port) ParseSlaveAddress(string? xml)
    {
        var doc = Load(xml);
        var address = doc?.SelectSingleNode("//TreeItem/EtherCAT/AmsAddress");
        if (address is null) return (null, null);
        string? netId = address["AmsNetId"]?.InnerText?.Trim();
        long? port = ParseNumber(address["AmsPort"]?.InnerText);
        return AmsNetIdText.IsUsable(netId) && port is > 0 and <= 0xFFFF ? (netId, (int)port) : (null, null);
    }

    /// <summary>A node of the project tree as the box walk needs it.</summary>
    internal interface IProjectionNode
    {
        string Name { get; }
        string Path { get; }
        string Xml { get; }
        IEnumerable<IProjectionNode> Children { get; }
    }

    /// <summary>
    /// The projected boxes below a master, in tree order, each EtherCAT box exactly once (boxes nested below couplers are
    /// reachable on several paths of the walk; the tree path is the identity).
    /// </summary>
    public static List<EtherCatProjectedSlave> CollectSlaves(IProjectionNode master, int maxDepth = 24)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<EtherCatProjectedSlave>();
        void Walk(IProjectionNode node, int depth)
        {
            if (depth >= maxDepth) return;
            foreach (var child in node.Children)
            {
                if (!seen.Add(child.Path)) continue;
                var slave = ParseSlave(child.Xml, child.Path, child.Name);
                if (slave is not null) result.Add(slave);
                Walk(child, depth + 1);
            }
        }
        Walk(master, 0);
        return result;
    }

    /// <summary>
    /// The master NetId of a project is relative to its target ("0.0.0.0.2.1"): the first four octets of the target NetId
    /// replace the leading 0.0.0.0. Returns the NetId unchanged when it is absolute, null when it is relative and no
    /// target NetId is known.
    /// </summary>
    public static string? ApplyTargetPrefix(string netId, string? targetNetId, out bool relative)
    {
        relative = netId.StartsWith("0.0.0.0.", StringComparison.Ordinal);
        if (!relative) return netId;
        if (!AmsNetIdText.IsUsable(targetNetId)) return null;
        string[] target = targetNetId!.Trim().Split('.');
        string[] master = netId.Split('.');
        return string.Join('.', target.Take(4).Concat(master.Skip(4)));
    }

    public static string Snippet(string? xml)
    {
        string s = xml ?? string.Empty;
        return s.Length <= SnippetLength ? s : s[..SnippetLength];
    }
}

/// <summary>
/// Reads the online identity of the slaves of one EtherCAT master over its ADS interface (index groups 0x3, 0x6, 0x7,
/// 0x9, 0x11, 0x12) and joins it with the projected boxes by fixed address. Read-only: no state requests, no writes.
/// </summary>
internal sealed class EtherCatIdentityReader(Func<string, int, IAdsRawReader> open)
{
    internal const uint GroupState = 0x3, GroupCount = 0x6, GroupAddresses = 0x7, GroupLinkState = 0x9,
        GroupIdentity = 0x11, GroupCrc = 0x12;
    internal const uint MasterStateOffset = 0x100;
    internal const int MaxSlaves = 65535;

    public static string StateName(int stateByte) => (stateByte & 0x0F) switch
    {
        1 => "Init", 2 => "PreOp", 3 => "Bootstrap", 4 => "SafeOp", 8 => "Op", var n => $"Unknown(0x{n:X})",
    };

    public static IReadOnlyList<string> LinkFlagNames(int link)
    {
        var flags = new List<string>();
        if (link == 0) { flags.Add("linkOk"); return flags; }
        if ((link & 0x01) != 0) flags.Add("linkNotPresent");
        if ((link & 0x02) != 0) flags.Add("noCommunication");
        if ((link & 0x04) != 0) flags.Add("linkMissing");
        if ((link & 0x08) != 0) flags.Add("additionalLink");
        if ((link & 0x10) != 0) flags.Add("portA");
        if ((link & 0x20) != 0) flags.Add("portB");
        if ((link & 0x40) != 0) flags.Add("portC");
        if ((link & 0x80) != 0) flags.Add("portD");
        return flags;
    }

    /// <summary>Reads every master; one that does not answer is a status of its own and the others continue.</summary>
    public EtherCatIdentitiesResult ReadAll(
        IEnumerable<EtherCatMasterConfig> masters, string? netIdOverride, int? portOverride, bool includeCrc, bool includeCoe = false,
        bool includeDeviceDetails = false) =>
        new(DateTimeOffset.UtcNow, masters.Select(m => Read(m, netIdOverride, portOverride, includeCrc, includeCoe, includeDeviceDetails)).ToList());

    public EtherCatMasterIdentities Read(
        EtherCatMasterConfig master, string? netIdOverride, int? portOverride, bool includeCrc, bool includeCoe = false,
        bool includeDeviceDetails = false)
    {
        string? netId = string.IsNullOrWhiteSpace(netIdOverride) ? master.NetId : netIdOverride.Trim();
        int? port = portOverride ?? master.Port;
        var warnings = new List<string>();

        EtherCatMasterIdentities Fail(string status, string message, AdsRawReadResult? error = null)
        {
            warnings.Add($"Safety identity could not be checked for master '{master.Name}' ({status}).");
            return new(
                master.TreePath, master.Name, status, message, netId, port, master.AddressSource,
                status == EtherCatMasterStatus.AddressNotFound ? master.RawSnippet : null,
                error is { Ok: false } ? error.AdsError : null, error is { Ok: false } ? error.AdsErrorName : null,
                null, null, null, ProjectedOnly(master, netId ?? string.Empty, port ?? 0), warnings);
        }

        if (netId is null || port is null)
            return Fail(EtherCatMasterStatus.AddressNotFound,
                "The AMS NetId of the master could not be read from its device XML" +
                (master.AddressSource is null ? string.Empty : $" ({master.AddressSource})") +
                "; pass amsNetId (and adsPort) together with masterTreePath.");

        IAdsRawReader reader;
        try { reader = new RetryOnTimeoutReader(open(netId, port.Value)); }
        catch (Exception ex) { return Fail(EtherCatMasterStatus.MasterUnreachable, $"ADS connection to {netId}:{port} failed: {ex.Message}"); }

        try
        {
            return ReadFrom(reader, master, netId, port.Value, includeCrc, includeCoe, includeDeviceDetails && includeCoe, warnings);
        }
        catch (Exception ex)
        {
            return Fail(EtherCatMasterStatus.MasterUnreachable, $"Reading {netId}:{port} failed: {ex.Message}");
        }
        finally
        {
            (reader as IDisposable)?.Dispose();
        }
    }

    private static List<EtherCatSlaveRecord> ProjectedOnly(EtherCatMasterConfig master, string netId, int port) =>
        master.Slaves.Select(p => Record(p.FixedAddress, p, null, null, null, null, netId, port, null, null)).ToList();

    private EtherCatMasterIdentities ReadFrom(
        IAdsRawReader reader, EtherCatMasterConfig master, string netId, int port, bool includeCrc, bool includeCoe, bool includeDetails, List<string> warnings)
    {
        EtherCatMasterIdentities Fail(string message, AdsRawReadResult error)
        {
            warnings.Add($"Safety identity could not be checked for master '{master.Name}' ({EtherCatMasterStatus.MasterUnreachable}).");
            return new(
                master.TreePath, master.Name, EtherCatMasterStatus.MasterUnreachable, message, netId, port, master.AddressSource,
                null, error.AdsError, error.AdsErrorName, null, null, null, ProjectedOnly(master, netId, port), warnings);
        }

        // Slave count first: it classifies the master as reachable or not.
        var countRead = reader.Read(GroupCount, 0, 4);
        if (!countRead.Ok)
            return Fail($"The EtherCAT master at {netId}:{port} did not answer: {countRead.ErrorText}. " +
                        "Is the configuration active on the target and is there a route to it?", countRead);
        if (countRead.Data.Length < 2)
            return Fail($"The slave count answer has an unexpected size ({countRead.Data.Length} bytes).", countRead);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(countRead.Data);

        int? masterState = null;
        string? masterStateName = null;
        var stateRead = reader.Read(GroupState, MasterStateOffset, 2);
        if (stateRead.Ok && stateRead.Data.Length >= 2)
        {
            masterState = BinaryPrimitives.ReadUInt16LittleEndian(stateRead.Data);
            masterStateName = StateName(masterState.Value);
        }
        else warnings.Add($"Master state not readable: {(stateRead.Ok ? "unexpected size" : stateRead.ErrorText)}.");

        // The fixed addresses (IG 0x7). A failed read falls back to the projected fixed addresses; it never stops the rest.
        var masterIssues = new List<string>();
        List<int> onlineAddresses;
        var addressRead = count == 0 ? AdsRawReadResult.Success([]) : reader.Read(GroupAddresses, 0, count * 2);
        bool addressesFromMaster = addressRead.Ok;
        if (addressRead.Ok)
        {
            int available = Math.Min(count, addressRead.Data.Length / 2);
            onlineAddresses = Enumerable.Range(0, available)
                .Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(addressRead.Data.AsSpan(i * 2, 2))).ToList();
            if (available < count)
            {
                string issue = $"The master reports {count} slaves but returned {available} fixed addresses.";
                warnings.Add(issue);
                masterIssues.Add(issue);
            }
        }
        else
        {
            onlineAddresses = master.Slaves.Where(p => !p.Passive && p.FixedAddress is not null).Select(p => p.FixedAddress!.Value).ToList();
            string issue = $"The fixed addresses of the slaves are not readable ({addressRead.ErrorText}); " +
                           (onlineAddresses.Count > 0
                               ? $"using the {onlineAddresses.Count} projected fixed addresses instead."
                               : "no projected fixed address is available either, no slave could be read.");
            warnings.Add(issue);
            masterIssues.Add(issue);
        }
        int addressCount = onlineAddresses.Count;

        var linkRead = count == 0 || !addressesFromMaster ? AdsRawReadResult.Success([]) : reader.Read(GroupLinkState, 0, count * 2);
        if (!linkRead.Ok)
        {
            string issue = $"State and link of the slaves not readable: {linkRead.ErrorText}.";
            warnings.Add(issue);
            masterIssues.Add(issue);
        }

        var projectedByAddress = new Dictionary<int, EtherCatProjectedSlave>();
        foreach (var p in master.Slaves)
        {
            if (p.FixedAddress is { } a && !projectedByAddress.TryAdd(a, p))
                warnings.Add($"Fixed address {a} is projected twice ('{projectedByAddress[a].TreePath}' and '{p.TreePath}'); only the first is joined.");
        }

        // The join key is the fixed address. A configuration that was never activated carries none (PhysAddr missing or
        // 0), and then nothing matches: the i-th projected box (tree order, EtherCAT boxes only) is joined to the i-th
        // online address instead. Product and revision differences still show a wrong order.
        var joinable = master.Slaves.Where(p => !p.Passive).ToList();
        bool byOrder = joinable.Count > 0 && addressCount > 0 && !onlineAddresses.Any(projectedByAddress.ContainsKey);
        if (byOrder)
        {
            warnings.Add($"The projected boxes carry no fixed address that matches an online address (never activated configuration?): " +
                         $"joined by order, the i-th projected box ({joinable.Count}) with the i-th online slave ({addressCount}). " +
                         "Check the productDiffers / revisionDiffers flags; a wrong order shows up there.");
            if (joinable.Count != addressCount)
                warnings.Add($"The order join is unreliable: {joinable.Count} projected boxes but {addressCount} online slaves.");
        }

        var records = new List<EtherCatSlaveRecord>();
        var matched = new HashSet<EtherCatProjectedSlave>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < addressCount; i++)
        {
            int address = onlineAddresses[i];
            EtherCatProjectedSlave? projected = null;
            string? joinedBy = null;
            if (byOrder)
            {
                if (i < joinable.Count) { projected = joinable[i]; joinedBy = "order"; }
            }
            else if (projectedByAddress.TryGetValue(address, out var byAddress))
            {
                projected = byAddress;
                joinedBy = "address";
            }
            if (projected is not null) matched.Add(projected);

            // Every read of one slave is guarded: a timeout or an ADS error is recorded on that slave and the next
            // slave is read anyway.
            var slaveErrors = new List<string>();
            int? state = null, link = null;
            if (addressesFromMaster)
            {
                if (!linkRead.Ok) slaveErrors.Add($"IG 0x9: {linkRead.ErrorText}");
                else if (linkRead.Data.Length >= (i + 1) * 2)
                {
                    state = linkRead.Data[i * 2];
                    link = linkRead.Data[i * 2 + 1];
                }
                else slaveErrors.Add("IG 0x9: no state/link for this slave");
            }

            EtherCatIdentity? online = null;
            string? adsError = null;
            EtherCatCrc? crc = null;
            EtherCatCoe? coe = null;
            EtherCatDeviceDetails? details = null;
            try
            {
                var idRead = reader.Read(GroupIdentity, (uint)address, 16);
                if (!idRead.Ok) adsError = idRead.ErrorText;
                else if (idRead.Data.Length < 16) adsError = $"identity answer has {idRead.Data.Length} bytes, expected 16";
                else
                {
                    var d = idRead.Data.AsSpan();
                    online = new EtherCatIdentity(BinaryPrimitives.ReadUInt32LittleEndian(d), BinaryPrimitives.ReadUInt32LittleEndian(d[4..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(d[8..]), BinaryPrimitives.ReadUInt32LittleEndian(d[12..]));
                }
                if (adsError is not null) slaveErrors.Add($"IG 0x11: {adsError}");

                if (includeCrc)
                {
                    var crcRead = reader.Read(GroupCrc, (uint)address, 16);
                    if (crcRead.Ok && crcRead.Data.Length >= 16)
                    {
                        var d = crcRead.Data.AsSpan();
                        crc = new EtherCatCrc(BinaryPrimitives.ReadUInt32LittleEndian(d), BinaryPrimitives.ReadUInt32LittleEndian(d[4..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(d[8..]), BinaryPrimitives.ReadUInt32LittleEndian(d[12..]));
                    }
                    else
                    {
                        string crcError = $"CRC counters of slave {address} not readable: {(crcRead.Ok ? "unexpected size" : crcRead.ErrorText)}.";
                        warnings.Add(crcError);
                        slaveErrors.Add($"IG 0x12: {(crcRead.Ok ? "unexpected size" : crcRead.ErrorText)}");
                    }
                }

                if (includeCoe && online is not null)
                {
                    var (coeResult, detailResult, failures) = ReadCoe(netId, address, includeDetails, projected?.Name);
                    coe = coeResult;
                    details = detailResult;
                    slaveErrors.AddRange(failures);
                }
            }
            catch (Exception ex)
            {
                slaveErrors.Add($"read failed: {ex.Message}");
                adsError ??= ex.Message;
            }

            string readStatus = online is null ? EtherCatReadStatus.Failed
                : slaveErrors.Count > 0 ? EtherCatReadStatus.Partial : EtherCatReadStatus.Ok;
            records.Add(Record(address, projected, online, state, link, crc, netId, port, adsError, GroupIdentity, coe, details, joinedBy)
                with { ReadStatus = readStatus, ReadErrors = slaveErrors });
        }

        if (includeDetails) ResolveSafetyIdentifiers(records, warnings);

        // Projected boxes without an online address are listed too.
        foreach (var p in master.Slaves)
            if (!matched.Contains(p))
                records.Add(Record(p.FixedAddress, p, null, null, null, null, netId, port, null, null));

        var incomplete = records.Where(r => r.IndexGroup is not null && r.ReadStatus != EtherCatReadStatus.Ok).ToList();
        if (incomplete.Count > 0)
            warnings.Add($"{incomplete.Count} of {addressCount} slaves could not be read completely (addresses {string.Join(", ", incomplete.Select(r => r.Address))}).");

        bool partial = masterIssues.Count > 0 || incomplete.Count > 0;
        return new EtherCatMasterIdentities(master.TreePath, master.Name,
            partial ? EtherCatMasterStatus.Partial : EtherCatMasterStatus.Ok,
            partial ? "Not everything could be read (timeouts or ADS errors); see the slaves' readErrors and the warnings." : null,
            netId, port, master.AddressSource, null, null, null, masterState, masterStateName, count, records, warnings);
    }

    internal const uint CoeIndexGroup = 0xF302;

    private static uint CoeOffset(int index, int subIndex) => ((uint)index << 16) | (uint)subIndex;

    private static string? CoeText(AdsRawReadResult r) => r.Ok ? MdpValueDecoder.Text(r.Data).TrimEnd(' ') : null;

    /// <summary>
    /// Read-only SDO upload of the device info through the master: AMS NetId = master NetId, port = the slave's fixed
    /// address, index group 0xF302, index offset = index &lt;&lt; 16 | subindex. With <paramref name="includeDetails"/> the
    /// BIC, safety identity and motor name plate follow, and for AX5000 drives (CoE unsupported, or an AX5xxx name) the
    /// SoE parameters (index group 0xF420) over the same connection.
    /// </summary>
    private (EtherCatCoe Coe, EtherCatDeviceDetails? Details, List<string> Failures) ReadCoe(
        string netId, int address, bool includeDetails, string? projectedName)
    {
        var failures = new List<string>();
        IAdsRawReader coe;
        try { coe = new RetryOnTimeoutReader(open(netId, address)); }
        catch (Exception ex)
        {
            failures.Add($"CoE connection: {ex.Message}");
            return (new EtherCatCoe(EtherCatCoeStatus.Error, null, null, null, null, ex.Message), null, failures);
        }

        try
        {
            var name = coe.Read(CoeIndexGroup, CoeOffset(0x1008, 0), 128);
            if (!name.Ok)
            {
                string status = name.AdsErrorName.Equals("PortNotConnected", StringComparison.OrdinalIgnoreCase) ? EtherCatCoeStatus.NoMailbox
                    : name.AdsErrorName.Equals("UnknownAmsCommand", StringComparison.OrdinalIgnoreCase) ? EtherCatCoeStatus.NotCoe
                    : EtherCatCoeStatus.Error;
                if (EtherCatDeviceDetailReader.IsTransient(name)) failures.Add($"CoE 0x1008: {name.ErrorText}");
                EtherCatDeviceDetails? soeOnly = null;
                if (includeDetails && status == EtherCatCoeStatus.NotCoe)
                {
                    var channels = EtherCatDeviceDetailReader.ReadSoe(coe, failures);
                    if (channels.Count > 0) soeOnly = new EtherCatDeviceDetails(null, null, null, null, channels, []);
                    failures.AddRange(channels.SelectMany(c => c.Errors).Where(EtherCatDeviceDetailReader.IsTransientText));
                }
                return (new EtherCatCoe(status, null, null, null, null, name.ErrorText), soeOnly, failures);
            }

            var hw = coe.Read(CoeIndexGroup, CoeOffset(0x1009, 0), 128);
            var sw = coe.Read(CoeIndexGroup, CoeOffset(0x100A, 0), 128);
            var serial = coe.Read(CoeIndexGroup, CoeOffset(0x1018, 4), 4);
            if (EtherCatDeviceDetailReader.IsTransient(hw)) failures.Add($"CoE 0x1009: {hw.ErrorText}");
            if (EtherCatDeviceDetailReader.IsTransient(sw)) failures.Add($"CoE 0x100A: {sw.ErrorText}");
            if (EtherCatDeviceDetailReader.IsTransient(serial)) failures.Add($"CoE 0x1018:04: {serial.ErrorText}");
            long? serialNo = serial.Ok && serial.Data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(serial.Data) : null;
            string? error = !serial.Ok ? $"0x1018:04: {serial.ErrorText}" : serialNo is null ? "0x1018:04: unexpected size" : null;
            string? deviceName = CoeText(name);
            var result = new EtherCatCoe(EtherCatCoeStatus.Ok, deviceName, CoeText(hw), CoeText(sw), serialNo, error);
            if (!includeDetails) return (result, null, failures);

            var details = EtherCatDeviceDetailReader.ReadCoeDetails(coe, deviceName, projectedName);
            failures.AddRange(details.Errors.Where(EtherCatDeviceDetailReader.IsTransientText));
            if (EtherCatDeviceParsing.LooksLikeDrive(deviceName, details.Bic?.Article, projectedName))
            {
                var channels = EtherCatDeviceDetailReader.ReadSoe(coe, failures);
                if (channels.Count > 0) details = details with { Soe = channels };
                failures.AddRange(channels.SelectMany(c => c.Errors).Where(EtherCatDeviceDetailReader.IsTransientText));
            }
            return (result, details, failures);
        }
        catch (Exception ex)
        {
            failures.Add($"CoE read failed: {ex.Message}");
            return (new EtherCatCoe(EtherCatCoeStatus.Error, null, null, null, null, ex.Message), null, failures);
        }
        finally
        {
            (coe as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Second pass: every safety device gets an effective identifier (F980:02, BTN, 0x1018:04, host drive serial) or is
    /// flagged safetySerialMissing and listed in a warning. Never a silent 0/null.
    /// </summary>
    private static void ResolveSafetyIdentifiers(List<EtherCatSlaveRecord> records, List<string> warnings)
    {
        var missing = new List<string>();
        var unknown = new List<string>();
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (rec.IndexGroup is null) continue;

            // A safety device whose reads timed out is still known by its article pattern: its identity is unknown, not absent.
            var safety = rec.Details?.Safety;
            if (safety is null && rec.ReadErrors is { Count: > 0 }
                && EtherCatDeviceParsing.LooksLikeSafetyDevice(rec.Name, rec.Coe?.DeviceName))
                safety = new EtherCatSafety(true, null, null, null, false, null, null, false, null, "article pattern");
            if (safety is not { IsSafetyDevice: true }) continue;
            var details = rec.Details ?? new EtherCatDeviceDetails(null, null, null, null, null, []);

            EtherCatSlaveRecord? host = null;
            if (i > 0 && records[i - 1].Online is not null
                && (records[i - 1].Details?.Soe is not null
                    || EtherCatDeviceParsing.LooksLikeDrive(records[i - 1].Name, records[i - 1].Coe?.DeviceName, records[i - 1].Details?.Bic?.Article)))
                host = records[i - 1];

            string?[] names = [rec.Name, rec.Coe?.DeviceName, details.Bic?.Article];
            var (value, source, viaHost, hostAddress) = EtherCatDeviceDetailReader.EffectiveSafetyIdentifier(
                details, rec.SerialNo, rec.SerialSource, names, host);
            bool absent = value is null;
            bool isUnknown = absent && rec.ReadErrors is { Count: > 0 };
            string? reason = isUnknown ? string.Join("; ", rec.ReadErrors!.Take(3)) : null;

            // An AX5805/AX5806 always shows its host drive, whatever identifier it ended up with.
            EtherCatSafetyHostDrive? hostDrive = null;
            if (EtherCatDeviceParsing.IsDriveSafetyOption(names) && host?.Details?.Soe is { Count: > 0 } channels)
            {
                var channel = channels.FirstOrDefault(c => !string.IsNullOrEmpty(c.SerialNo)) ?? channels[0];
                hostDrive = new EtherCatSafetyHostDrive(host.Address, channel.SerialNo, channel.SafetyOption);
            }
            records[i] = rec with
            {
                // One serial per device at the top level: a safety device without a master/CoE/SoE serial takes its
                // effective safety identifier (with the source that says where it came from).
                SerialNo = rec.SerialNo ?? value,
                SerialSource = rec.SerialNo is null ? source : rec.SerialSource,
                SerialAvailable = rec.SerialAvailable || value is not null,
                Details = details with { Safety = safety with { EffectiveSerial = value, EffectiveSerialSource = source, ViaHostDrive = viaHost, HostDriveAddress = hostAddress, HostDrive = hostDrive } },
                SafetySerialMissing = absent && !isUnknown,
                SafetyIdentityUnknown = isUnknown,
                SafetyIdentityUnknownReason = reason,
            };
            string who = $"address {rec.Address}, tree path '{rec.TreePath ?? "-"}', name '{rec.Name ?? rec.Coe?.DeviceName ?? "-"}'";
            if (isUnknown) unknown.Add($"{who} ({reason})");
            else if (absent) missing.Add(who);
        }
        if (missing.Count > 0)
            warnings.Add($"Safety devices without a unique identifier ({missing.Count}): {string.Join("; ", missing)}.");
        if (unknown.Count > 0)
            warnings.Add($"Safety identity could not be read for ({unknown.Count}): {string.Join("; ", unknown)}.");
    }

    private static EtherCatSlaveRecord Record(
        int? address, EtherCatProjectedSlave? projected, EtherCatIdentity? online, int? state, int? link, EtherCatCrc? crc,
        string netId, int port, string? adsError, uint? indexGroup, EtherCatCoe? coe = null, EtherCatDeviceDetails? details = null,
        string? joinedBy = null)
    {
        // Serial precedence: the master's identity read if it is not 0, else CoE 0x1018:04 if it is not 0, else the SoE
        // S-0-0432 text of an AX5000 drive. The serial is text: SoE serials keep their leading zeros.
        string? serial = null;
        string? source = null;
        if (online is { SerialNo: not 0 }) { serial = online.SerialNo.ToString(System.Globalization.CultureInfo.InvariantCulture); source = EtherCatSerialSource.Master; }
        else if (coe is { SerialNo: not null and not 0 }) { serial = coe.SerialNo.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); source = EtherCatSerialSource.Coe; }
        else if (SoeSerial(details) is { } soe) { serial = soe; source = EtherCatSerialSource.Soe; }
        return Build(address, projected, online, state, link, crc, netId, port, adsError, indexGroup, coe, serial, source, details, joinedBy);
    }

    // AX5000 drives have no CoE serial; S-0-0432 of the first channel that has one stands for the device.
    private static string? SoeSerial(EtherCatDeviceDetails? details)
    {
        string? text = details?.Soe?.Select(c => c.SerialNo).FirstOrDefault(t => !string.IsNullOrEmpty(t) && t.Any(c => c != '0'));
        return text;
    }

    private static EtherCatSlaveRecord Build(
        int? address, EtherCatProjectedSlave? projected, EtherCatIdentity? online, int? state, int? link, EtherCatCrc? crc,
        string netId, int port, string? adsError, uint? indexGroup, EtherCatCoe? coe, string? serial, string? serialSource,
        EtherCatDeviceDetails? details, string? joinedBy) => new(
        address, projected?.TreePath, projected?.Name, projected, online,
        state, state is null ? null : StateName(state.Value), link, link is null ? [] : LinkFlagNames(link.Value),
        IdentityInvalid: state is { } s && (s & 0x20) != 0,
        RevisionDiffers: online is not null && projected?.RevisionNo is { } pr && pr != online.RevisionNo,
        ProductDiffers: online is not null && projected?.ProductCode is { } pc && pc != online.ProductCode,
        VendorDiffers: online is not null && projected?.VendorId is { } pv && pv != online.VendorId,
        SerialAvailable: serial is not null,
        Missing: link is { } l1 && (l1 & 0x04) != 0,
        ExtraLink: link is { } l2 && (l2 & 0x08) != 0,
        crc, adsError, netId, port, indexGroup, indexGroup is null || address is null ? null : (uint)address.Value,
        coe, serial, serialSource, details, JoinedBy: joinedBy);
}
