using System.Xml;
using System.Text.RegularExpressions;
using PtcXaeMcp.WinForms.TwinCat.Schema;
using PtcXaeMcp.WinForms.TwinCat.Tree;

namespace PtcXaeMcp.WinForms.TwinCat.Apply;

/// <summary>
/// Builds the schema's EtherCAT topology under a resolved master on the <see cref="ISysManTree"/>
/// seam (issue #155 step 5, R5). Every node that cannot be created is recorded as a
/// <see cref="MissingEthercatNodeInfo"/> (its subtree as consequence-skipped); the caller turns a
/// direct failure into a failed apply (E-4, <see cref="ApplyReport"/>).
///
/// E-19: CreateChild sometimes throws although TwinCAT added the box. The runner used to try the
/// next alternate vInfo right away, which could add the same box twice. Now, after a thrown
/// CreateChild, the child is looked up at its exact path first: a node that is there and matches
/// (name, ProductRevision against the vInfo just tried) is adopted and no other vInfo is tried; a
/// node that is there but does not match is a failure (never a second CreateChild next to it).
/// A node that already exists at the target path BEFORE the first CreateChild is not adopted
/// either — this run did not create it.
/// </summary>
internal sealed class EtherCatTopologyBuilder
{
    private sealed record PendingPreviousPort(string ItemPath, string ItemName, string PreviousPort, string UpstreamPhysAddr);

    private sealed record CreateOutcome(ITreeNode? Node, string Error)
    {
        public static CreateOutcome Ok(ITreeNode node) => new(node, string.Empty);
        public static CreateOutcome Fail(string error) => new(null, error);
    }

    private readonly ISysManTree _tree;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<string, IReadOnlyList<string>>? _esiIdentities;
    private readonly Dictionary<string, string> _createdPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MissingEthercatNodeInfo> _missing = [];
    private readonly List<PendingPreviousPort> _pending = [];

