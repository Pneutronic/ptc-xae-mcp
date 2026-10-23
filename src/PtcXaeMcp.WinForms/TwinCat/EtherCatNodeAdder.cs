using System.Xml;
using PtcXaeMcp.WinForms.TwinCat.Apply;
using PtcXaeMcp.WinForms.TwinCat.Tree;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// The create steps of twincat_add_ethercat_device / twincat_add_ethercat_box on the
/// <see cref="ISysManTree"/> seam (issue #152, D-15). Before, a CreateChild that threw
/// "already exists" fell through to a name lookup that adopted the PRE-EXISTING node, reported it
/// as "Added" and created its children a second time underneath. Now:
/// <list type="bullet">
/// <item>the target path is looked up BEFORE CreateChild — a node that is already there is an
/// error, nothing is created;</item>
/// <item>a CreateChild that throws is only "adopted" when the node at the exact new path matches
/// the requested name and vInfo (E-19 rule of the schema runner) — never another vInfo next to it;</item>
/// <item>when a later step fails, the error lists every node this call already created.</item>
/// <item>issue #190: once a node exists, a busy XAE never leaves this class un-final — it becomes a
/// <see cref="XaeBlockedException"/>, so the caller's outer busy retry cannot replay the add and
/// report its own node as "already exists".</item>
/// </list>
/// </summary>
internal sealed class EtherCatNodeAdder
{
    private readonly ISysManTree _tree;
    private readonly Action<ITreeNode, string, string, string> _applyPreviousPort;
    private readonly Func<string, string?>? _esiHint;
    private readonly List<string> _created = [];

    // Issue #190: the path of a CreateChild that was issued but whose node is not verified yet.
    // From the moment CreateChild is called the tree may have changed, so a busy XAE is no longer
    // replayable — even before the node made it into _created.
    private string? _unverified;

    private bool MayHaveMutated => _created.Count > 0 || _unverified is not null;

