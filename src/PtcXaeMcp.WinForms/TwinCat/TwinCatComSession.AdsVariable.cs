using System.Globalization;
#if TWINCAT
using TwinCAT;            // SymbolLoaderSettings
using TwinCAT.Ads;        // AdsClient
using TwinCAT.Ads.Plc;   // MemoryArea, IndexGroupMemoryAreaExtension
using TwinCAT.Ads.TypeSystem;  // SymbolLoaderFactory
using TwinCAT.TypeSystem; // ISymbol, IProcessImageAddress
#endif

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- ads-variable-rw --------------------------------------------------------
    //
    // Reads / writes live PLC variable values via the TwinCAT ADS protocol
    // (Beckhoff.TwinCAT.Ads NuGet, v7). Unlike the DTE-based COM tools, this
    // path talks directly to the running ADS server (XAR or UMR) — not to the
    // XAE IDE — so XAE does not have to be in the loop for each read/write.
    //
    // Type detection: SymbolLoaderFactory loads the full symbol table once per
    // call and maps the TwinCAT type name to a .NET type for ReadAny/WriteAny.
    // Supported primitives: BOOL BIT BYTE WORD DWORD SINT INT DINT LINT
    //                       USINT UINT UDINT ULINT REAL LREAL STRING(*).
    // ARRAY[lo..hi] OF STRING is handled separately (raw byte marshaling —
    // ReadAny/WriteAny have no built-in support for string arrays with
    // per-element size), see TryGetStringArrayCount/ReadStringArray/WriteStringArray.
    // ENUM symbols (e.g. E_STATE) are handled separately too — the declared
    // type name itself isn't a MapPlcType primitive, so IsEnumSymbol resolves
    // the underlying primitive via IAliasType.BaseTypeName and the symbolic
    // name via IEnumType.EnumValues, see ReadEnumValue/ConvertEnumFromJson.
    //
    // Write safety: AT %I* symbols (hardware inputs) are refused; the check
    // is done via ISymbol.Category (Symbol vs SomeOtherCategory) and whether
    // the symbol has an address that indicates an input mapping.
    //
    // AmsNetId default: the local AMS router's NetId (issue #202, LocalAmsNetIdResolver);
    // the registry's AmsNetId, then the loopback "127.0.0.1.1.1", only when the router
    // does not answer. See AdsTargetNetId.Resolve.

#if !TWINCAT
    public AdsVariableResult ReadAdsVariable(string symbolPath, string? amsNetId, int adsPort)
        => new(false, symbolPath, null, null, amsNetId ?? "local", adsPort, TwinCatNotBuilt.Message);

    public AdsVariableResult WriteAdsVariable(string symbolPath, string value, string? amsNetId, int adsPort)
        => new(false, symbolPath, null, null, amsNetId ?? "local", adsPort, TwinCatNotBuilt.Message);

    public AdsBatchReadResult ReadAdsVariablesBatch(IReadOnlyList<string> symbolPaths, string? amsNetId, int adsPort)
        => new(false, Array.Empty<AdsBatchReadItem>(), amsNetId ?? "local", adsPort, TwinCatNotBuilt.Message);

    public SignalTransitionResult MeasureSignalTransition(string startSymbol, string startEquals,
        string endSymbol, string endEquals, int timeoutMs, string? amsNetId, int adsPort)
        => new(false, 0, false, TwinCatNotBuilt.Message);

    public InspectFbInstanceResult InspectFbInstance(string instancePath, string? amsNetId, int adsPort)
        => new(false, instancePath, null, Array.Empty<FbMemberEntry>(), Array.Empty<FbRpcMethodEntry>(),
            amsNetId ?? "local", adsPort, TwinCatNotBuilt.Message);
