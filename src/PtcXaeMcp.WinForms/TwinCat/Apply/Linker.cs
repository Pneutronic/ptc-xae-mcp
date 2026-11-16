using PtcXaeMcp.WinForms.TwinCat.Schema;
using PtcXaeMcp.WinForms.TwinCat.Tree;
using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat.Apply;

/// <summary>Where the linker links: the resolved PLC and EtherCAT master of one apply run.</summary>
/// <param name="Strict">Fail-closed mode (terminal resolution): an ambiguous terminal name is an error, not a pick.</param>
internal sealed record LinkTarget(string PlcName, string MasterPath, bool Strict);

/// <summary>
/// Outcome of <see cref="Linker.LinkAll"/>. <paramref name="NotLinked"/> counts the points without a
/// <c>symbol</c> (topology only); they are neither linked nor a failure.
/// </summary>
internal sealed record LinkOutcome(List<LinkResult> Links, List<string> Unresolved, bool HadUnresolvedFailure, int NotLinked = 0);

/// <summary>
/// Links the schema's IO points on the <see cref="ISysManTree"/> seam (issue #155 step 6, R8).
/// <list type="bullet">
/// <item>E-3: the PLC side of a point is the exact process-image symbol its <c>symbol</c> names
/// (<c>&lt;instance&gt;^&lt;task&gt; Inputs/Outputs^&lt;symbol&gt;</c>), looked up flat in the exact image
/// folders of the resolved PLC's instance, <c>I</c> only in input folders and <c>Q</c> only in output
/// folders. An array element is a flat name too (<c>MAIN.aCyl[1].bIn</c>); there is no prefix or suffix
/// matching, so <c>aCyl[1]</c> never reaches <c>aCyl[10]</c> or <c>aCyl[1].bIn2</c>. Two hits are an
/// ambiguity error, never a pick. A point without a symbol is not linked (topology only).</item>
/// <item>E-17: both sides are resolved by lookup and <c>LinkVariables</c> is called exactly once per
/// point (it used to double as the probe and then run a second time).</item>
/// <item>E-16: the master subtree (terminal names) and the task list are read once per run and
/// cached, instead of 200k–300k-node walks per IO point.</item>
/// </list>
/// </summary>
internal sealed class Linker
{
    /// <summary>Node budget for the one-time master-subtree name index.</summary>
    internal const int MasterIndexLimit = 200_000;

    private readonly ISysManTree _tree;
    private readonly LinkTarget _target;
    private readonly IReadOnlyDictionary<string, string> _schemaTerminalPaths;
    private readonly IReadOnlyDictionary<string, string> _createdPaths;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly CancellationToken _cancellationToken;

    private readonly Dictionary<string, string> _plcPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ioddCheckedTerminals = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>>? _masterNameIndex;
    private List<(string Path, string Direction)>? _imageFolderCache;

    public Linker(
        ISysManTree tree,
        LinkTarget target,
        EthercatNode schemaRoot,
        IReadOnlyDictionary<string, string>? createdPaths = null,
        Action<string>? info = null,
        Action<string>? warn = null,
        CancellationToken cancellationToken = default)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _schemaTerminalPaths = BuildSchemaTerminalPathIndex(schemaRoot, target.MasterPath);
        _createdPaths = createdPaths ?? new Dictionary<string, string>();
        _info = info ?? (_ => { });
        _warn = warn ?? (_ => { });
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Links every point. A point that cannot be resolved or linked is collected with its reason;
    /// <paramref name="failIfUnresolved"/> decides whether that fails the run (E-11).
    /// </summary>
    public LinkOutcome LinkAll(IReadOnlyList<IoPoint> points, bool failIfUnresolved)
    {
        var links = new List<LinkResult>();
        var unresolved = new List<string>();
        int notLinked = 0;
        foreach (var point in points)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string direction = point.Direction.Trim().ToUpperInvariant();
            if (!point.HasSymbol)
            {
                // Topology only (D2): the point builds hardware, nothing is linked, nothing fails.
                notLinked++;
                _info($"Not linked (no symbol): {point.Label} ({point.TerminalName} Ch{point.Channel} {direction})");
                continue;
            }

            try
            {
                string ioPath = ResolveIoLeafPath(point);
                string plcPath = ResolvePlcSymbolPath(point.Symbol!, direction);
                _tree.LinkVariables(plcPath, ioPath);
                _info($"Linked: {point.Label} <-> {ioPath}");
                links.Add(new LinkResult
                {
                    VarName = point.Label,
                    Symbol = point.Symbol!.Trim(),
                    Direction = direction,
                    Type = string.IsNullOrWhiteSpace(point.Type) ? "BOOL" : point.Type!,
                    Terminal = point.TerminalName,
                    Channel = point.Channel,
                    Group = point.Group ?? string.Empty,
                    Description = point.Description ?? string.Empty,
                    PlcPath = plcPath,
                    IoPath = ioPath,
                });
            }
            catch (Exception ex) when (ComCall.Recoverable(ex))
            {
                string reason = $"{point.Label} ({point.TerminalName} Ch{point.Channel} {direction}): {ex.Message}";
                unresolved.Add(reason);
                _warn(failIfUnresolved
                    ? $"Not resolved (failIfUnresolved=true, reported at the end of the run): {reason}"
                    : $"Not resolved, skipped (failIfUnresolved=false): {reason}");
            }
        }

