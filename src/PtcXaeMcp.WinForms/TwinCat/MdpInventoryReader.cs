using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Reads the Beckhoff Device Manager (MDP) of an IPC through an <see cref="IAdsRawReader"/>: device identity, the module
/// list, and every table of every module, sized dynamically by each table's own length entry (subindex 0).
/// Read-only: only config-area and device-area reads, never a service-transfer object.
/// </summary>
internal sealed class MdpInventoryReader(IAdsRawReader transport, MdpReadLimits? limits = null, TimeProvider? clock = null)
{
    private readonly MdpReadLimits _limits = limits ?? new MdpReadLimits();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private int _reads;
    private long _startedAt;
    private bool _budgetExhausted;
    private List<string> _warnings = [];

    /// <summary>Error codes that mean the target machine itself did not answer (as opposed to "no MDP port there").</summary>
    private static bool IsUnreachable(uint code) => code is 7 or 1861;

    public IpcInventoryResult Read(string netId, IReadOnlySet<int>? moduleTypes, bool includeRaw)
    {
        _reads = 0;
        _budgetExhausted = false;
        _warnings = [];
        _startedAt = _clock.GetTimestamp();
        DateTimeOffset readAt = _clock.GetUtcNow();

        IpcInventoryResult Failed(string status, string message, AdsRawReadResult? error) =>
            new(status, netId, MdpAddress.AmsPort, readAt, message,
                error is { Ok: false } ? error.AdsError : null, error is { Ok: false } ? error.AdsErrorName : null,
                null, [], _warnings);

        // 1. Module count: classifies the target (unreachable / no Device Manager / not ready).
        AdsRawReadResult countRead;
        try { countRead = ReadRaw(MdpAddress.ModuleListTable, 0); }
        catch (Exception ex)
        {
            // An ADS exception carries its error code; report it like a failed read, so that code and name are filled.
            var coded = AdsErrorOf(ex);
            if (coded is not null)
                return IsUnreachable(coded.AdsError)
                    ? Failed(IpcInventoryStatus.TargetUnreachable,
                        $"ADS connection to {netId}:{MdpAddress.AmsPort} failed: {coded.ErrorText}.", coded)
                    : Failed(IpcInventoryStatus.MdpUnavailable,
                        $"The target answers, but the Device Manager is not available: {coded.ErrorText}.", coded);
            return Failed(IpcInventoryStatus.TargetUnreachable,
                $"ADS connection to {netId}:{MdpAddress.AmsPort} failed: {ex.Message}", null);
        }
        if (!countRead.Ok)
        {
            return IsUnreachable(countRead.AdsError)
                ? Failed(IpcInventoryStatus.TargetUnreachable,
                    $"Target {netId} did not answer on ADS port {MdpAddress.AmsPort}: {countRead.ErrorText}. " +
                    "Check the route to the target and that TwinCAT is running.", countRead)
                : Failed(IpcInventoryStatus.MdpUnavailable,
                    $"The target answers, but the Device Manager (MDP index group 0x{MdpAddress.IndexGroup:X}) is not available: " +
                    $"{countRead.ErrorText}. The target is probably not a Beckhoff IPC or has no Device Manager.", countRead);
        }
        if (!MdpValueDecoder.TryDecode("U16", countRead.Data, out var countObj))
            return Failed(IpcInventoryStatus.MdpUnavailable,
                $"The module count answer has an unexpected size ({countRead.Data.Length} bytes).", null);
        int count = (int)(uint)countObj!;
        if (count == 0)
            return Failed(IpcInventoryStatus.MdpNotReady,
                "The Device Manager reports no modules yet. It creates its modules some time after boot; wait 10-60 s and retry.", null);
        if (count > _limits.MaxLen)
        {
            _warnings.Add($"Module count {count} exceeds the limit {_limits.MaxLen}; only the first {_limits.MaxLen} are read.");
            count = _limits.MaxLen;
        }

        // 2. Device identity (BTN, BIC); older Device Manager versions do not have it.
        var device = new MdpDevice(ReadDeviceString(0, "BTN"), ReadDeviceString(1, "BIC"));

        // 3. Module list.
        var modules = new List<MdpModule>();
        for (int i = 1; i <= count && !_budgetExhausted; i++)
        {
            var idRead = TryRead(MdpAddress.ModuleListTable, i);
            if (idRead is null) break;
            if (!idRead.Ok || !MdpValueDecoder.TryDecode("U32", idRead.Data, out var idObj))
            {
                _warnings.Add($"Module list entry {i} unreadable ({(idRead.Ok ? "unexpected size" : idRead.ErrorText)}); skipped.");
                continue;
            }
            uint id = (uint)idObj!;
            int moduleType = (int)(id >> 16);
            int moduleId = (int)(id & 0xFFFF);
            if (moduleId > 0xFF)
            {
                _warnings.Add($"Module list entry {i} has module ID 0x{moduleId:X}, outside 0x00-0xFF; skipped.");
                continue;
            }
            if (moduleTypes is not null && !moduleTypes.Contains(moduleType)) continue;

            string? name = null;
            var nameRead = TryRead(MdpAddress.ModuleNameTable, i);
            if (nameRead is { Ok: true }) name = MdpValueDecoder.Text(nameRead.Data);
            else if (nameRead is { Ok: false }) _warnings.Add($"Name of module {i} unreadable: {nameRead.ErrorText}.");

            modules.Add(ReadModule(moduleId, moduleType, name, includeRaw));
        }

        if (_budgetExhausted)
            _warnings.Add($"Read budget exhausted after {_reads} reads; the result is partial.");

        return new IpcInventoryResult(IpcInventoryStatus.Ok, netId, MdpAddress.AmsPort, readAt, null, null, null,
            device, modules, _warnings);
    }