    /// <param name="esiIdentities">vInfo → identity vInfos of a bare third-party Type
    /// (<see cref="EsiCatalogScanner.ThirdPartyIdentityVInfos"/>), tried after the vInfo itself (#52).</param>
    public EtherCatTopologyBuilder(ISysManTree tree, Action<string>? info = null, Action<string>? warn = null,
        CancellationToken cancellationToken = default, Func<string, IReadOnlyList<string>>? esiIdentities = null)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _info = info ?? (_ => { });
        _warn = warn ?? (_ => { });
        _cancellationToken = cancellationToken;
        _esiIdentities = esiIdentities;
    }

    /// <summary>
    /// Tree path of every node created (or adopted) in this run, by schema node name, plus a
    /// <c>__KEY__</c>+<see cref="LinkCandidates.TerminalNameKey"/> entry for designation lookups.
    /// </summary>
    public IReadOnlyDictionary<string, string> CreatedPaths => _createdPaths;

    /// <summary>Nodes that were not created: direct failures and their consequence-skipped subtrees.</summary>
    public IReadOnlyList<MissingEthercatNodeInfo> MissingNodes => _missing;

    /// <summary>Creates <paramref name="root"/> (and its subtree) under <paramref name="master"/>.</summary>
    public void Build(ITreeNode master, EthercatNode root)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(root);
        _pending.Clear();
        BuildNode(master, "TIID", root, root.Name);
        if (_pending.Count > 0)
        {
            _info("Applying pending PreviousPort selections (EK1122 junctions)...");
            foreach (var request in _pending)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ITreeNode? item = _tree.Lookup(request.ItemPath);
                if (item is null)
                {
                    _warn($"Tree item for PreviousPort '{request.PreviousPort}' not found: '{request.ItemPath}'");
                    continue;
                }
                SetPreviousPort(item, request);
            }
        }
    }

    /// <summary>
    /// A schema root such as "CX5140" under TIID is the IPC itself, not an EtherCAT box: its
    /// children are created directly under the master.
    /// </summary>
    internal static bool ShouldFlattenPseudoRootNode(string parentVInfo, EthercatNode node) =>
        string.Equals(parentVInfo, "TIID", StringComparison.OrdinalIgnoreCase) && IsCxPseudoNode(node.VInfo);

    internal static bool IsCxPseudoNode(string? vInfo) =>
        Regex.IsMatch(vInfo ?? string.Empty, @"^CX\d{3,4}(?:-[A-Za-z0-9]+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when a box's ProductRevision (e.g. "EL1008-0000-0018") is what CreateChild with
    /// <paramref name="vInfo"/> produces: the same revision, or any revision of the same type when
    /// <paramref name="vInfo"/> is the bare type ("EL1008"). Pure.
    /// </summary>
    internal static bool RevisionMatchesVInfo(string productRevision, string vInfo)
    {
        string rev = productRevision.Trim();
        string want = vInfo.Trim();
        if (rev.Length == 0 || want.Length == 0) return false;
        if (string.Equals(rev, want, StringComparison.OrdinalIgnoreCase)) return true;
        return rev.StartsWith(want + "-", StringComparison.OrdinalIgnoreCase);
    }

    internal static string CombineSchemaPath(string parentSchemaPath, string nodeName)
    {
        if (string.IsNullOrWhiteSpace(parentSchemaPath)) return nodeName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(nodeName)) return parentSchemaPath;
        return parentSchemaPath + "^" + nodeName;
    }

    /// <summary>Logs the end-of-run summary of missing nodes (nothing when all were created).</summary>
    public void LogMissingSummary(Action<string> step)
    {
        if (_missing.Count == 0) return;

        var direct = _missing.Where(n => n.IsDirectFailure).ToList();
        var skipped = _missing.Where(n => !n.IsDirectFailure).ToList();

        step("Missing EtherCAT devices");
        _warn($"Summary: failed directly: {direct.Count}. Skipped as a consequence: {skipped.Count}.");
        for (int i = 0; i < direct.Count; i++)
        {
            var n = direct[i];
            string parentPath = string.IsNullOrWhiteSpace(n.ParentPath) ? "<unknown>" : n.ParentPath;
            _warn($"[{i + 1}] '{n.Name}' [vInfo={n.VInfo}] | Parent='{n.ParentName}' | ParentPath='{parentPath}' | Schema='{n.SchemaPath}' | Reason: {n.Reason}");
        }

        if (skipped.Count == 0) return;
        _warn("Consequence nodes:");
        int groupIndex = 1;
        foreach (var group in MissingNodeReport.GroupSkipped(direct, skipped))
        {
            string examples = string.Join(", ", group.Nodes.Take(3).Select(n => $"'{n.Name}'"));
            if (group.Nodes.Count > 3) examples += $" (+{group.Nodes.Count - 3} more)";
            string cause = group.Cause is null ? "unknown cause" : $"'{group.Cause.Name}' [vInfo={group.Cause.VInfo}]";
            _warn($"[{groupIndex++}] Cause={cause} | Consequence nodes={group.Nodes.Count} | Examples: {examples}");
        }
    }

    // ---- recursion -----------------------------------------------------------------------

    private void BuildNode(ITreeNode parent, string parentVInfo, EthercatNode node, string schemaPath)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        parent = Fresh(parent);

        if (ShouldFlattenPseudoRootNode(parentVInfo, node))
        {
            _info($"Schema root '{node.Name}' [vInfo={node.VInfo}] is not created as an EtherCAT node; its children are created directly under '{parent.Name}'.");
            CacheCreatedPath(node.Name, parent.Path);
            foreach (var child in node.Children)
                BuildNode(parent, node.VInfo, child, CombineSchemaPath(schemaPath, child.Name));
            return;
        }

        ITreeNode createParent = ResolveCreateParent(parent, node);
        _info($"CreateChild '{node.Name}' [vInfo={node.VInfo}] under '{createParent.Name}'");
        CreateOutcome outcome = CreateBox(createParent, node);
        if (outcome.Node is null)
        {
            RecordMissing(createParent.Name, createParent.Path, node, schemaPath, outcome.Error, isDirectFailure: true);
            RecordSkippedDescendants(node, schemaPath);
            return;
        }

        ITreeNode item = outcome.Node;
        string path = item.Path;
        CacheCreatedPath(node.Name, path);

        if (!string.IsNullOrWhiteSpace(node.PreviousPort))
        {
            if (string.IsNullOrWhiteSpace(path))
                _warn($"PreviousPort '{node.PreviousPort}' for '{node.Name}' cannot be queued: the tree item has no readable path.");
            else
                _pending.Add(new PendingPreviousPort(path, node.Name, node.PreviousPort, ReadStationAddress(parent)));
        }

        foreach (var child in node.Children)
            BuildNode(item, node.VInfo, child, CombineSchemaPath(schemaPath, child.Name));
    }

    private CreateOutcome CreateBox(ITreeNode parent, EthercatNode node)
    {
        string parentPath = parent.Path;
        string childPath = parentPath.Length == 0 ? string.Empty : $"{parentPath}^{node.Name}";

        // Not ours to adopt: a node of that name was there before this run touched the parent.
        if (childPath.Length > 0 && _tree.Lookup(childPath) is not null)
            return CreateOutcome.Fail(
                $"A node named '{node.Name}' already exists at '{childPath}' before CreateChild; it was not created by this run " +
                "and is not adopted. Use an empty master or rename the schema node.");

        Exception? firstError = null;
        string[] candidates = EtherCatVInfo.GetCreateChildVInfoCandidates(node.VInfo, _esiIdentities);
        for (int i = 0; i < candidates.Length; i++)
        {
            string candidate = candidates[i];
            if (i > 0)
                _warn($"CreateChild with vInfo '{node.VInfo}' failed; retrying with substitute vInfo '{candidate}'.");

            ITreeNode created;
            try
            {
                created = parent.CreateChild(node.Name, EtherCatVInfo.BoxItemSubType, null, candidate);
            }
            catch (Exception ex) when (ComCall.Recoverable(ex))
            {
                firstError ??= ex;
                // E-19: the throw does not prove nothing was added. Look before trying another vInfo.
                ITreeNode? leftBehind = childPath.Length == 0 ? null : _tree.Lookup(childPath);
                if (leftBehind is null) continue;

                string? mismatch = VerifyAdopted(leftBehind, node.Name, candidate);
                if (mismatch is null)
                {
                    _warn($"CreateChild '{node.Name}' [vInfo={candidate}] reported an error ({Flatten(ex)}), but the box exists at " +
                          $"'{childPath}' and matches name and vInfo; adopted it, no other vInfo tried.");
                    return CreateOutcome.Ok(leftBehind);
                }

                return CreateOutcome.Fail(
                    $"CreateChild '{node.Name}' [vInfo={candidate}] reported an error ({Flatten(ex)}) and left a node at '{childPath}' " +
                    $"that does not match ({mismatch}). No other vInfo was tried, so the box is not duplicated; " +
                    "check the node in the XAE (ESI / revision).");
            }

            if (NameMatches(created, node.Name)) return CreateOutcome.Ok(created);

            // CreateChild answered with some other item; only the exact path counts.
            ITreeNode? byPath = childPath.Length == 0 ? null : _tree.Lookup(childPath);
            if (byPath is not null && NameMatches(byPath, node.Name)) return CreateOutcome.Ok(byPath);
            return CreateOutcome.Fail(
                $"CreateChild '{node.Name}' [vInfo={candidate}] returned '{created.Path}', and '{childPath}' does not resolve to the new box.");
        }

        return CreateOutcome.Fail(
            $"CreateChild failed: parent='{parent.Name}' path='{parentPath}' child='{node.Name}' vInfo='{node.VInfo}'. " +
            $"COM: {Flatten(firstError)}. HINT: the device description (ESI) for this vInfo is missing, or TwinCAT accepts " +
            "only the base vInfo without a revision suffix.");
    }

    private static bool NameMatches(ITreeNode node, string name) =>
        string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase);

    // Null when the node left behind by a thrown CreateChild is the box that was asked for.
    private static string? VerifyAdopted(ITreeNode node, string name, string vInfo)
    {
        if (!NameMatches(node, name)) return $"name '{node.Name}', expected '{name}'";
        string revision = ReadProductRevision(node);
        if (revision.Length == 0) return "its ProductRevision cannot be read, so its vInfo cannot be verified";
        return RevisionMatchesVInfo(revision, vInfo)
            ? null
            : $"ProductRevision '{revision}' does not match vInfo '{vInfo}'";
    }

    private static string ReadProductRevision(ITreeNode node) =>
        ReadSlaveInfo(node, "ProductRevision");

    private static string ReadStationAddress(ITreeNode node) =>
        ReadSlaveInfo(node, "PhysAddr");

    // EtherCAT/Slave/Info/<field> of a box; "" when the node has none (e.g. the master) or the
    // XML cannot be read.
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

    private ITreeNode ResolveCreateParent(ITreeNode parent, EthercatNode node)
    {
        if (string.IsNullOrWhiteSpace(node.PreviousPort)) return parent;

        string port = node.PreviousPort.Trim().ToUpperInvariant();
        ITreeNode? best = null;
        int bestScore = 0;
        foreach (ITreeNode child in parent.Children())
        {
            int score = LinkCandidates.ScoreEthernetBranchPortCandidate(child.Name, child.Path, port);
            if (score > bestScore)
            {
                best = child;
                bestScore = score;
            }
        }

        if (best is not null)
        {
            _info($"Using Ethernet port '{best.Name}' for '{node.Name}' [vInfo={node.VInfo}] with previousPort '{node.PreviousPort}'.");
            return Fresh(best);
        }

        // Couplers/junctions (EK1100, EK1122, ...) expose no separate "Ethernet port" item: the
        // upstream link is selected later through the <PreviousPort> XML, so creating the child
        // directly under the parent is the normal path here.
        _info($"No separate Ethernet port node for previousPort '{node.PreviousPort}' under '{parent.Name}'; creating the child " +
              $"directly under the parent (PreviousPort is set via XML). Children: {DescribeChildren(parent)}");
        return parent;
    }

    private void SetPreviousPort(ITreeNode slave, PendingPreviousPort request)
    {
        string desiredPort = request.PreviousPort.Trim();
        if (desiredPort.Length == 0) return;

        var doc = new XmlDocument();
        doc.LoadXml(slave.ProduceXml());
        XmlNodeList? previousPorts = doc.SelectNodes("/TreeItem/EtherCAT/Slave/PreviousPort");
        if (previousPorts is null || previousPorts.Count == 0)
        {
            _warn($"No <PreviousPort> nodes found for '{request.ItemName}'.");
            return;
        }

        var elements = new List<XmlElement>();
        var candidates = new List<PreviousPortSelector.PortCandidate>();
        foreach (XmlNode xmlNode in previousPorts)
        {
            xmlNode.Attributes?.RemoveNamedItem("Selected");
            if (xmlNode is not XmlElement element) continue;
            elements.Add(element);
            candidates.Add(new PreviousPortSelector.PortCandidate(
                element["Port"]?.InnerText?.Trim() ?? string.Empty,
                element["PhysAddr"]?.InnerText?.Trim() ?? string.Empty));
        }

        PreviousPortSelector.Selection selection = PreviousPortSelector.Select(candidates, desiredPort, request.UpstreamPhysAddr);
        if (selection.ChosenIndex is null)
        {
            _warn($"{selection.Warning} (terminal '{request.ItemName}')");
            return;
        }
        if (selection.Warning is not null)
            _warn($"Port '{desiredPort}' for '{request.ItemName}': {selection.Warning}");

        elements[selection.ChosenIndex.Value].SetAttribute("Selected", "1");
        slave.ConsumeXml(doc.OuterXml);
        _info($"PreviousPort '{desiredPort}' set for '{request.ItemName}'" +
              (request.UpstreamPhysAddr.Length == 0 ? string.Empty : $" (upstream PhysAddr '{request.UpstreamPhysAddr}')"));
    }

    // The node at its path, freshly resolved (a cached COM reference can miss children added
    // after it was read); the node itself when its path is unreadable or no longer resolves.
    private ITreeNode Fresh(ITreeNode node)
    {
        string path = node.Path;
        return path.Length == 0 ? node : _tree.Lookup(path) ?? node;
    }

    private static string DescribeChildren(ITreeNode parent)
    {
        try
        {
            var names = parent.Children().Take(8).Select(c => string.IsNullOrWhiteSpace(c.Name) ? $"<empty:{c.SubType}>" : c.Name).ToList();
            return names.Count == 0 ? "<none>" : string.Join(", ", names);
        }
        catch (Exception ex) when (ComCall.Recoverable(ex))
        {
            return $"<unreadable: {ex.Message}>";
        }
    }

    private void CacheCreatedPath(string itemName, string path)
    {
        if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(path)) return;
        _createdPaths[itemName] = path;
        string key = LinkCandidates.TerminalNameKey(itemName);
        if (key.Length > 0) _createdPaths["__KEY__" + key] = path;
    }

    private void RecordMissing(string parentName, string parentPath, EthercatNode node, string schemaPath, string reason, bool isDirectFailure)
    {
        _missing.Add(new MissingEthercatNodeInfo
        {
            Name = node.Name,
            VInfo = node.VInfo,
            ParentName = parentName,
            ParentPath = parentPath,
            SchemaPath = schemaPath,
            Reason = reason,
            IsDirectFailure = isDirectFailure,
        });
        string kind = isDirectFailure ? "Missing" : "Skipped";
        _warn($"{kind}: '{node.Name}' [vInfo={node.VInfo}] under '{parentName}' (schema '{schemaPath}'). Reason: {reason}");
    }

    private void RecordSkippedDescendants(EthercatNode parentNode, string parentSchemaPath)
    {
        foreach (var child in parentNode.Children)
            RecordSkippedSubtree(child, parentNode, parentSchemaPath);
    }

    private void RecordSkippedSubtree(EthercatNode node, EthercatNode missingParent, string missingParentSchemaPath)
    {
        string schemaPath = CombineSchemaPath(missingParentSchemaPath, node.Name);
        RecordMissing(missingParent.Name, missingParentSchemaPath, node, schemaPath,
            $"Skipped because parent node '{missingParent.Name}' was not created.", isDirectFailure: false);
        foreach (var child in node.Children)
            RecordSkippedSubtree(child, node, schemaPath);
    }

    private static string Flatten(Exception? ex)
    {
        if (ex is null) return "<no COM details>";
        Exception current = ex;
        while (current is System.Reflection.TargetInvocationException { InnerException: not null } tie)
            current = tie.InnerException!;
        return string.IsNullOrWhiteSpace(current.Message) ? current.GetType().Name : current.Message;
    }
}