        _info($"Link summary: OK={links.Count}  skipped={unresolved.Count}  notLinked={notLinked}");
        return new LinkOutcome(links, unresolved, failIfUnresolved && unresolved.Count > 0, notLinked);
    }

    // ---- PLC side --------------------------------------------------------------------------

    /// <summary>
    /// Exact process-image path of <paramref name="symbol"/> (a full PLC instance path, e.g.
    /// <c>MAIN.fbCyl.bIn</c> or <c>MAIN.aCyl[1].bIn</c>) for <paramref name="direction"/>: one flat lookup
    /// <c>&lt;folder&gt;^&lt;symbol&gt;</c> per image folder of that direction; only when that misses, the
    /// member of a STRUCT-typed AT variable as a child node (<c>MAIN.fbX.stIn.bB</c> →
    /// <c>&lt;folder&gt;^MAIN.fbX.stIn^bB</c>). Throws naming every folder tried when it is not there (or
    /// is there twice).
    /// </summary>
    internal string ResolvePlcSymbolPath(string symbol, string direction)
    {
        symbol = symbol.Trim();
        string dir = direction.Trim().ToUpperInvariant();
        string cacheKey = $"{symbol}|{dir}";
        if (_plcPathCache.TryGetValue(cacheKey, out string? cached)) return cached;

        string? invalid = PlcSymbolPath.Check(symbol);
        if (invalid is not null)
            throw new InvalidOperationException($"PLC symbol '{symbol}' {invalid}.");

        List<string> folders = ImageFolders(dir);
        List<string> hits = LookupAll(folders, [symbol]);
        // A member of a STRUCT-typed AT variable is a child node of the flat variable
        // (<folder>^MAIN.fbX.stIn^bB), not a flat name; tried only when the flat name is not there.
        if (hits.Count == 0)
            hits = LookupAll(folders, PlcSymbolPath.StructMemberCandidates(symbol));

        if (hits.Count > 1)
            throw new InvalidOperationException(
                $"PLC variable '{symbol}' is ambiguous in the process image: {string.Join(", ", hits.Select(h => $"'{h}'"))}. Nothing was linked.");
        if (hits.Count == 1) return _plcPathCache[cacheKey] = hits[0];

        string tried = folders.Count == 0 ? "<no image folder found>" : string.Join(", ", folders.Select(f => $"'{f}'"));
        string arrayHint = symbol.Contains('[', StringComparison.Ordinal)
            ? " An array element is looked up as written, flat (e.g. 'MAIN.aCyl[1].bIn'; two indexes as '[2,1]' without blanks)."
            : string.Empty;
        string structHint = symbol.Contains('.', StringComparison.Ordinal)
            ? " A member of a STRUCT-typed AT variable was also tried as a child node ('MAIN.fbX.stIn.bB' as 'MAIN.fbX.stIn^bB')."
            : string.Empty;
        throw new InvalidOperationException(
            $"PLC symbol '{symbol}' not found in the {(dir == "Q" ? "output" : "input")} process image of PLC '{_target.PlcName}' (tried {tried}). " +
            "Declare it AT %I* / AT %Q* with the direction the point states, build the PLC " +
            "(twincat_build_plc), then apply again. A same-named member of another instance is never used." + arrayHint + structHint);
    }

    // Exact lookups of every spelling in every folder; distinct tree paths of the nodes found.
    private List<string> LookupAll(List<string> folders, IReadOnlyList<string> spellings) =>
        folders
            .SelectMany(folder => spellings.Select(spelling => _tree.Lookup($"{folder}^{spelling}")))
            .Where(node => node is not null)
            .Select(node => node!.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // "<instance>^<task> Inputs"/"... Outputs" folders that exist, for every task under TIRT and
    // every instance of the resolved PLC. Read once per run (E-16).
    private List<string> ImageFolders(string direction)
    {
        _imageFolderCache ??= ReadImageFolders();
        string dir = direction == "Q" ? "Q" : "I";
        return _imageFolderCache.Where(f => f.Direction == dir).Select(f => f.Path).ToList();
    }

    private List<(string Path, string Direction)> ReadImageFolders()
    {
        var tasks = new List<string>();
        ITreeNode? tirt = _tree.Lookup("TIRT");
        if (tirt is not null)
            tasks.AddRange(SafeChildren(tirt).Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
        if (!tasks.Contains("PlcTask", StringComparer.OrdinalIgnoreCase)) tasks.Add("PlcTask");

        string plcPath = $"TIPC^{_target.PlcName}";
        var instances = new List<string> { $"{plcPath}^{_target.PlcName} Instance" };
        ITreeNode? plc = _tree.Lookup(plcPath);
        if (plc is not null)
            instances.AddRange(SafeChildren(plc)
                .Where(c => c.Name.EndsWith(" Instance", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Path.Length > 0 ? c.Path : $"{plcPath}^{c.Name}"));

        var folders = new List<(string Path, string Direction)>();
        foreach (string instance in instances.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string task in tasks.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string dir in new[] { "I", "Q" })
                    foreach (string name in LinkCandidates.ImageFolderNames(task, dir))
                    {
                        string path = $"{instance}^{name}";
                        if (!folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) &&
                            _tree.Lookup(path) is not null)
                            folders.Add((path, dir));
                    }
        return folders;
    }

    // Children, or none when the child count cannot be read (a genuine failure, not busy).
    private static IEnumerable<ITreeNode> SafeChildren(ITreeNode node)
    {
        try { return node.Children().ToList(); }
        catch (Exception ex) when (ComCall.Recoverable(ex)) { return []; }
    }

    // ---- IO side ---------------------------------------------------------------------------

    /// <summary>The IO leaf path of <paramref name="point"/>; throws naming every path tried.</summary>
    internal string ResolveIoLeafPath(IoPoint point)
    {
        if (point.IsIoLink) return ResolveIoLinkLeafPath(point);

        var tried = new List<string>();
        string? explicitLeaf = ResolveExplicitIoLeaf(point, tried);
        if (explicitLeaf is not null) return explicitLeaf;

        foreach (string candidate in SchemaLeafCandidates(point.TerminalName, point.Channel, point.Direction))
        {
            if (Probe(candidate, tried)) return candidate;
        }

        ITreeNode terminal = FindTerminal(point.TerminalName);
        var direct = new List<string>();
        LinkCandidates.AddIoCandidates(direct, terminal.Path, point.Channel, point.Direction);
        foreach (string candidate in direct)
        {
            if (Probe(candidate, tried)) return candidate;
        }

        string? leaf = FindIoLeafUnder(terminal, point.Channel, point.Direction, point.IoLeaf ?? string.Empty);
        if (leaf is not null) return leaf;

        throw new InvalidOperationException(
            $"IO leaf not found: terminal '{terminal.Path}', channel {point.Channel}, direction {point.Direction}. " +
            $"None of these paths exists: {string.Join(", ", tried.Select(t => $"'{t}'"))}. " +
            "Channel variables of a terminal are not listed as its tree children, only addressable by path — check " +
            "the channel number, or pass the leaf name as ioLeaf.");
    }

    /// <summary>
    /// An <c>ioLeaf</c> that is a tree path is looked up exactly, before any channel-name candidate:
    /// absolute (<c>TIID^…</c>) as written, otherwise relative to the terminal
    /// (<c>"AI Standard Channel 1^Value"</c> → <c>&lt;terminal&gt;^AI Standard Channel 1^Value</c>). Channel
    /// variables are not listed as tree children, so a leaf whose channel node is not called
    /// <c>Channel N</c> (analog terminals name it after the PDO) is reachable only this way. A path that
    /// does not exist is an error naming it, never a guess. A plain name (no <c>^</c>) is tried under the
    /// terminal too; when it is not there it keeps its old role as a name hint for the leaf search.
    /// </summary>
    private string? ResolveExplicitIoLeaf(IoPoint point, List<string> tried)
    {
        string leaf = point.IoLeaf?.Trim() ?? string.Empty;
        if (leaf.Length == 0) return null;

        bool isPath = leaf.Contains('^', StringComparison.Ordinal);
        string candidate = leaf.StartsWith("TIID^", StringComparison.OrdinalIgnoreCase)
            ? leaf
            : $"{FindTerminal(point.TerminalName).Path}^{leaf}";
        if (Probe(candidate, tried)) return candidate;
        if (!isPath) return null;

        throw new InvalidOperationException(
            $"IO leaf '{leaf}' (ioLeaf) not found: '{candidate}' does not exist. An ioLeaf path is used exactly as " +
            "given, relative to the terminal or absolute from TIID^; check it with twincat_get_mapping_info or the " +
            "XAE tree (e.g. 'AI Standard Channel 1^Value' for an analog input).");
    }

    private bool Probe(string path, List<string> tried)
    {
        if (tried.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;
        tried.Add(path);
        return _tree.Lookup(path) is not null;
    }

    private IEnumerable<string> SchemaLeafCandidates(string terminalName, int channel, string direction)
    {
        var result = new List<string>();
        _schemaTerminalPaths.TryGetValue(terminalName, out string? path);
        if (path is not null) LinkCandidates.AddIoCandidates(result, path, channel, direction);
        if (_schemaTerminalPaths.TryGetValue("__KEY__" + LinkCandidates.TerminalNameKey(terminalName), out string? keyed) &&
            !string.Equals(keyed, path, StringComparison.OrdinalIgnoreCase))
            LinkCandidates.AddIoCandidates(result, keyed, channel, direction);
        return result;
    }

    // The terminal: the path this run created it at, else a unique exact-name match in the
    // master subtree (index read once per run, E-16).
    private ITreeNode FindTerminal(string terminalName)
    {
        foreach (string key in new[] { terminalName, "__KEY__" + LinkCandidates.TerminalNameKey(terminalName) })
        {
            if (_createdPaths.TryGetValue(key, out string? created) && _tree.Lookup(created) is ITreeNode hit)
                return hit;
        }

        _masterNameIndex ??= BuildMasterNameIndex();
        if (!_masterNameIndex.TryGetValue(terminalName, out var paths) || paths.Count == 0)
            throw new InvalidOperationException($"Terminal '{terminalName}' not found under EtherCAT master '{_target.MasterPath}'.");
        if (paths.Count > 1)
        {
            if (_target.Strict)
                throw new InvalidOperationException(
                    $"Terminal name '{terminalName}' is ambiguous under '{_target.MasterPath}': {string.Join(", ", paths.Select(p => $"'{p}'"))}.");
            _warn($"Terminal name '{terminalName}' occurs {paths.Count} times under '{_target.MasterPath}'; using '{paths[0]}' (lenient mode).");
        }
        return _tree.Lookup(paths[0])
            ?? throw new InvalidOperationException($"Terminal '{terminalName}' at '{paths[0]}' no longer resolves.");
    }

    private Dictionary<string, List<string>> BuildMasterNameIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        ITreeNode master = _tree.Lookup(_target.MasterPath)
            ?? throw new InvalidOperationException($"EtherCAT master '{_target.MasterPath}' not found.");
        var stack = new Stack<ITreeNode>();
        foreach (ITreeNode child in SafeChildren(master)) stack.Push(child);
        int seen = 0;
        while (stack.Count > 0 && seen++ < MasterIndexLimit)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ITreeNode node = stack.Pop();
            string name = node.Name;
            if (!string.IsNullOrWhiteSpace(name) && node.Path.Length > 0)
            {
                if (!index.TryGetValue(name, out var list)) index[name] = list = [];
                list.Add(node.Path);
            }
            foreach (ITreeNode child in SafeChildren(node)) stack.Push(child);
        }
        return index;
    }

    // Channel node by name in the terminal's IO group folders, then a bounded scored scan of the
    // terminal's own subtree (never outside it).
    private string? FindIoLeafUnder(ITreeNode terminal, int channel, string direction, string ioLeafOverride)
    {
        string[] channelNames = [$"Channel {channel}", $"Kanal {channel}"];
        foreach (string folderName in LinkCandidates.IoGroupFolderNames(direction))
        {
            foreach (string channelName in channelNames)
            {
                ITreeNode? hit = _tree.Lookup($"{terminal.Path}^{folderName}^{channelName}");
                if (hit is not null) return hit.Path;
            }
        }

        string dir = direction.Trim().ToUpperInvariant();
        string? best = null;
        int bestScore = -1;
        var stack = new Stack<ITreeNode>();
        stack.Push(terminal);
        int seen = 0;
        while (stack.Count > 0 && seen++ < 10_000)
        {
            ITreeNode node = stack.Pop();
            string name = node.Name;
            string path = node.Path;
            if (!ReferenceEquals(node, terminal) && LinkCandidates.IsIoLeafChannelMatch(name, path, channel))
            {
                int score = LinkCandidates.ScoreIoLeafCandidate(name, channel, dir, ioLeafOverride);
                if (score > bestScore)
                {
                    best = path;
                    bestScore = score;
                }
            }
            foreach (ITreeNode child in SafeChildren(node)) stack.Push(child);
        }
        return best;
    }

    // Named IO-Link process-data entry under its master terminal.
    private string ResolveIoLinkLeafPath(IoPoint point)
    {
        EnsureIoLinkIoddPresent(point);

        var tried = new List<string>();
        if (_schemaTerminalPaths.TryGetValue(point.TerminalName, out string? schemaPath))
            foreach (string candidate in IoLinkLinkPaths.Candidates(schemaPath, point.IoLinkPort, point.IoLinkChannel))
                if (Probe(candidate, tried)) return candidate;

        ITreeNode terminal = FindTerminal(point.TerminalName);
        foreach (string candidate in IoLinkLinkPaths.Candidates(terminal.Path, point.IoLinkPort, point.IoLinkChannel))
            if (Probe(candidate, tried)) return candidate;

        // An explicit ioLeaf (full path) still wins when it exists.
        if (!string.IsNullOrWhiteSpace(point.IoLeaf) && _tree.Lookup(point.IoLeaf) is not null) return point.IoLeaf!;

        throw new InvalidOperationException(
            $"IO-Link PDO leaf not found for '{point.Label}': port {point.IoLinkPort?.ToString(CultureInfo.InvariantCulture) ?? "?"} " +
            $"channel '{point.IoLinkChannel}' under terminal '{point.TerminalName}' (tried {string.Join(", ", tried.Select(t => $"'{t}'"))}). " +
            "Is the device assigned to the port and the IODD broken down?");
    }

    // Once per IO-Link master terminal: every configured port's IODD must be installed. A read
    // failure lets normal resolution surface the problem; a positively missing IODD stops the run.
    private void EnsureIoLinkIoddPresent(IoPoint point)
    {
        if (!_ioddCheckedTerminals.Add(point.TerminalName)) return;

        ITreeNode? node = _schemaTerminalPaths.TryGetValue(point.TerminalName, out string? sp) ? _tree.Lookup(sp) : null;
        if (node is null)
        {
            try { node = FindTerminal(point.TerminalName); }
            catch (Exception ex) when (ComCall.Recoverable(ex)) { return; }
        }

        IoLinkConfig cfg;
        try { cfg = IoLinkXmlParser.Parse(node.Path.Length > 0 ? node.Path : point.TerminalName, node.ProduceXml(false)); }
        catch (Exception ex) when (ComCall.Recoverable(ex)) { return; }

        string? folder = IoddFolder.ResolveDefault();
        if (string.IsNullOrWhiteSpace(folder)) return;

        string? report = IoddPresence.MissingReport(cfg, IoddCatalog.Scan(folder));
        if (report is not null) throw new InvalidOperationException(report);
    }

    /// <summary>
    /// Expected tree path of every schema node under the resolved master
    /// (<paramref name="masterPath"/>), by name and by designation key. Pure.
    /// </summary>
    internal static Dictionary<string, string> BuildSchemaTerminalPathIndex(EthercatNode root, string masterPath)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root is null || string.IsNullOrWhiteSpace(masterPath)) return index;

        void AddPath(string nodeName, string path)
        {
            if (string.IsNullOrWhiteSpace(nodeName)) return;
            index[nodeName] = path;
            string key = "__KEY__" + LinkCandidates.TerminalNameKey(nodeName);
            index.TryAdd(key, path);
        }

        void Walk(EthercatNode node, string parentPath)
        {
            string current = $"{parentPath}^{node.Name}";
            AddPath(node.Name, current);
            foreach (var child in node.Children) Walk(child, current);
        }

        if (EtherCatTopologyBuilder.ShouldFlattenPseudoRootNode("TIID", root))
        {
            AddPath(root.Name, masterPath);
            foreach (var child in root.Children) Walk(child, masterPath);
        }
        else
        {
            Walk(root, masterPath);
        }
        return index;
    }
}
