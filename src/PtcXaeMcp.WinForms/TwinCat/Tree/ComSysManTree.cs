namespace PtcXaeMcp.WinForms.TwinCat.Tree;

/// <summary>
/// The real <see cref="ISysManTree"/>: the TwinCAT System Manager (ITcSysManager) reached over
/// late-bound COM. Every call goes through <see cref="ComCall"/> — a busy/blocked XAE is retried
/// against a deadline and then surfaces as <see cref="XaeBlockedException"/>, never as
/// "not found"; genuine failures (member missing, wrong node) take the documented fallback.
/// Also hosts the COM read helpers the runner used to keep privately (R3 of issue #155), so there
/// is one copy of them.
/// </summary>
internal sealed class ComSysManTree : ISysManTree
{
    /// <summary>Deadline for one tree call while the XAE rejects calls as busy.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly dynamic _sysMan;
    private readonly CancellationToken _cancellationToken;

    public ComSysManTree(object sysMan, CancellationToken cancellationToken)
    {
        _sysMan = sysMan ?? throw new ArgumentNullException(nameof(sysMan));
        _cancellationToken = cancellationToken;
    }

    /// <summary>The ITcSysManager object this tree wraps.</summary>
    public object SysMan => _sysMan;

    /// <summary>One COM call: cancellation checked first, busy retried against <see cref="CallTimeout"/>.</summary>
    internal T Call<T>(Func<T> action, string? operation = null)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return ComCall.Run(action, CallTimeout, operation: operation);
    }

    internal void Call(Action action, string? operation = null)
        => Call<object?>(() => { action(); return null; }, operation);

    public ITreeNode? Lookup(string path)
    {
        object? raw = LookupRaw(path);
        if (raw is null) return null;
        var node = new ComTreeNode(this, raw);
        string actual = node.Path;
        // Exact: a readable path that differs from the requested one is not this node.
        return actual.Length == 0 || string.Equals(actual, path, StringComparison.OrdinalIgnoreCase)
            ? node
            : null;
    }

    /// <summary>
    /// The runner's historical lookup (LookupTreeItem + <see cref="SelectSingleTreeItemFromLookup"/>):
    /// null for a genuine "not there", busy/blocked propagates. A path below a referenced stand-alone
    /// PLC project is walked child by child instead (<see cref="TwinCatComSession.MustWalkPlcPath"/>):
    /// LookupTreeItem on it crashes the XAE (#14).
    /// </summary>
    internal object? LookupRaw(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Call(() => TwinCatComSession.MustWalkPlcPath(SysMan, path), $"NestedProject check for '{path}'"))
                return Call(() => TwinCatComSession.WalkPlcPath(SysMan, path), $"tree walk to '{path}'");
        }
        catch (Exception ex) when (ComCall.Recoverable(ex))
        {
            return null;
        }

        try
        {
            return Call(() => (object?)SelectSingleTreeItemFromLookup(_sysMan.LookupTreeItem(path), expectedPath: path),
                $"LookupTreeItem('{path}')");
        }
        catch (Exception ex) when (ComCall.Recoverable(ex))
        {
            return null;
        }
    }

    public void Delete(ITreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string path = node.Path;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Refusing to delete a tree item whose path cannot be read.");

        string parentPath = TreeNodeExtensions.ParentPath(path);
        if (parentPath.Length == 0)
            throw new InvalidOperationException($"Refusing to delete top-level tree item '{path}'.");

        ITreeNode parent = Lookup(parentPath)
            ?? throw new InvalidOperationException($"Parent '{parentPath}' of '{path}' not found; nothing was deleted.");
        string leaf = TreeNodeExtensions.Leaf(path);
        dynamic parentRaw = parent.Raw;
        dynamic nodeRaw = node.Raw;

        // Same shape as TwinCatComSession.DeletePlcObject: DeleteChild(leaf) on the parent
        // addresses exactly this name; the node's own Delete() is the fallback for XAE builds
        // that index children differently. Busy propagates instead of taking the fallback.
        try
        {
            Call(() => { parentRaw.DeleteChild(leaf); }, $"DeleteChild('{leaf}')");
        }
        catch (Exception ex) when (ComCall.Recoverable(ex))
        {
            try
            {
                Call(() => { nodeRaw.Delete(); }, $"Delete('{path}')");
            }
            catch (Exception ex2) when (ComCall.Recoverable(ex2))
            {
                throw new InvalidOperationException($"Delete failed for '{path}': {ex.Message} / {ex2.Message}", ex2);
            }
        }
    }

    public void LinkVariables(string pathA, string pathB)
        => Call(() => { _sysMan.LinkVariables(pathA, pathB, 0, 0, 0); }, "LinkVariables");

    // ---- COM read helpers (moved from NativeTwinCatRunner, R3) ------------------------------

    /// <summary>Item name: Name, then ItemName, then the last path segment, else "&lt;unknown&gt;".</summary>
    internal static string ReadName(object item)
    {
        dynamic d = item;
        string? value = ComCall.ReadProp(() => (string?)d.Name, null);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        value = ComCall.ReadProp(() => (string?)d.ItemName, null);
        if (!string.IsNullOrWhiteSpace(value)) return value;

        string path = ReadPath(item);
        return path.Length > 0 ? path.Split('^')[^1] : "<unknown>";
    }

    /// <summary>ITcSmTreeItem.PathName, empty when unreadable.</summary>
    internal static string ReadPath(object item)
    {
        dynamic d = item;
        return ComCall.ReadProp(() => (string?)d.PathName, null) ?? string.Empty;
    }

    /// <summary>ItemSubType (then SubType), <paramref name="fallback"/> when neither can be read.</summary>
    internal static int ReadSubType(object item, int fallback)
    {
        dynamic d = item;
        int? value = ComCall.ReadProp(() => (int?)(int)d.ItemSubType, null);
        if (value is not null) return value.Value;
        value = ComCall.ReadProp(() => (int?)(int)d.SubType, null);
        return value ?? fallback;
    }

    /// <summary>ITcSmTreeItem.ItemType, -1 when unreadable.</summary>
    internal static int ReadItemType(object item)
    {
        dynamic d = item;
        return ComCall.ReadProp(() => (int?)(int)d.ItemType, null) ?? -1;
    }

    /// <summary>A COM call that returned a one-element SAFEARRAY yields that element.</summary>
    internal static object? UnwrapSingle(object? value)
    {
        if (value is Array array)
        {
            return array.Length == 1 ? array.GetValue(0) : null;
        }

        return value;
    }

    /// <summary>
    /// LookupTreeItem may return a single item or an array of candidates. Picks the one whose
    /// path (then name/subtype) matches, preferring an exact path.
    /// </summary>
    internal static object? SelectSingleTreeItemFromLookup(
        object? lookupResult,
        string expectedPath = "",
        string expectedName = "",
        int expectedSubType = -1)
    {
        if (lookupResult is null)
        {
            return null;
        }

        static object? GetParent(object item)
        {
            dynamic d = item;
            return ComCall.ReadProp(() => (object?)d.Parent, null);
        }

        bool Matches(object? item, bool requirePath)
        {
            if (item is null || item is Array)
            {
                return false;
            }

            if (requirePath && !string.IsNullOrWhiteSpace(expectedPath) &&
                !string.Equals(ReadPath(item), expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedName) &&
                !string.Equals(ReadName(item), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return expectedSubType < 0 || ReadSubType(item, -1) == expectedSubType;
        }

        if (lookupResult is not Array array)
        {
            return Matches(lookupResult, requirePath: true) ? lookupResult : null;
        }

        foreach (object? candidate in array)
        {
            if (Matches(candidate, requirePath: true)) return candidate;
        }

        foreach (object? candidate in array)
        {
            object? parent = candidate is null ? null : GetParent(candidate);
            if (Matches(parent, requirePath: true)) return parent;
        }

        foreach (object? candidate in array)
        {
            if (Matches(candidate, requirePath: false)) return candidate;
        }

        foreach (object? candidate in array)
        {
            object? parent = candidate is null ? null : GetParent(candidate);
            if (Matches(parent, requirePath: false)) return parent;
        }

        return array.Length == 1 ? array.GetValue(0) : null;
    }
}

/// <summary>A COM ITcSmTreeItem behind <see cref="ITreeNode"/>.</summary>
internal sealed class ComTreeNode : ITreeNode
{
    private readonly ComSysManTree _tree;
    private readonly dynamic _item;

    public ComTreeNode(ComSysManTree tree, object item)
    {
        _tree = tree;
        _item = item ?? throw new ArgumentNullException(nameof(item));
    }

    public object Raw => _item;

    // Issue #190: the property reads go through the tree's busy scope like every other call on
    // the seam. ReadProp alone rethrows a busy XAE raw — right after a CreateChild that escaped
    // to the caller's outer ComBusyRetry.Run and replayed the whole add, which then found its own
    // node ("already exists"). Retried here, exhaustion is a final XaeBlockedException.
    public string Name => _tree.Call(() => ComSysManTree.ReadName(_item), "Name");
    public string Path => _tree.Call(() => ComSysManTree.ReadPath(_item), "PathName");
    public int ItemType => _tree.Call(() => ComSysManTree.ReadItemType(_item), "ItemType");
    public int SubType => _tree.Call(() => ComSysManTree.ReadSubType(_item, -1), "ItemSubType");

    public int ChildCount => _tree.Call(() => (int)_item.ChildCount, "ChildCount");

    public ITreeNode? Child(int index)
    {
        try
        {
            object? raw = _tree.Call(() => ComSysManTree.UnwrapSingle(_item.Child(index)), "Child");
            return raw is null ? null : new ComTreeNode(_tree, raw);
        }
        catch (Exception ex) when (ComCall.Recoverable(ex))
        {
            return null;
        }
    }

    public ITreeNode CreateChild(string name, int subType, string? before, object? vInfo)
    {
        object? raw = _tree.Call(
            () => ComSysManTree.UnwrapSingle(_item.CreateChild(name, subType, before, vInfo)),
            $"CreateChild('{name}')");
        if (raw is not null) return new ComTreeNode(_tree, raw);

        // Some item kinds return nothing from CreateChild; the child is then addressed by path.
        string parentPath = Path;
        string childPath = parentPath.Length == 0 ? string.Empty : $"{parentPath}^{name}";
        return (childPath.Length == 0 ? null : _tree.Lookup(childPath))
            ?? throw new InvalidOperationException(
                $"CreateChild('{name}') under '{parentPath}' returned no item and '{childPath}' cannot be resolved.");
    }

    public string ProduceXml(bool recursive = true)
        => _tree.Call(() => (string)_item.ProduceXml(recursive), "ProduceXml") ?? string.Empty;

    public void ConsumeXml(string xml) => _tree.Call(() => { _item.ConsumeXml(xml); }, "ConsumeXml");

    public void ExportChild(string childName, string filePath) =>
        _tree.Call(() => { _item.ExportChild(childName, filePath); }, $"ExportChild('{childName}')");
}
