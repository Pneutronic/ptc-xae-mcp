using System.Xml;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // The tree access (device XML of the box: ADS address and, for the comparison, its Startup list) is the only COM
    // part; the ADS reads and the comparison are SoeParameterReader.
    public SoeParameterReadResult ReadSoeParameters(
        string treePath, IReadOnlyList<int>? idns, int? driveNo, IReadOnlyList<int> elements, int timeoutMs)
    {
        var (xml, targetNetId) = ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object sysMan = RequireSystemManager(dte);
            dynamic? node = LookupTreeItemOrNull(sysMan, treePath)
                ?? throw new InvalidOperationException($"Tree path not found: {treePath}");
            string produced = ComCall.ReadProp(() => (string?)node.ProduceXml(false) ?? string.Empty, string.Empty);
            return (produced, ProjectTargetNetIdOrNull());
        }, OpenTimeout);

        var (rawNetId, port) = EtherCatProjection.ParseSlaveAddress(xml);
        if (rawNetId is null || port is null)
            throw new InvalidOperationException(
                $"'{treePath}' has no usable ADS address (EtherCAT/AmsAddress) in its device XML — is it an EtherCAT box?");
        string netId = EtherCatProjection.ApplyTargetPrefix(rawNetId, targetNetId, out _)
            ?? throw new InvalidOperationException(
                $"The master NetId {rawNetId} of '{treePath}' is relative to the target and no target NetId is known.");

        IReadOnlyList<EtherCatStartupEntry> entries = [];
        if (idns is null)
        {
            var doc = new XmlDocument();
            try { doc.LoadXml(xml); }
            catch (XmlException ex) { throw new InvalidOperationException($"ProduceXml for '{treePath}' is not well-formed: {ex.Message}", ex); }
            entries = EtherCatStartupParser.Parse(treePath, treePath, doc, includeFixed: true, EtherCatStartupProtocol.SoE)?.Entries
                ?? throw new InvalidOperationException($"'{treePath}' is not an EtherCAT box (no EtherCAT/Slave/Info section).");
        }

#if TWINCAT
        using var client = new AdsRawClient(netId, port.Value, timeoutMs);
        var reader = new SoeParameterReader(new RetryOnTimeoutReader(client), $"{netId}:{port}");
        return idns is null
            ? new SoeParameterReadResult(treePath, netId, port.Value, SoeParameterMode.Compare, reader.Compare(entries, driveNo))
            : new SoeParameterReadResult(treePath, netId, port.Value, SoeParameterMode.Read, reader.ReadAll(idns, driveNo ?? 0, elements));
#else
        throw TwinCatNotBuilt.Exception();
#endif
    }
}