    /// <summary>The ADS error an exception carries (an ErrorCode property of the exception or an inner one), or null.</summary>
    internal static AdsRawReadResult? AdsErrorOf(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            object? value = e.GetType().GetProperty("ErrorCode")?.GetValue(e);
            if (value is null) continue;
            try { return AdsRawReadResult.Failure(Convert.ToUInt32(value, CultureInfo.InvariantCulture), value.ToString() ?? "unknown"); }
            catch (Exception conversion) when (conversion is FormatException or InvalidCastException or OverflowException) { }
        }
        return null;
    }

    private string? ReadDeviceString(int sub, string what)
    {
        var r = TryRead(MdpAddress.DeviceInfoTable, sub);
        if (r is null) return null;
        if (!r.Ok)
        {
            _warnings.Add($"{what} not available: {r.ErrorText}.");
            return null;
        }
        return MdpValueDecoder.Text(r.Data);
    }

    private MdpModule ReadModule(int moduleId, int moduleType, string? name, bool includeRaw)
    {
        var tables = new List<MdpTable>();
        var values = new Dictionary<int, List<(int Sub, object? Value)>>();
        string label = $"module 0x{moduleId:X2} ({MdpLayoutCatalog.TypeName(moduleType)})";

        for (int tableId = 0; tableId <= 0xF && !_budgetExhausted; tableId++)
        {
            int tableIndex = MdpAddress.ConfigTable(moduleId, tableId);
            var layout = MdpLayoutCatalog.Find(moduleType, tableId);
            if (layout is { Skip: true })
            {
                _warnings.Add($"{label} table 0x{tableIndex:X4} ({layout.Name}) is not read: it has no length entry.");
                continue;
            }

            var lenRead = TryRead(tableIndex, 0);
            if (lenRead is null) break;
            if (!lenRead.Ok) continue; // table absent
            if (!MdpValueDecoder.TryDecode("U16", lenRead.Data, out var lenObj))
            {
                _warnings.Add($"{label} table 0x{tableIndex:X4}: length entry has an unexpected size ({lenRead.Data.Length} bytes); skipped.");
                continue;
            }
            int len = (int)(uint)lenObj!;
            if (len > _limits.MaxLen)
            {
                _warnings.Add($"{label} table 0x{tableIndex:X4}: length {len} exceeds the limit {_limits.MaxLen}; only the first {_limits.MaxLen} entries are read.");
                len = _limits.MaxLen;
            }

            var entries = new List<MdpEntry>
            {
                new(0, "Len", "U16", (uint)lenObj!, includeRaw ? Convert.ToHexString(lenRead.Data) : null, null,
                    MdpAddress.IndexOffset(tableIndex, 0)),
            };
            var tableValues = new List<(int, object?)>();
            for (int sub = 1; sub <= len; sub++)
            {
                var r = TryRead(tableIndex, sub);
                if (r is null) break;
                var field = layout?.FieldAt(sub);
                uint offset = MdpAddress.IndexOffset(tableIndex, sub);
                if (!r.Ok)
                {
                    entries.Add(new MdpEntry(sub, field?.Name, field?.Type, null, null, r.ErrorText, offset));
                    tableValues.Add((sub, null));
                    continue;
                }

                object? value = null;
                bool decoded = field is not null && MdpValueDecoder.TryDecode(field.Type, r.Data, out value);
                if (field is not null && !decoded)
                    _warnings.Add($"{label} table 0x{tableIndex:X4} subindex {sub}: {r.Data.Length} bytes do not decode as {field.Type}; raw value kept.");
                bool keepRaw = includeRaw || !decoded;
                entries.Add(new MdpEntry(sub, field?.Name, field?.Type, decoded ? value : null,
                    keepRaw ? Convert.ToHexString(r.Data) : null, null, offset));
                tableValues.Add((sub, decoded ? value : null));
            }
            tables.Add(new MdpTable(tableIndex, layout?.Name, entries));
            values[tableId] = tableValues;
        }

        return new MdpModule(moduleId, moduleType, MdpLayoutCatalog.TypeName(moduleType), name, tables,
            MdpLayoutCatalog.IsKnown(moduleType) ? Decode(moduleType, label, values) : null);
    }

    /// <summary>Builds the typed view: scalar fields by name plus the zipped parallel-array row sets.</summary>
    private Dictionary<string, object?> Decode(int moduleType, string label, Dictionary<int, List<(int Sub, object? Value)>> values)
    {
        var decoded = new Dictionary<string, object?>(StringComparer.Ordinal);
        var grouped = MdpLayoutCatalog.GroupsOf(moduleType).SelectMany(g => g.Columns.Select(c => c.Table)).ToHashSet();

        foreach (var (tableId, list) in values)
        {
            if (tableId == 0 || grouped.Contains(tableId)) continue;
            var layout = MdpLayoutCatalog.Find(moduleType, tableId);
            if (layout?.Fields is null) continue;
            foreach (var (sub, value) in list)
            {
                var field = layout.FieldAt(sub);
                if (field is not null && value is not null) decoded[field.Name] = value;
            }
        }

        foreach (var group in MdpLayoutCatalog.GroupsOf(moduleType))
        {
            var columns = group.Columns
                .Where(c => values.ContainsKey(c.Table))
                .Select(c => (c.Column, Values: values[c.Table].ToDictionary(v => v.Sub, v => v.Value), Len: values[c.Table].Count))
                .ToList();
            if (columns.Count == 0) continue;
            int rows = columns.Max(c => c.Len);
            if (columns.Select(c => c.Len).Distinct().Count() > 1 || columns.Count != group.Columns.Count)
                _warnings.Add($"{label} {group.Name}: column lengths differ ({string.Join(", ", columns.Select(c => $"{c.Column}={c.Len}"))}); missing cells are null.");
            var list = new List<Dictionary<string, object?>>();
            for (int row = 1; row <= rows; row++)
            {
                var item = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var c in group.Columns)
                {
                    var col = columns.FirstOrDefault(x => x.Column == c.Column);
                    item[c.Column] = col.Values is not null && col.Values.TryGetValue(row, out var v) ? v : null;
                }
                list.Add(item);
            }
            decoded[group.Name] = list;
        }

        if (moduleType == MdpLayoutCatalog.TypeTwinCat
            && decoded.TryGetValue("majorVersion", out var major) && decoded.TryGetValue("minorVersion", out var minor)
            && decoded.TryGetValue("buildVersion", out var build))
            decoded["version"] = decoded.TryGetValue("revision", out var revision)
                ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{build}.{revision}")
                : string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{build}");

        return decoded;
    }

    private readonly IAdsRawReader _transport = new RetryOnTimeoutReader(transport);

    private AdsRawReadResult ReadRaw(int tableIndex, int subIndex) =>
        _transport.Read(MdpAddress.IndexGroup, MdpAddress.IndexOffset(tableIndex, subIndex), _limits.BufferSize);

    /// <summary>One counted read; null when the read budget is exhausted. A transport exception becomes a failed result.</summary>
    private AdsRawReadResult? TryRead(int tableIndex, int subIndex)
    {
        if (_budgetExhausted) return null;
        if (_reads >= _limits.MaxReads || _clock.GetElapsedTime(_startedAt) > _limits.Duration)
        {
            _budgetExhausted = true;
            return null;
        }
        _reads++;
        try { return ReadRaw(tableIndex, subIndex); }
        catch (Exception ex) { return AdsRawReadResult.Failure(0xFFFFFFFF, ex.GetType().Name + ": " + ex.Message); }
    }
}
