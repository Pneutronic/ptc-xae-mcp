namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // sbom: MDP inventory of the target IPC. Without an explicit NetId the open solution's own target is used.
    public IpcInventoryResult ReadIpcInventory(string? amsNetId, IReadOnlyList<int>? moduleTypes, bool includeRaw, int timeoutMs)
    {
        string netId = AdsTargetNetId.Resolve(
            string.IsNullOrWhiteSpace(amsNetId) ? AdsTargetNetId.ProjectKeyword : amsNetId,
            LocalAmsNetIdResolver.Shared, ProjectTargetNetIdOrNull);
        var filter = moduleTypes is null ? null : moduleTypes.ToHashSet();
#if TWINCAT
        using var transport = new AdsRawClient(netId, MdpAddress.AmsPort, timeoutMs);
        return new MdpInventoryReader(transport).Read(netId, filter, includeRaw);
#else
        throw TwinCatNotBuilt.Exception();
#endif
    }

    // sbom: online identities of the slaves of EtherCAT masters. The tree walk (projected boxes, master address) is
    // the only COM part; the ADS reads and the join are EtherCatIdentityReader.
    public EtherCatIdentitiesResult ReadEtherCatIdentities(string? masterTreePath, string? amsNetId, int? adsPort, bool includeCrc, bool includeCoe, bool includeDeviceDetails, int timeoutMs)
    {
        var masters = ComBusyRetry.Run(() => CollectEtherCatMasters(masterTreePath), OpenTimeout);
#if TWINCAT
        var reader = new EtherCatIdentityReader((netId, port) => new AdsRawClient(netId, port, timeoutMs));
        return reader.ReadAll(masters, amsNetId, adsPort, includeCrc, includeCoe, includeDeviceDetails);
#else
        throw TwinCatNotBuilt.Exception();
#endif
    }

    private const int MaxTreeDepth = 24;

    private static List<EtherCatMasterConfig> CollectEtherCatMasters(string? masterTreePath)
    {
        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        var nodes = new List<object>();

        if (!string.IsNullOrWhiteSpace(masterTreePath))
        {
            dynamic? node = LookupTreeItemOrNull(sysMan, masterTreePath.Trim())
                ?? throw new InvalidOperationException($"Tree path not found: {masterTreePath}");
            if (ComCall.ReadProp(() => (int)node.ItemSubType, -1) != EtherCatMasterSubType)
                throw new InvalidOperationException($"'{masterTreePath}' is not an EtherCAT master device (ItemSubType {EtherCatMasterSubType}).");
            nodes.Add(node);
        }
        else
        {
            dynamic? tiid = LookupTreeItemOrNull(sysMan, "TIID");
            if (tiid is not null)
                foreach (object child in Children(tiid))
                    if (ComCall.ReadProp(() => (int)((dynamic)child).ItemSubType, -1) == EtherCatMasterSubType)
                        nodes.Add(child);
        }

        var result = new List<EtherCatMasterConfig>();
        foreach (dynamic master in nodes)
        {
            string name = NameOf(master);
            string path = ComCall.ReadProp(() => (string?)master.PathName ?? name, name);
            string xml = ComCall.ReadProp(() => (string?)master.ProduceXml(false) ?? string.Empty, string.Empty);
            var (netId, port, source) = EtherCatProjection.ParseMasterAddress(xml);
            if (netId is not null)
            {
                // The master NetId of a project is relative to its target (0.0.0.0.2.1): the target's first four octets complete it.
                string? resolved = EtherCatProjection.ApplyTargetPrefix(netId, ProjectTargetNetIdOrNull(), out bool relative);
                if (relative)
                    source = resolved is null
                        ? $"{source} (relative NetId {netId}, but no target NetId is known)"
                        : $"{source} (relative, target prefix)";
                netId = resolved;
            }
            var slaves = EtherCatProjection.CollectSlaves(new ComProjectionNode(master), MaxTreeDepth);
            bool complete = netId is not null && port is not null;
            result.Add(new EtherCatMasterConfig(path, name, netId, port, source, complete ? null : EtherCatProjection.Snippet(xml), slaves));
        }
        return result;
    }

    // Adapter of a COM tree node for EtherCatProjection.CollectSlaves.
    private sealed class ComProjectionNode(object node) : EtherCatProjection.IProjectionNode
    {
        public string Name => NameOf(node);

        public string Path => ComCall.ReadProp(() => (string?)((dynamic)node).PathName ?? Name, Name);

        public string Xml => ComCall.ReadProp(() => (string?)((dynamic)node).ProduceXml(false) ?? string.Empty, string.Empty);

        public IEnumerable<EtherCatProjection.IProjectionNode> Children =>
            TwinCatComSession.Children(node).Select(c => (EtherCatProjection.IProjectionNode)new ComProjectionNode(c));
    }
}
