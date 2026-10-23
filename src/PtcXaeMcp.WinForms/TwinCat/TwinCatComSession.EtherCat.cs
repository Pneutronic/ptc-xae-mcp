using System.Xml;
using PtcXaeMcp.WinForms.TwinCat.Tree;

namespace PtcXaeMcp.WinForms.TwinCat;

// ethercat-topology-edit: granular add / remove / set-previous-port for
// EtherCAT boxes. Uses the same proven COM mechanics as the apply-schema
// runner (CreateChild ItemSubType 9099 + EtherCatVInfo candidate fallback +
// ProduceXml/ConsumeXml PreviousPort patch), exposed as discrete operations.
internal sealed partial class TwinCatComSession
{
    // ItemSubType for top-level EtherCAT devices (Infosys 242737035):
    // master = 111, slave = 130. Boxes/terminals use 9099 (EtherCatVInfo).
    private const int EtherCatMasterSubType = 111;
    private const int EtherCatSlaveSubType = 130;

    public string AddEtherCatDevice(string name, string deviceKind) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("device name is required.");

        int subType = (deviceKind?.Trim().ToLowerInvariant()) switch
        {
            null or "" or "master" => EtherCatMasterSubType,
            "slave" => EtherCatSlaveSubType,
            _ => throw new InvalidOperationException(
                $"deviceKind '{deviceKind}' is invalid — expected 'master' or 'slave'."),
        };

        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic? tiid = LookupTreeItemOrNull(sysMan, "TIID")
            ?? throw new InvalidOperationException("TIID (I/O devices) node not found.");

        string deviceName = name.Trim();
        string kindWord = subType == EtherCatMasterSubType ? "master" : "slave";
        // D-15 (issue #152): an existing device of that name is an error, never "Created"; every
        // call on the seam retries busy locally, so the CreateChild is never replayed.
        var tree = new ComSysManTree(sysMan, CancellationToken.None);
        string path = new EtherCatNodeAdder(tree, ApplyPreviousPortOnNode)
            .AddDevice(new ComTreeNode(tree, (object)tiid), deviceName, subType, kindWord);

