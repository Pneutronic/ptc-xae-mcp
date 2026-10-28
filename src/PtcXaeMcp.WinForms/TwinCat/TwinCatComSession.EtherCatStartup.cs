namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // The Startup list is read from the nodes' ProduceXml only (the saved .xti keeps SoE/EoE as binary blobs); the XML
    // handling is EtherCatStartupParser / EtherCatStartupReader, this is the tree access.
    public EtherCatStartupResult ReadEtherCatStartup(string? treePath, bool includeFixed, string? protocol) =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object sysMan = RequireSystemManager(dte);

            if (!string.IsNullOrWhiteSpace(treePath))
            {
                dynamic? node = LookupTreeItemOrNull(sysMan, treePath.Trim())
                    ?? throw new InvalidOperationException($"Tree path not found: {treePath}");
                var box = EtherCatStartupReader.ParseNode(new ComProjectionNode((object)node), includeFixed, protocol)
                    ?? throw new InvalidOperationException(
                        $"'{treePath}' is not an EtherCAT box (its ProduceXml has no EtherCAT/Slave/Info section).");
                return new EtherCatStartupResult([box], 1);
            }

            var masters = new List<EtherCatProjection.IProjectionNode>();
            dynamic? tiid = LookupTreeItemOrNull(sysMan, "TIID");
            if (tiid is not null)
                foreach (object child in Children(tiid))
                    if (ComCall.ReadProp(() => (int)((dynamic)child).ItemSubType, -1) == EtherCatMasterSubType)
                        masters.Add(new ComProjectionNode(child));
            return EtherCatStartupReader.ReadAll(masters, includeFixed, protocol, MaxTreeDepth);
        }, OpenTimeout);
}
