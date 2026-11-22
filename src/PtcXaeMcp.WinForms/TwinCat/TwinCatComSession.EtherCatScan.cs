using System.Runtime.ExceptionServices;
using PtcXaeMcp.WinForms.TwinCat.Tree;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- ethercat-scan -----------------------------------------------------------
    //
    // The XAE "Scan" of an EtherCAT device in config mode, without touching the configured device: a temporary
    // EtherCAT master on the same adapter (AddressInfo copied from the target) receives ConsumeXml with
    // <ScanBoxes>1</ScanBoxes>, its boxes are read like those of the target (EtherCatProjection.CollectSlaves),
    // and the temporary device is deleted again. Nothing is saved. Infosys: tc3_automationinterface 242737035
    // (creating EtherCAT devices), 21840348171 (device scan, AddressInfo), 21840453387 (ScanBoxes via ConsumeXml).
    //
    // Every call after CreateChild goes through AfterMutation so a busy XAE is retried locally: letting it
    // escape to ComBusyRetry.Run would replay the CreateChild and leave a second temporary device behind.

    public EtherCatScanData ScanEtherCatMaster(string masterTreePath) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(masterTreePath))
            throw new InvalidOperationException("masterTreePath is required.");

        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic target = LookupTreeItemOrNull(sysMan, masterTreePath)
            ?? throw new InvalidOperationException($"Tree path not found: {masterTreePath}");
        if (ComCall.ReadProp(() => (int)target.ItemSubType, -1) != EtherCatMasterSubType)
            throw new InvalidOperationException(
                $"'{masterTreePath}' is not an EtherCAT master device (ItemSubType {EtherCatMasterSubType}).");
        dynamic tiid = LookupTreeItemOrNull(sysMan, "TIID")
            ?? throw new InvalidOperationException("TIID (I/O devices) node not found.");

        string targetXml;
        try { targetXml = (string)target.ProduceXml(false); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException($"ProduceXml failed on '{masterTreePath}': {ex.Message}", ex);
        }
        var (addressInfo, addressError) = EtherCatScanXml.ExtractAddressInfo(targetXml);
        if (addressInfo is null) throw new InvalidOperationException(addressError);

        var configured = EtherCatScanChain.From(
            EtherCatProjection.CollectSlaves(new ComProjectionNode(target), MaxTreeDepth));

        string tempName = EtherCatScanXml.TemporaryDeviceName();
        var tree = new ComSysManTree(sysMan, CancellationToken.None);
        string tempPath = new EtherCatNodeAdder(tree, ApplyPreviousPortOnNode)
            .AddDevice(new ComTreeNode(tree, (object)tiid), tempName, EtherCatMasterSubType, "master");

        List<EtherCatScanBox>? scanned = null;
        Exception? failure = null;
        string? cleanupError = null;
        try
        {
            dynamic temp = AfterMutation(() => LookupTreeItemOrNull(sysMan, tempPath))
                ?? throw new InvalidOperationException($"The temporary scan device '{tempPath}' could not be resolved after creating it.");
            string scanXml = EtherCatScanXml.BuildScanXml(addressInfo);
            AfterMutation<object?>(() => { temp.ConsumeXml(scanXml); return null; });
            scanned = AfterMutation(() => EtherCatScanChain.From(
                EtherCatProjection.CollectSlaves(
                    new ComProjectionNode(LookupTreeItemOrNull(sysMan, tempPath)
                        ?? throw new InvalidOperationException($"The temporary scan device '{tempPath}' disappeared during the scan.")),
                    MaxTreeDepth)));
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            failure = ex; // rethrown below, after the temporary device is removed
        }
        finally
        {
            // Also when a busy XAE escapes: the device goes first, so the retry of the call starts clean.
            cleanupError = RemoveTemporaryScanDevice(sysMan, tiid, tempName, tempPath);
        }

        if (cleanupError is not null)
            throw new InvalidOperationException(failure is null
                ? cleanupError
                : $"{failure.Message} In addition: {cleanupError}", failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        return new EtherCatScanData(masterTreePath, configured, scanned!);
    }, OpenTimeout);

    // Deletes the temporary device and checks it is gone. Null when it is gone; otherwise the text that names it.
    private static string? RemoveTemporaryScanDevice(object sysMan, dynamic tiid, string name, string path)
    {
        string? problem = null;
        try
        {
            try { AfterMutation<object?>(() => { tiid.DeleteChild(name); return null; }); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                // Some XAE versions index EtherCAT children differently: fall back to the node's own Delete().
                dynamic? node = AfterMutation(() => LookupTreeItemOrNull(sysMan, path));
                if (node is not null) AfterMutation<object?>(() => { node.Delete(); return null; });
            }
            if (AfterMutation(() => LookupTreeItemOrNull(sysMan, path) is not null))
                problem = "the path still resolves after the delete";
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            problem = ex.Message;
        }
        return problem is null
            ? null
            : $"The temporary scan device '{name}' ({path}) could not be removed: {problem}. " +
              "Delete it by hand in the I/O tree and do not save the project before that.";
    }
}