    /// <param name="applyPreviousPort">(slave, port, itemName, upstreamPhysAddr): selects and verifies the PreviousPort.</param>
    /// <param name="esiHint">vInfo → sentence on the ESI for that type (<see cref="EsiInstallHint.For"/>), added to the
    /// message when no box can be created; null or a null answer adds nothing.</param>
    public EtherCatNodeAdder(ISysManTree tree, Action<ITreeNode, string, string, string> applyPreviousPort,
        Func<string, string?>? esiHint = null)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _applyPreviousPort = applyPreviousPort ?? throw new ArgumentNullException(nameof(applyPreviousPort));
        _esiHint = esiHint;
    }

    /// <summary>Paths created by this instance, in creation order.</summary>
    public IReadOnlyList<string> Created => _created;

    /// <summary>Creates a top-level I/O device (EtherCAT master/slave) under TIID. Returns its path.</summary>
    public string AddDevice(ITreeNode tiid, string name, int subType, string kindWord)
    {
        try { return AddDeviceCore(tiid, name, subType, kindWord); }
        catch (Exception ex) when (MayHaveMutated && ComCall.IsBusy(ex))
        {
            throw NotReplayable(ex);
        }
    }

    private string AddDeviceCore(ITreeNode tiid, string name, int subType, string kindWord)
    {
        string path = ChildPath(tiid, name);
        if (_tree.Lookup(path) is not null)
            throw new InvalidOperationException(
                $"An I/O device named '{name}' already exists at '{path}' — nothing was created. " +
                "Pick another name or use the existing device.");

        ITreeNode created;
        _unverified = path;
        try { created = tiid.CreateChild(name, subType, null, null); }
        catch (Exception ex) when (ComCall.Recoverable(ex))
        {
            // The throw does not prove nothing was added: only a node at the exact new path counts.
            created = _tree.Lookup(path) ?? throw new InvalidOperationException(
                $"CreateChild failed for EtherCAT {kindWord} '{name}' (subType {subType}): {ex.Message}", ex);
        }
        ITreeNode node = Verified(created, path, name);
        _created.Add(node.Path.Length > 0 ? node.Path : path);
        _unverified = null;
        return _created[^1];
    }

    /// <summary>Creates <paramref name="spec"/> and its subtree under <paramref name="parent"/>. Returns the node count.</summary>
    public int AddBox(ITreeNode parent, EtherCatBoxSpec spec, string? beforeSibling)
    {
        try { return AddBoxRecursive(parent, spec, beforeSibling); }
        catch (Exception ex) when (MayHaveMutated && ComCall.IsBusy(ex))
        {
            throw NotReplayable(ex);
        }
        catch (Exception ex) when (_created.Count > 0 && ComCall.Recoverable(ex))
        {
            throw new InvalidOperationException(
                $"{ex.Message} Already created by this call: {string.Join(", ", _created)} — these nodes exist in " +
                "the project now (the project was NOT saved); remove them with twincat_remove_ethercat_box if unwanted.", ex);
        }
    }

    private int AddBoxRecursive(ITreeNode parent, EtherCatBoxSpec spec, string? beforeSibling)
    {
        string name = string.IsNullOrWhiteSpace(spec.Name) ? spec.VInfo : spec.Name!;
        string path = ChildPath(parent, name);
        if (_tree.Lookup(path) is not null)
            throw new InvalidOperationException(
                $"An EtherCAT node named '{name}' already exists at '{path}' — it was not created by this call and " +
                "is not adopted, so nothing was added for it. Remove it first or give the new box another name.");

        _unverified = path;
        ITreeNode created = CreateBox(parent, name, path, spec.VInfo, beforeSibling);
        _created.Add(created.Path.Length > 0 ? created.Path : path);
        _unverified = null;

        if (!string.IsNullOrWhiteSpace(spec.PreviousPort))
            _applyPreviousPort(created, spec.PreviousPort!.Trim(), name, ReadStationAddress(parent));

        int count = 1;
        foreach (var child in spec.ChildList)
            count += AddBoxRecursive(created, child, null);
        return count;
    }

    private ITreeNode CreateBox(ITreeNode parent, string name, string path, string vInfo, string? beforeSibling)
    {
        Exception? firstError = null;
        foreach (string candidate in EtherCatVInfo.GetCreateChildVInfoCandidates(vInfo))
        {
            ITreeNode created;
            try { created = parent.CreateChild(name, EtherCatVInfo.BoxItemSubType, beforeSibling ?? string.Empty, candidate); }
            catch (Exception ex) when (ComCall.Recoverable(ex))
            {
                firstError ??= ex;
                // E-19: a throw does not prove nothing was added — look before trying another vInfo.
                ITreeNode? leftBehind = _tree.Lookup(path);
                if (leftBehind is null) continue;
                string revision = ReadSlaveInfo(leftBehind, "ProductRevision");
                if (string.Equals(leftBehind.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    EtherCatTopologyBuilder.RevisionMatchesVInfo(revision, candidate))
                    return leftBehind;
                throw new InvalidOperationException(
                    $"CreateChild '{name}' [vInfo={candidate}] reported an error ({ex.Message}) and left a node at '{path}' " +
                    $"that does not match (ProductRevision '{revision}'). No other vInfo was tried, so nothing was duplicated.", ex);
            }
            return Verified(created, path, name);
        }

        string? esi = _esiHint?.Invoke(vInfo);
        throw new InvalidOperationException(
            $"CreateChild failed for EtherCAT box '{name}' (vInfo='{vInfo}') under '{parent.Path}': " +
            $"{firstError?.Message ?? "no child produced"}. " + (esi is null ? "" : esi + " ") +
            "HINT: the ESI/device description for this vInfo may be " +
            "missing, or TwinCAT accepts only the base vInfo without a revision suffix.", firstError);
    }

    /// <summary>
    /// Issue #190: a busy XAE after this call created something. Replaying the add would find the
    /// node it just created and report "already exists", so the busy turns final here and names
    /// what already exists.
    /// </summary>
    private XaeBlockedException NotReplayable(Exception busy)
    {
        string reason = ComCall.Describe(busy, static () => null);
        string created = _created.Count > 0 ? string.Join(", ", _created) : "nothing verified";
        string unverified = _unverified is null ? "" : $" (possibly also '{_unverified}': its CreateChild was issued, the node is not verified)";
        return new XaeBlockedException(
            $"{reason} Already created by this call: {created}{unverified} — these nodes may exist in the project " +
            "now (the project was NOT saved); the add was not repeated. Check them with twincat_get_fieldbus_topology " +
            "and remove them with twincat_remove_ethercat_box if unwanted.",
            busy is XaeBlockedException b ? b.Dialog : null, busy);
    }

    // CreateChild answered with some item: only the node at the exact new path, with the requested name, counts.
    private ITreeNode Verified(ITreeNode created, string path, string name)
    {
        if (string.Equals(created.Name, name, StringComparison.OrdinalIgnoreCase)) return created;
        ITreeNode? byPath = _tree.Lookup(path);
        if (byPath is not null && string.Equals(byPath.Name, name, StringComparison.OrdinalIgnoreCase)) return byPath;
        throw new InvalidOperationException(
            $"CreateChild '{name}' returned '{created.Path}', and '{path}' does not resolve to the new node.");
    }

    private static string ChildPath(ITreeNode parent, string name)
    {
        string parentPath = parent.Path;
        if (parentPath.Length == 0)
            throw new InvalidOperationException($"The parent of '{name}' has no readable tree path.");
        return $"{parentPath}^{name}";
    }

    /// <summary>EtherCAT station address of a box; "" for a master/device or unreadable XML.</summary>
    public static string ReadStationAddress(ITreeNode node) => ReadSlaveInfo(node, "PhysAddr");

    private static string ReadSlaveInfo(ITreeNode node, string field)
    {
        try
        {
            string xml = node.ProduceXml(false);
            if (string.IsNullOrWhiteSpace(xml)) return string.Empty;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc.SelectSingleNode($"/TreeItem/EtherCAT/Slave/Info/{field}")?.InnerText?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ComCall.Recoverable(ex))
        {
            return string.Empty;
        }
    }
}