#else

    private static readonly TimeSpan AdsTimeout = TimeSpan.FromSeconds(10);

    public AdsVariableResult ReadAdsVariable(string symbolPath, string? amsNetId, int adsPort)
    {
        string resolvedNetId = ResolveAmsNetId(amsNetId);
        try
        {
            using var client = new AdsClient();
            client.Connect(resolvedNetId, adsPort);

            var (sym, loadError) = LoadSymbol(client, symbolPath);
            if (sym is null)
            {
                var propertyResult = TryReadProperty(client, symbolPath, resolvedNetId, adsPort);
                if (propertyResult is not null) return propertyResult;
                return new(false, symbolPath, null, null, resolvedNetId, adsPort, loadError!);
            }

            string plcType = NormalizeTypeName(sym.TypeName);

            if (TryGetStringArrayCount(plcType, out int arrCount))
            {
                try
                {
                    string[] values = ReadStringArray(client, symbolPath, sym, arrCount);
                    return new(true, symbolPath, plcType, values, resolvedNetId, adsPort, "OK");
                }
                catch (Exception ex) when (ComCall.NotBusy(ex)) {
                    return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                        $"[Error] Failed to read STRING array: {ex.Message}");
                }
            }

            if (plcType == "STRING")
            {
                try
                {
                    return new(true, symbolPath, plcType, ReadScalarString(client, symbolPath, sym),
                        resolvedNetId, adsPort, "OK");
                }
                catch (Exception ex) when (ComCall.NotBusy(ex)) {
                    return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                        $"[Error] Failed to read STRING: {ex.Message}");
                }
            }

            if (sym.DataType is IEnumType enumType)
            {
                string enumBase = NormalizeTypeName(enumType.BaseTypeName);
                Type? enumDotNetType = MapPlcType(enumBase);
                if (enumDotNetType is null)
                    return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                        $"[Error] Unsupported ENUM base type '{enumBase}' for '{plcType}'.");

                uint enumHandle = (uint)client.CreateVariableHandle(symbolPath);
                try
                {
                    object rawValue = client.ReadAny(enumHandle, enumDotNetType);
                    return new(true, symbolPath, plcType, ToEnumJsonValue(rawValue, enumType), resolvedNetId, adsPort, "OK");
                }
                finally
                {
                    try { client.DeleteVariableHandle(enumHandle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                }
            }

            Type? dotNetType = MapPlcType(plcType);
            if (dotNetType is null)
                return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                    $"[Error] Unsupported PLC type '{plcType}'. " +
                    $"Supported: BOOL BIT BYTE WORD DWORD SINT INT DINT LINT USINT UINT UDINT ULINT REAL LREAL STRING ARRAY[..] OF STRING ENUM.");

            uint handle = (uint)client.CreateVariableHandle(symbolPath);
            try
            {
                object rawValue = client.ReadAny(handle, dotNetType);
                object jsonValue = ConvertToJson(rawValue, plcType);
                return new(true, symbolPath, plcType, jsonValue, resolvedNetId, adsPort, "OK");
            }
            finally
            {
                try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) {
            return new(false, symbolPath, null, null, resolvedNetId, adsPort,
                $"ReadAdsVariable failed: {ex.Message}");
        }
    }

    // A PROPERTY without its own backing VAR never appears in
    // IStructInstance.MemberInstances under its own declared name, so
    // LoadSymbol legitimately reports "not found" for it and
    // CreateVariableHandle would fail the same way. Three cases exist, all
    // confirmed live 2026-08-08 against a real FB instance (PLC.tmc + a real
    // read), tried in order (cheapest/most-likely-to-exist first):
    //
    //  1. {attribute 'monitoring':='variable'} makes the compiler create a
    //     REAL, addressable shadow variable -- but under a mangled name, not
    //     the clean property name, e.g. property "CompressedAirPressure" on
    //     FB_Machine backs onto member
    //     "__FB_MACHINE____GETCOMPRESSEDAIRPRESSURE___COMPRESSEDAIRPRESSURE"
    //     (a plain, directly-readable BOOL -- verified live, matches the XAE
    //     Watch window's value exactly). Found by pattern match instead of
    //     reconstructing the type-qualified prefix ourselves, because the
    //     declaring type can differ from the instance's own type (inherited
    //     properties).
    //  2. The compiler marked the getter RPC-callable (RpcEnable="plc" in the
    //     module's .tmc -- a per-property compiled flag, not a project-wide
    //     switch; absent for plenty of properties, e.g. PTC_Library's
    //     FB_Base.State/.OperatingMode). The getter is named __get<Name>
    //     (double underscore, no separator -- confirmed against PLC.tmc; NOT
    //     the "get_<Name>" convention this method originally guessed) and can
    //     be invoked via ADS RPC.
    //  3. Neither of the above: the property is not reachable via raw ADS AT
    //     ALL -- not as a symbol, not via RPC -- only via the XAE
    //     Automation-Interface/online-value channel (a different transport
    //     than TwinCAT.Ads.AdsClient), which is why the XAE Watch window can
    //     still show a live value while this path cannot (confirmed for
    //     State/OperatingMode). Returns null so the caller falls back to the
    //     original "symbol not found" error instead of masking it.
    private static AdsVariableResult? TryReadProperty(AdsClient client, string symbolPath, string resolvedNetId, int adsPort)
    {
        int lastDot = symbolPath.LastIndexOf('.');
        if (lastDot <= 0) return null;

        string parentPath = symbolPath[..lastDot];
        string memberName = symbolPath[(lastDot + 1)..];

        var (parentSym, _) = LoadSymbol(client, parentPath);
        if (parentSym is null) return null;

        if (parentSym is IStructInstance st)
        {
            string marker = "____GET" + memberName.ToUpperInvariant();
            string suffix = "___" + memberName.ToUpperInvariant();
            var shadow = st.MemberInstances.FirstOrDefault(m =>
                m.InstanceName.Contains(marker, StringComparison.OrdinalIgnoreCase) &&
                m.InstanceName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (shadow is not null)
            {
                var shadowResult = TryReadResolvedSymbol(client, symbolPath, shadow, resolvedNetId, adsPort);
                if (shadowResult is not null) return shadowResult;
            }
        }

        if (parentSym is IRpcCallableInstance rpc && rpc.RpcMethods.TryGetMethod("__get" + memberName, out var getter))
        {
            try
            {
                object? raw = rpc.InvokeRpcMethod(getter.Name, Array.Empty<object>());
                string plcType = NormalizeTypeName(getter.ReturnType);
                object? jsonValue = raw switch
                {
                    float f => float.IsFinite(f) ? (object)f : f.ToString(CultureInfo.InvariantCulture),
                    double d => double.IsFinite(d) ? (object)d : d.ToString(CultureInfo.InvariantCulture),
                    null or bool or byte or sbyte or short or ushort or int or uint or long or ulong or string => raw,
                    _ => raw.ToString(),
                };
                return new(true, symbolPath, plcType, jsonValue, resolvedNetId, adsPort, "OK");
            }
            catch (Exception ex) when (ComCall.NotBusy(ex)) {
                return new(false, symbolPath, null, null, resolvedNetId, adsPort,
                    $"[Error] Failed to read PROPERTY '{symbolPath}' via RPC getter '{getter.Name}': {ex.Message}");
            }
        }

        return null;
    }

    // Reads an already-resolved ISymbol (typically the mangled shadow
    // variable behind a monitored PROPERTY) using its OWN InstancePath for
    // the ADS handle, while reporting the caller's originally-requested
    // symbolPath in the result. Mirrors the primitive/string-array/enum
    // dispatch in ReadAdsVariable, kept as its own self-contained helper so
    // this fallback cannot affect the already-verified direct-symbol path.
    // Returns null on any failure so the caller can fall through cleanly.
    private static AdsVariableResult? TryReadResolvedSymbol(AdsClient client, string symbolPath, ISymbol sym, string resolvedNetId, int adsPort)
    {
        string plcType = NormalizeTypeName(sym.TypeName);
        try
        {
            if (TryGetStringArrayCount(plcType, out int arrCount))
            {
                string[] values = ReadStringArray(client, sym.InstancePath, sym, arrCount);
                return new(true, symbolPath, plcType, values, resolvedNetId, adsPort, "OK");
            }

            if (plcType == "STRING")
                return new(true, symbolPath, plcType,
                    ReadScalarString(client, sym.InstancePath, sym), resolvedNetId, adsPort, "OK");

            if (sym.DataType is IEnumType enumType)
            {
                string enumBase = NormalizeTypeName(enumType.BaseTypeName);
                Type? enumDotNetType = MapPlcType(enumBase);
                if (enumDotNetType is null) return null;

                uint enumHandle = (uint)client.CreateVariableHandle(sym.InstancePath);
                try
                {
                    object rawValue = client.ReadAny(enumHandle, enumDotNetType);
                    return new(true, symbolPath, plcType, ToEnumJsonValue(rawValue, enumType), resolvedNetId, adsPort, "OK");
                }
                finally
                {
                    try { client.DeleteVariableHandle(enumHandle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                }
            }

            Type? dotNetType = MapPlcType(plcType);
            if (dotNetType is null) return null;

            uint handle = (uint)client.CreateVariableHandle(sym.InstancePath);
            try
            {
                object rawValue = client.ReadAny(handle, dotNetType);
                object jsonValue = ConvertToJson(rawValue, plcType);
                return new(true, symbolPath, plcType, jsonValue, resolvedNetId, adsPort, "OK");
            }
            finally
            {
                try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) {
            return null;
        }
    }

    public AdsVariableResult WriteAdsVariable(string symbolPath, string value, string? amsNetId, int adsPort)
    {
        string resolvedNetId = ResolveAmsNetId(amsNetId);
        try
        {
            using var client = new AdsClient();
            client.Connect(resolvedNetId, adsPort);

            var (sym, loadError) = LoadSymbol(client, symbolPath);
            if (sym is null)
                return new(false, symbolPath, null, null, resolvedNetId, adsPort, loadError!);

            // Refuse writes to input-mapped symbols (%I*).
            if (IsInputMapped(sym))
                return new(false, symbolPath, sym.TypeName, null, resolvedNetId, adsPort,
                    $"[Error] Symbol '{symbolPath}' is input-mapped (AT %I*) — write refused.");

            string plcType = NormalizeTypeName(sym.TypeName);

            if (TryGetStringArrayCount(plcType, out int arrCount))
            {
                try
                {
                    byte[] written = WriteStringArray(client, symbolPath, sym, arrCount, value);
                    // D-3 (issue #152): compare the bytes read back slot by slot with what was written.
                    byte[] back = ReadRaw(client, symbolPath, (int)sym.Size);
                    int elemSize = arrCount > 0 ? written.Length / arrCount : 0;
                    for (int i = 0; i < arrCount; i++)
                    {
                        string? mismatch = PlcStringCodec.SlotMismatch(written, back, i * elemSize, elemSize);
                        if (mismatch is not null)
                            return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                                $"[Error] The STRING array write did not land: element {i}: {mismatch}" + OverwrittenHint);
                    }
                    string[] roundTrip = ReadStringArray(client, symbolPath, sym, arrCount);
                    return new(true, symbolPath, plcType, roundTrip, resolvedNetId, adsPort, "OK (read back)");
                }
                catch (Exception ex) when (ComCall.NotBusy(ex)) {
                    return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                        $"[Error] Failed to write STRING array: {ex.Message}");
                }
            }

            if (plcType == "STRING")
            {
                try
                {
                    byte[] written = WriteScalarString(client, symbolPath, sym, value);
                    byte[] back = ReadRaw(client, symbolPath, written.Length);
                    string? mismatch = PlcStringCodec.SlotMismatch(written, back, 0, written.Length);
                    if (mismatch is not null)
                        return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                            $"[Error] The STRING write did not land: {mismatch}" + OverwrittenHint);
                    return new(true, symbolPath, plcType, PlcStringCodec.Decode(back, 0, back.Length),
                        resolvedNetId, adsPort, "OK (read back)");
                }
                catch (Exception ex) when (ComCall.NotBusy(ex)) {
                    return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                        $"[Error] Failed to write STRING: {ex.Message}");
                }
            }

            if (sym.DataType is IEnumType enumType)
            {
                string enumBase = NormalizeTypeName(enumType.BaseTypeName);
                Type? enumDotNetType = MapPlcType(enumBase);
                if (enumDotNetType is null)
                    return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                        $"[Error] Unsupported ENUM base type '{enumBase}' for '{plcType}'.");

                object? enumConverted = ConvertEnumFromJson(value, enumType, enumDotNetType);
                if (enumConverted is null)
                    return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                        $"[Error] Cannot convert '{value}' to ENUM '{plcType}'. " +
                        $"Use a member name ({string.Join(", ", enumType.GetNames())}) or a numeric value.");

                uint enumHandle = (uint)client.CreateVariableHandle(symbolPath);
                try
                {
                    client.WriteAny(enumHandle, enumConverted);
                    object enumBack = client.ReadAny(enumHandle, enumDotNetType);
                    string? enumMismatch = AdsReadBack.PrimitiveMismatch(enumConverted, enumBack);
                    if (enumMismatch is not null)
                        return new(false, symbolPath, plcType, ToEnumJsonValue(enumBack, enumType), resolvedNetId, adsPort,
                            $"[Error] The write did not land: {enumMismatch}" + OverwrittenHint);
                    return new(true, symbolPath, plcType, ToEnumJsonValue(enumBack, enumType), resolvedNetId, adsPort, "OK (read back)");
                }
                finally
                {
                    try { client.DeleteVariableHandle(enumHandle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                }
            }

            Type? dotNetType = MapPlcType(plcType);
            if (dotNetType is null)
                return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                    $"[Error] Unsupported PLC type '{plcType}'.");

            if (dotNetType == typeof(bool) && !AdsBoolValue.TryParse(value, out _))
                return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                    AdsBoolValue.UnknownMessage(value));
            object? converted = ConvertFromJson(value, plcType, dotNetType);
            if (converted is null)
                return new(false, symbolPath, plcType, null, resolvedNetId, adsPort,
                    $"[Error] Cannot convert '{value}' to PLC type '{plcType}'.");

            uint handle = (uint)client.CreateVariableHandle(symbolPath);
            try
            {
                client.WriteAny(handle, converted);
                // D-3 (issue #152): WriteAny returning is not proof - read the symbol back.
                object back = client.ReadAny(handle, dotNetType);
                string? mismatch = AdsReadBack.PrimitiveMismatch(converted, back);
                if (mismatch is not null)
                    return new(false, symbolPath, plcType, ConvertToJson(back, plcType), resolvedNetId, adsPort,
                        $"[Error] The write did not land: {mismatch}" + OverwrittenHint);
                return new(true, symbolPath, plcType, ConvertToJson(back, plcType), resolvedNetId, adsPort, "OK (read back)");
            }
            finally
            {
                try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) {
            return new(false, symbolPath, null, null, resolvedNetId, adsPort,
                $"WriteAdsVariable failed: {ex.Message}");
        }
    }

    public AdsBatchReadResult ReadAdsVariablesBatch(IReadOnlyList<string> symbolPaths, string? amsNetId, int adsPort)
    {
        string resolvedNetId = ResolveAmsNetId(amsNetId);
        try
        {
            using var client = new AdsClient();
            client.Connect(resolvedNetId, adsPort);

            // Resolve every symbol over the SAME connection. The symbol table is
            // loaded lazily on first miss (LoadSymbol caches nothing, so for large
            // checklists this still scans the flat list per symbol — acceptable for
            // the snapshot sizes IBN uses; can be upgraded to the ADS sum-command
            // (0xF080) later for hot paths). D-2 (issue #152): each symbol's failure is
            // isolated — including CreateVariableHandle, which used to sit outside the
            // per-item try and ended the whole batch on the first bad symbol.
            var items = AdsReadBack.CollectBatch(symbolPaths,
                symbolPath => ReadBatchItem(client, symbolPath, resolvedNetId, adsPort));
            return new(true, items, resolvedNetId, adsPort, "OK");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) {
            return new(false, Array.Empty<AdsBatchReadItem>(), resolvedNetId, adsPort,
                $"ReadAdsVariablesBatch failed: {ex.Message}");
        }
    }

    private static AdsBatchReadItem ReadBatchItem(AdsClient client, string symbolPath, string resolvedNetId, int adsPort)
    {
        var (sym, loadError) = LoadSymbol(client, symbolPath);
        if (sym is null)
        {
            var propertyResult = TryReadProperty(client, symbolPath, resolvedNetId, adsPort);
            return propertyResult is not null
                ? new(symbolPath, propertyResult.PlcType, propertyResult.Value, propertyResult.Success, propertyResult.Message)
                : new(symbolPath, null, null, false, loadError!);
        }

        string plcType = NormalizeTypeName(sym.TypeName);

        if (TryGetStringArrayCount(plcType, out int arrCount))
        {
            try { return new(symbolPath, plcType, ReadStringArray(client, symbolPath, sym, arrCount), true, "OK"); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                return new(symbolPath, plcType, null, false, $"[Error] Failed to read STRING array: {ex.Message}");
            }
        }

        if (plcType == "STRING")
        {
            try { return new(symbolPath, plcType, ReadScalarString(client, symbolPath, sym), true, "OK"); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                return new(symbolPath, plcType, null, false, $"[Error] Failed to read STRING: {ex.Message}");
            }
        }

        if (sym.DataType is IEnumType enumType)
        {
            string enumBase = NormalizeTypeName(enumType.BaseTypeName);
            Type? enumDotNetType = MapPlcType(enumBase);
            if (enumDotNetType is null)
                return new(symbolPath, plcType, null, false, $"[Error] Unsupported ENUM base type '{enumBase}' for '{plcType}'.");

            uint enumHandle = (uint)client.CreateVariableHandle(symbolPath);
            try
            {
                object rawValue = client.ReadAny(enumHandle, enumDotNetType);
                return new(symbolPath, plcType, ToEnumJsonValue(rawValue, enumType), true, "OK");
            }
            finally
            {
                try { client.DeleteVariableHandle(enumHandle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }

        Type? dotNetType = MapPlcType(plcType);
        if (dotNetType is null)
            return new(symbolPath, plcType, null, false, $"[Error] Unsupported PLC type '{plcType}'.");

        uint handle = (uint)client.CreateVariableHandle(symbolPath);
        try
        {
            object rawValue = client.ReadAny(handle, dotNetType);
            return new(symbolPath, plcType, ConvertToJson(rawValue, plcType), true, "OK");
        }
        finally
        {
            try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
    }

    public SignalTransitionResult MeasureSignalTransition(string startSymbol, string startEquals,
        string endSymbol, string endEquals, int timeoutMs, string? amsNetId, int adsPort)
    {
        string resolvedNetId = ResolveAmsNetId(amsNetId);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var client = new AdsClient();
            client.Connect(resolvedNetId, adsPort);

            // Wait for the start edge (A), then time until the end edge (B). The
            // whole operation is bounded by timeoutMs.
            if (!WaitForCondition(client, startSymbol, startEquals, timeoutMs, sw, out string? startErr))
                return new(startErr is null, sw.Elapsed.TotalMilliseconds, startErr is null,
                    startErr ?? $"Timed out waiting for start condition '{startSymbol}'=='{startEquals}'.");

            double tStart = sw.Elapsed.TotalMilliseconds;
            if (!WaitForCondition(client, endSymbol, endEquals, timeoutMs, sw, out string? endErr))
                return new(endErr is null, sw.Elapsed.TotalMilliseconds - tStart, endErr is null,
                    endErr ?? $"Timed out waiting for end condition '{endSymbol}'=='{endEquals}'.");

            return new(true, sw.Elapsed.TotalMilliseconds - tStart, false, "OK");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) {
            return new(false, 0, false, $"MeasureSignalTransition failed: {ex.Message}");
        }
    }

    public InspectFbInstanceResult InspectFbInstance(string instancePath, string? amsNetId, int adsPort)
    {
        string resolvedNetId = ResolveAmsNetId(amsNetId);
        try
        {
            using var client = new AdsClient();
            client.Connect(resolvedNetId, adsPort);

            var (sym, loadError) = LoadSymbol(client, instancePath);
            if (sym is null)
                return new(false, instancePath, null, Array.Empty<FbMemberEntry>(), Array.Empty<FbRpcMethodEntry>(),
                    resolvedNetId, adsPort, loadError!);

            var members = new List<FbMemberEntry>();
            var rpcMethods = new List<FbRpcMethodEntry>();

            if (sym is IStructInstance st)
            {
                foreach (ISymbol m in st.MemberInstances)
                {
                    int? offset = (m as IMember)?.Offset;
                    members.Add(new FbMemberEntry(m.InstanceName, m.InstancePath, m.IsProperty, m.TypeName, m.Size, offset));
                }
                foreach (var method in st.RpcMethods)
                {
                    rpcMethods.Add(new FbRpcMethodEntry(method.Name, RpcMethodExtension.IsVoid(method), method.Parameters?.Count ?? 0));
                }
            }

            return new(true, instancePath, sym.TypeName, members, rpcMethods, resolvedNetId, adsPort, "OK");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) {
            return new(false, instancePath, null, Array.Empty<FbMemberEntry>(), Array.Empty<FbRpcMethodEntry>(),
                resolvedNetId, adsPort, $"InspectFbInstance failed: {ex.Message}");
        }
    }

    // Polls a symbol until its string-rendered value equals expected (trim/ci),
    // or the overall stopwatch exceeds timeoutMs. Returns true on match; false on
    // timeout (error=null) or on a read error (error set).
    private static bool WaitForCondition(AdsClient client, string symbolPath, string expected,
        int timeoutMs, System.Diagnostics.Stopwatch sw, out string? error)
    {
        error = null;
        var (sym, loadError) = LoadSymbol(client, symbolPath);
        if (sym is null) { error = loadError; return false; }
        string plcType = NormalizeTypeName(sym.TypeName);
        // STRING has no marshal size for ReadAny — poll it as raw bytes instead
        // (same reason as ReadScalarString).
        bool isString = plcType == "STRING";
        int stringSize = isString ? (int)sym.Size : 0;
        Type? dotNetType = isString ? typeof(byte[]) : MapPlcType(plcType);
        if (dotNetType is null) { error = $"[Error] Unsupported PLC type '{plcType}'."; return false; }

        uint handle = (uint)client.CreateVariableHandle(symbolPath);
        try
        {
            string want = (expected ?? string.Empty).Trim();
            while (sw.Elapsed.TotalMilliseconds < timeoutMs)
            {
                object raw = isString
                    ? client.ReadAny(handle, typeof(byte[]), new[] { stringSize })
                    : client.ReadAny(handle, dotNetType);
                string have = isString
                    ? DecodePlcString((byte[])raw, 0, stringSize)
                    : Convert.ToString(ConvertToJson(raw, plcType),
                        System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (string.Equals(have.Trim(), want, StringComparison.OrdinalIgnoreCase))
                    return true;
                System.Threading.Thread.Sleep(5);
            }
            return false; // timed out (error stays null)
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { error = $"[Error] read failed: {ex.Message}"; return false; }
        finally { try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { } }
    }

    // ---- helpers -------------------------------------------------------------

    // The given NetId, "project" (the open solution's own target), or the local
    // AMS router's NetId (issue #202). The decision is AdsTargetNetId.Resolve,
    // outside #if TWINCAT so it is unit-tested.
    private static string ResolveAmsNetId(string? amsNetId) =>
        AdsTargetNetId.Resolve(amsNetId, LocalAmsNetIdResolver.Shared, ProjectTargetNetIdOrNull);

    // Reads TargetNetId from the open solution's .tsproj. Parsed from the file
    // rather than asked of COM, so it also works when the XAE is busy or absent.
    private static string? ProjectTargetNetIdOrNull()
    {
        try
        {
            string? sln = null;
            try { sln = (string)GetActiveTcDte().Solution.FullName; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            string? dir = sln is null ? null : Path.GetDirectoryName(sln);
            if (string.IsNullOrEmpty(dir)) return null;

            // A .tsproj opened directly sits in a generated wrapper solution elsewhere, so the solution's
            // own projects are asked for their paths as well.
            var candidates = Directory.EnumerateFiles(dir, "*.tsproj", SearchOption.AllDirectories)
                .Concat(SolutionTsprojPaths()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var tsproj in candidates)
            {
                var doc = System.Xml.Linq.XDocument.Load(tsproj);
                var attr = doc.Root?.Element("Project")?.Attribute("TargetNetId")
                        ?? doc.Descendants("Project").FirstOrDefault()?.Attribute("TargetNetId");
                string? value = attr?.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        return null;
    }

    private static List<string> SolutionTsprojPaths()
    {
        var paths = new List<string>();
        try
        {
            dynamic projects = GetActiveTcDte().Solution.Projects;
            int count = ComCall.ReadProp(() => (int)projects.Count, 0);
            for (int i = 1; i <= count; i++)
            {
                string? path = ComCall.ReadProp<string?>(() => (string?)projects.Item(i).FullName, null);
                if (path is not null && path.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    paths.Add(path);
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        return paths;
    }

    private static (ISymbol? sym, string? error) LoadSymbol(AdsClient client, string symbolPath)
    {
        // SymbolLoaderSettings.Default uses VirtualTree mode, which returns only 22 root
        // containers (GVLs, POUs) — not leaf variables. Flat mode returns all ~600+ exported
        // symbols including GVL members and PROGRAM variables. For FB member paths not in the
        // flat ADS symbol table (e.g. MAIN.fbMachine.Step), a second VirtualTree pass with
        // recursive child traversal resolves them via type-system expansion.
        try
        {
            var ct = System.Threading.CancellationToken.None;

            // Stage 1: flat list covers all directly exported symbols.
            var flatLoader = SymbolLoaderFactory.Create(client, new SymbolLoaderSettings(SymbolsLoadMode.Flat));
            var flatResult = flatLoader.GetSymbolsAsync(ct).GetAwaiter().GetResult();
            if (!flatResult.Succeeded)
                return (null, $"SymbolLoader failed: {flatResult.ErrorCode}");

            var sym = (flatResult.Symbols ?? Enumerable.Empty<ISymbol>())
                .FirstOrDefault(s => string.Equals(
                    s.InstancePath, symbolPath, StringComparison.OrdinalIgnoreCase));
            if (sym is not null) return (sym, null);

            // Stage 2: FB member not in flat table — traverse VirtualTree children.
            var treeLoader = SymbolLoaderFactory.Create(client, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree));
            var treeResult = treeLoader.GetSymbolsAsync(ct).GetAwaiter().GetResult();
            if (treeResult.Succeeded)
            {
                sym = FindSymbolRecursive(treeResult.Symbols ?? Enumerable.Empty<ISymbol>(), symbolPath);
                if (sym is not null) return (sym, null);
            }

            return (null, $"[Error] Symbol '{symbolPath}' not found in the PLC symbol table.");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) {
            return (null, $"SymbolLoader error: {ex.Message}");
        }
    }

    private static ISymbol? FindSymbolRecursive(IEnumerable<ISymbol> symbols, string symbolPath)
    {
        foreach (var s in symbols)
        {
            if (string.Equals(s.InstancePath, symbolPath, StringComparison.OrdinalIgnoreCase))
                return s;
            if (s is IStructInstance st)
            {
                var child = FindSymbolRecursive(st.MemberInstances, symbolPath);
                if (child is not null) return child;
            }
            if (s is IArrayInstance arr)
            {
                var child = FindSymbolRecursive(arr.Elements, symbolPath);
                if (child is not null) return child;
            }
        }
        return null;
    }

    private static bool IsInputMapped(ISymbol sym)
    {
        // AT %I* symbols implement IProcessImageAddress with an IndexGroup
        // that maps to MemoryArea.Input. Fail open on any exception —
        // never silently refuse a valid write target.
        try
        {
            if (sym is IProcessImageAddress addr)
            {
                var area = IndexGroupMemoryAreaExtension.GetArea(addr);
                return area == MemoryArea.Input;
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        return false;
    }

    // D-1 (issue #152): moved to AdsTypeNames (outside #if TWINCAT) so it is unit-testable
    // without the TwinCAT SDK; also fixes the namespace-strip eating the "ARRAY[...] OF "
    // prefix on a namespace-qualified array element type. See AdsTypeNames.NormalizeTypeName.
    private static string NormalizeTypeName(string typeName) => AdsTypeNames.NormalizeTypeName(typeName);

    private static bool TryGetStringArrayCount(string plcType, out int count) =>
        AdsTypeNames.TryGetStringArrayCount(plcType, out count);

    // A scalar STRING(n) occupies n+1 bytes (declared length + NUL) and the
    // symbol info knows that size — but ReadAny(handle, typeof(string)) throws
    // "Cannot marshal dotnet type 'String' because its marshal size is unknown!"
    // because the .NET type alone carries no length. So do what ReadStringArray
    // already does per element: read sym.Size raw bytes and cut at the first NUL.
    private static string ReadScalarString(AdsClient client, string symbolPath, ISymbol sym)
    {
        uint handle = (uint)client.CreateVariableHandle(symbolPath);
        try
        {
            int size = (int)sym.Size;
            byte[] raw = (byte[])client.ReadAny(handle, typeof(byte[]), new[] { size });
            return DecodePlcString(raw, 0, size);
        }
        finally
        {
            try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
    }

    // Writes a scalar STRING into its fixed-size, zero-padded slot and returns the
    // bytes written. D-4 (issue #152): Windows-1252 (TwinCAT's single-byte STRING),
    // not UTF-8; unrepresentable characters are refused before anything is written.
    private static byte[] WriteScalarString(AdsClient client, string symbolPath, ISymbol sym, string value)
    {
        byte[] raw = PlcStringCodec.Encode(value, (int)sym.Size);

        uint handle = (uint)client.CreateVariableHandle(symbolPath);
        try
        {
            client.WriteAny(handle, raw);
        }
        finally
        {
            try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
        return raw;
    }

    // Raw bytes of a symbol (the read-back of a STRING / STRING-array write).
    private static byte[] ReadRaw(AdsClient client, string symbolPath, int size)
    {
        uint handle = (uint)client.CreateVariableHandle(symbolPath);
        try { return (byte[])client.ReadAny(handle, typeof(byte[]), new[] { size }); }
        finally
        {
            try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
    }

    private const string OverwrittenHint =
        " (the ADS write returned without error; if the PLC program assigns this variable every cycle, it overwrites the value)";

    // Encoding and NUL handling live in PlcStringCodec — outside the #if TWINCAT
    // region and free of ADS types, so that decision is testable without a
    // running PLC (see PlcStringDecodeTests).
    private static string DecodePlcString(byte[] raw, int offset, int count)
        => PlcStringCodec.Decode(raw, offset, count);

    // Reads the whole array as a raw byte block (sym.Size covers all elements)
    // and splits it into fixed-size, null-terminated STRING slots. Avoids
    // needing the per-element declared length, which NormalizeTypeName strips.
    private static string[] ReadStringArray(AdsClient client, string symbolPath, ISymbol sym, int count)
    {
        uint handle = (uint)client.CreateVariableHandle(symbolPath);
        try
        {
            int totalSize = (int)sym.Size;
            byte[] raw = (byte[])client.ReadAny(handle, typeof(byte[]), new[] { totalSize });
            int elemSize = count > 0 ? totalSize / count : 0;
            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = DecodePlcString(raw, i * elemSize, elemSize);
            }
            return result;
        }
        finally
        {
            try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
    }

    // Expects `json` to be a JSON array of strings, e.g. ["a","b","c"], with
    // exactly `count` elements. Encodes each into its fixed-size, zero-padded
    // slot and writes the whole array as one raw byte block.
    private static byte[] WriteStringArray(AdsClient client, string symbolPath, ISymbol sym, int count, string json)
    {
        string[]? values = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
        if (values is null)
            throw new InvalidOperationException("Value must be a JSON array of strings.");
        if (values.Length != count)
            throw new InvalidOperationException($"Expected {count} elements, got {values.Length}.");

        int totalSize = (int)sym.Size;
        int elemSize = count > 0 ? totalSize / count : 0;
        var raw = new byte[totalSize];
        for (int i = 0; i < count; i++)
        {
            // D-4 (issue #152): Windows-1252, refused per element before anything is written.
            byte[] slot;
            try { slot = PlcStringCodec.Encode(values[i], elemSize); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Element {i} ('{values[i]}'): {ex.Message}", ex);
            }
            Array.Copy(slot, 0, raw, i * elemSize, elemSize);
        }

        uint handle = (uint)client.CreateVariableHandle(symbolPath);
        try
        {
            client.WriteAny(handle, raw);
        }
        finally
        {
            try { client.DeleteVariableHandle(handle); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
        return raw;
    }

    private static Type? MapPlcType(string plcType) => plcType switch
    {
        "BOOL" or "BIT" => typeof(bool),
        "BYTE" or "USINT" => typeof(byte),
        "WORD" or "UINT" => typeof(ushort),
        "DWORD" or "UDINT" => typeof(uint),
        "LWORD" or "ULINT" => typeof(ulong),
        "SINT" => typeof(sbyte),
        "INT" => typeof(short),
        "DINT" => typeof(int),
        "LINT" => typeof(long),
        "REAL" => typeof(float),
        "LREAL" => typeof(double),
        "STRING" => typeof(string),
        _ => null,
    };

    private static object ConvertToJson(object value, string plcType)
    {
        // Ensure float precision doesn't produce unexpected JSON (NaN / Infinity)
        if (value is float f) return float.IsFinite(f) ? (object)f : f.ToString(CultureInfo.InvariantCulture);
        if (value is double d) return double.IsFinite(d) ? (object)d : d.ToString(CultureInfo.InvariantCulture);
        return value;
    }

    private static object? ConvertFromJson(string raw, string plcType, Type dotNetType)
    {
        try
        {
            if (dotNetType == typeof(bool))
                return AdsBoolValue.TryParse(raw, out bool b) ? b : null;
            if (dotNetType == typeof(string))
                return raw;
            if (dotNetType == typeof(float))
                return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (dotNetType == typeof(double))
                return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            return Convert.ChangeType(raw, dotNetType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) {
            return null;
        }
    }

    // Pairs the raw underlying numeric value with its symbolic member name
    // (null when the value doesn't match any declared member — e.g. a
    // reserved/out-of-range value written by something other than this enum).
    private static EnumAdsValue ToEnumJsonValue(object rawValue, IEnumType enumType)
    {
        long numeric = Convert.ToInt64(rawValue, System.Globalization.CultureInfo.InvariantCulture);
        string? name = enumType.EnumValues
            .FirstOrDefault(ev => Convert.ToInt64(ev.Value, System.Globalization.CultureInfo.InvariantCulture) == numeric)
            ?.Name;
        return new EnumAdsValue(name, rawValue);
    }

    // Accepts either a declared member name (optionally JSON-quoted, e.g.
    // '"E_STATE_RUNNING"' or 'E_STATE_RUNNING') or a plain numeric string.
    // Returns null when neither resolves, so the caller can report a clear error.
    private static object? ConvertEnumFromJson(string raw, IEnumType enumType, Type dotNetType)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1];

        try
        {
            if (enumType.TryParse(trimmed, out IEnumValue? byName))
                return Convert.ChangeType(byName.Value, dotNetType, System.Globalization.CultureInfo.InvariantCulture);

            return Convert.ChangeType(trimmed, dotNetType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) {
            return null;
        }
    }
#endif
}