        SaveAllAfterMutation(dte);
        return $"Created EtherCAT {kindWord} '{path}' and saved the project.";
    }, OpenTimeout);

    public string AddEtherCatBox(string parentPath, EtherCatBoxSpec box, string? beforeSibling)
        => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            throw new InvalidOperationException("parentPath is required.");
        if (box is null || string.IsNullOrWhiteSpace(box.VInfo))
            throw new InvalidOperationException("box vInfo is required.");

        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic? parent = LookupTreeItemWithFallback(sysMan, parentPath)
            ?? throw new InvalidOperationException($"Tree path not found: {parentPath}");

        // D-15 (issue #152): snapshot-before-create — an existing node is an error, not "Added",
        // and its children are not created again underneath it.
        var tree = new ComSysManTree(sysMan, CancellationToken.None);
        int created = new EtherCatNodeAdder(tree, ApplyPreviousPortOnNode, vInfo => EsiInstallHint.For(vInfo))
            .AddBox(new ComTreeNode(tree, (object)parent), box, beforeSibling);
        SaveAllAfterMutation(dte);
        string word = created == 1 ? "node" : "nodes";
        return $"Added {created} EtherCAT {word} under '{parentPath}' and saved the project.";
    }, OpenTimeout);

    public string RemoveEtherCatBox(string treePath) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(treePath))
            throw new InvalidOperationException("treePath is required.");

        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic? node = LookupTreeItemWithFallback(sysMan, treePath)
            ?? throw new InvalidOperationException($"Tree path not found: {treePath}");

        int caret = treePath.LastIndexOf('^');
        if (caret <= 0)
            throw new InvalidOperationException(
                $"'{treePath}' has no parent — refusing to delete a top-level node.");
        string parentPath = treePath[..caret];
        string leaf = treePath[(caret + 1)..];

        dynamic? parent = LookupTreeItemWithFallback(sysMan, parentPath)
            ?? throw new InvalidOperationException($"Parent tree path not found: {parentPath}");

        try { parent.DeleteChild(leaf); }
        // Busy is not "the parent rejects the name": it propagates instead of triggering the fallback.
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            // Some XAE versions index EtherCAT children differently — fall back
            // to the node's own Delete().
            try { node.Delete(); }
            catch (Exception ex2)
            {
                throw new InvalidOperationException(
                    $"Delete failed for '{treePath}': {ex.Message} / {ex2.Message}", ex2);
            }
        }

        // Read-back: the removed path must no longer resolve (fresh lookup, local busy retry).
        bool stillThere = AfterMutation(() => LookupTreeItemWithFallback(sysMan, treePath) is not null);
        if (stillThere)
            throw new InvalidOperationException(
                $"Remove of EtherCAT node '{treePath}' did not land: the path still resolves after the delete. Nothing was saved.");
        SaveAllAfterMutation(dte);
        return $"Removed EtherCAT node '{treePath}' and saved the project (read-back: the path no longer resolves).";
    }, OpenTimeout);

    public string SetEtherCatPreviousPort(string treePath, string port) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(treePath))
            throw new InvalidOperationException("treePath is required.");
        if (string.IsNullOrWhiteSpace(port))
            throw new InvalidOperationException("port is required.");

        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic? node = LookupTreeItemWithFallback(sysMan, treePath)
            ?? throw new InvalidOperationException($"Tree path not found: {treePath}");

        // Disambiguate duplicate port letters by the upstream (parent) box's
        // station address, derived from the tree path. On a junction (EK1122)
        // several upstream devices expose the same letter; without this we could
        // wire the box to the wrong link.
        string upstreamPhysAddr = string.Empty;
        int caretIdx = treePath.LastIndexOf('^');
        if (caretIdx > 0)
        {
            dynamic? parentNode = LookupTreeItemWithFallback(sysMan, treePath[..caretIdx]);
            if (parentNode is not null) upstreamPhysAddr = ReadStationAddress(parentNode);
        }

        ApplyPreviousPortXml(node, port.Trim(), treePath, upstreamPhysAddr);
        SaveAllAfterMutation(dte);
        return $"Set previous port '{port.Trim()}' on '{treePath}' and saved the project.";
    }, OpenTimeout);

    // ---- internals ---------------------------------------------------------

    // PreviousPort for a node created on the seam (EtherCatNodeAdder): same ProduceXml → select →
    // ConsumeXml → read-back as set_ethercat_previous_port.
    // The box exists at this point, so a busy XAE is retried here (the select/ConsumeXml is
    // idempotent) instead of replaying the whole add.
    private static void ApplyPreviousPortOnNode(ITreeNode slave, string port, string itemName, string upstreamPhysAddr) =>
        AfterMutation<object?>(() => { ApplyPreviousPortXml(slave.Raw, port, itemName, upstreamPhysAddr); return null; });

    /// <summary>
    /// File.SaveAll after a mutation (#171 audit, issue #152): a busy XAE here is retried locally —
    /// letting it escape to the method's ComBusyRetry.Run would replay the mutation before it.
    /// </summary>
    private static void SaveAllAfterMutation(dynamic dte)
    {
        object d = dte;
        AfterMutation<object?>(() => { ((dynamic)d).ExecuteCommand("File.SaveAll"); return null; });
    }

    // ProduceXml → select the matching <PreviousPort> → ConsumeXml. Throws so
    // a deliberate caller gets a clear [Error] when the port is unavailable
    // (apply-schema's runner warns-and-continues; here the user asked for it).
    private static void ApplyPreviousPortXml(
        dynamic slaveItem, string desiredPort, string itemName, string upstreamPhysAddr = "")
    {
        if (string.IsNullOrWhiteSpace(desiredPort))
            throw new InvalidOperationException("previousPort is empty.");

        string xml = (string)slaveItem.ProduceXml();
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        XmlNodeList? previousPorts = doc.SelectNodes("/TreeItem/EtherCAT/Slave/PreviousPort");
        if (previousPorts is null || previousPorts.Count == 0)
            throw new InvalidOperationException(
                $"'{itemName}' has no <PreviousPort> entries — it is not a port-attached "
                + "EtherCAT slave (e.g. a coupler/master has no upstream port to select).");

        // A junction (EK1122) exposes the same port letter on several upstream
        // devices, so the candidate list has duplicate letters keyed by
        // <PhysAddr>. Clear every selection, then pick exactly one entry: the one
        // matching the upstream box's station address when known, otherwise the
        // first letter match. Selecting more than one corrupts the topology.
        XmlElement? chosen = null;
        XmlElement? firstLetterMatch = null;
        var available = new List<string>();
        foreach (XmlNode portNode in previousPorts)
        {
            portNode.Attributes?.RemoveNamedItem("Selected");

            if (portNode is not XmlElement element) continue;

            string port = element["Port"]?.InnerText?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(port)) available.Add(port);

            if (!string.Equals(port, desiredPort, StringComparison.OrdinalIgnoreCase)) continue;

            firstLetterMatch ??= element;

            string physAddr = element["PhysAddr"]?.InnerText?.Trim() ?? string.Empty;
            if (chosen is null &&
                !string.IsNullOrWhiteSpace(upstreamPhysAddr) &&
                string.Equals(physAddr, upstreamPhysAddr, StringComparison.OrdinalIgnoreCase))
            {
                chosen = element;
            }
        }

        chosen ??= firstLetterMatch;
        if (chosen is null)
        {
            string list = available.Count == 0 ? "<none>" : string.Join(", ", available);
            throw new InvalidOperationException(
                $"Port '{desiredPort}' is not available for '{itemName}'. Available: {list}.");
        }

        chosen.SetAttribute("Selected", "1");
        slaveItem.ConsumeXml(doc.OuterXml);

        // D-23 / #171 (issue #152): ConsumeXml writes had no read-back verification at all —
        // a swallowed failure would report success while the port stayed unchanged. Verify via
        // a fresh ProduceXml, and do that read in its own AfterMutation scope: a busy XAE
        // during the read-back must settle locally, not bubble to the caller's outer
        // ComBusyRetry.Run and replay this ConsumeXml.
        string? selectedAfter = AfterMutation(() =>
        {
            string afterXml = (string)slaveItem.ProduceXml();
            var afterDoc = new XmlDocument();
            afterDoc.LoadXml(afterXml);
            // ProduceXml reports the selection as Selected="true" (live, TC 4026.22) although
            // ConsumeXml is fed Selected="1" — accept both (same rule as ParseSelectedPreviousPort).
            return afterDoc.SelectSingleNode(
                "/TreeItem/EtherCAT/Slave/PreviousPort[@Selected='1' or @Selected='true']/Port")?.InnerText?.Trim();
        });
        if (!string.Equals(selectedAfter, desiredPort, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"ConsumeXml did not persist PreviousPort '{desiredPort}' on '{itemName}' — " +
                $"read-back shows '{selectedAfter ?? "<none selected>"}'.");
    }

    // EtherCAT station address (EtherCAT/Slave/Info/PhysAddr) of a box; "" when
    // absent (master/device level). Used to disambiguate PreviousPort letters.
    private static string ReadStationAddress(dynamic item)
    {
        try
        {
            string xml = (string)item.ProduceXml(false);
            if (string.IsNullOrWhiteSpace(xml)) return string.Empty;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc.SelectSingleNode("/TreeItem/EtherCAT/Slave/Info/PhysAddr")?.InnerText?.Trim()
                ?? string.Empty;
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return string.Empty; }
    }

    private static dynamic? FindChildByName(dynamic parent, string name)
    {
        int count = 0;
        try { count = (int)parent.ChildCount; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
        // Walk newest-first: a freshly created child is appended last.
        for (int i = count; i >= 1; i--)
        {
            dynamic? child;
            try { child = parent.Child(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
            if (child is null) continue;
            string childName = string.Empty;
            try { childName = (string)child.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (string.Equals(childName, name, StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    // CreateChild may hand back a single item or a 1-element object[].
    private static dynamic? UnwrapSingle(object? raw)
    {
        if (raw is null) return null;
        if (raw is object[] arr)
            return arr.Length == 1 ? arr[0] : (arr.Length == 0 ? null : arr);
        return raw;
    }
}
