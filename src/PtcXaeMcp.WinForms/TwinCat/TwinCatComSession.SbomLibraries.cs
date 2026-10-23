using System.Collections;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // sbom: the library repositories (ITcPlcLibraryManager.Repositories) and the per-PLC library context.

    // The repositories belong to ITcPlcLibraryManager. The property is not reachable on every COM pointer of the PLC
    // project: AddLibrary/RemoveReference only dispatch on the TIPC^<plc>^<project>^References tree item (see
    // GetLibraryManager), so that item is asked first, then the source-project node and its References child. If none
    // answers, the standard managed-library folders of the TwinCAT installation are listed instead
    // (source "filesystem-fallback") with a warning, so that the references can still be resolved to files.
    public LibraryRepositoryListing ListLibraryRepositoryEntries()
    {
        var warnings = new List<string>();
        List<LibraryRepositoryEntry>? entries = null;
        try { entries = ComBusyRetry.Run(() => ReadRepositoriesViaCom(warnings), OpenTimeout); }
        catch (InvalidOperationException ex) { warnings.Add( // com-allow: COMExceptions (busy XAE) are not InvalidOperationException and stay with ComBusyRetry
$"ITcPlcLibraryManager.Repositories could not be read ({ex.Message})."); }

        if (entries is not null) return new LibraryRepositoryListing(entries, []);

        var fallback = LibraryRepository.Roots()
            .Select(folder => new LibraryRepositoryEntry("System", folder, LibraryRepositorySource.FilesystemFallback)).ToList();
        if (fallback.Count == 0)
            throw new InvalidOperationException(
                "ITcPlcLibraryManager.Repositories is not readable and no managed-library folder was found in the TwinCAT installation. " +
                string.Join(" ", warnings));
        warnings.Add("The repositories come from the standard managed-library folders of the TwinCAT installation, not from the library manager (source filesystem-fallback); custom repositories are not listed.");
        return new LibraryRepositoryListing(fallback, warnings);
    }

    private static List<LibraryRepositoryEntry>? ReadRepositoriesViaCom(List<string> warnings)
    {
        dynamic dte = GetActiveTcDte();
        var candidates = new List<(string Label, object Node)>();
        object? referencesItem = FindLibraryManagerNode(dte);
        if (referencesItem is not null) candidates.Add(("References tree item", referencesItem));
        List<PlcSourceProject> projects = EnumeratePlcSourceProjects(dte);
        foreach (var p in projects)
        {
            candidates.Add(($"source-project node '{p.Name}'", p.Nested));
            object? child = FindReferencesChild(p.Nested);
            if (child is not null) candidates.Add(($"References child of '{p.Name}'", child));
        }

        foreach (var (label, node) in candidates)
        {
            var entries = ReadRepositories(node, label, warnings);
            if (entries is not null) return entries;
        }
        if (candidates.Count == 0) warnings.Add("No PLC project node found to ask for the repositories.");
        return null;
    }

    public IReadOnlyList<PlcLibraryContext> ReadPlcLibraryContexts(string? plcProjectPath) =>
        ComBusyRetry.Run<IReadOnlyList<PlcLibraryContext>>(() =>
        {
            dynamic dte = GetActiveTcDte();
            List<PlcSourceProject> projects = EnumeratePlcSourceProjects(dte);
            if (projects.Count == 0)
                throw new InvalidOperationException("No PLC source-project node found under TIPC — open a solution first.");

            if (!string.IsNullOrWhiteSpace(plcProjectPath))
            {
                string wanted = plcProjectPath.Trim();
                projects = projects.Where(p =>
                    string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Path, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.ProjectName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                if (projects.Count == 0)
                    throw new InvalidOperationException(
                        $"PLC project '{plcProjectPath}' not found. Available: {string.Join(", ", ((List<PlcSourceProject>)EnumeratePlcSourceProjects(dte)).Select(p => p.Path))}.");
            }

            var result = new List<PlcLibraryContext>();
            foreach (var p in projects)
            {
                dynamic? refsNode = FindReferencesChild(p.Nested);
                var refs = refsNode is null ? new List<LibraryReference>() : ReferencesFromNode(refsNode);
                if (refs.Count == 0 && projects.Count == 1)
                {
                    string? sln = ActiveSolutionPath();
                    string? plcproj = sln is null ? null : LibraryParsing.FindPlcprojNear(sln);
                    if (plcproj is not null) refs = LibraryParsing.ListReferencesFromPlcproj(plcproj);
                }

                object scan;
                try { scan = ((dynamic)p.Nested).ScanLibraries(); }
                catch (Exception ex) when (ComCall.NotBusy(ex))
                {
                    throw new InvalidOperationException($"ITcPlcLibraryManager.ScanLibraries failed: {ex.Message}", ex);
                }
                result.Add(new PlcLibraryContext(p.Name, refs, ReadPlaceholderResolutions(p.Nested, refsNode), MaterialiseInstalled(scan)));
            }
            return result;
        }, OpenTimeout);

    private sealed record PlcSourceProject(string Name, string Path, string ProjectName, object Nested);

    // Every PLC project under TIPC (the same node walk as FindPlcSourceProjectNode, without stopping at the first).
    private static List<PlcSourceProject> EnumeratePlcSourceProjects(dynamic dte)
    {
        var found = new List<PlcSourceProject>();
        dynamic? projects = ComCall.ReadProp<object?>(() => dte.Solution.Projects, null);
        if (projects is null) return found;
        int count = ComCall.ReadProp(() => (int)projects.Count, 0);
        for (int i = 1; i <= count; i++)
        {
            object? project = ComCall.ReadProp<object?>(() => projects.Item(i), null);
            object? sysObj = project is null ? null : ComCall.ReadProp<object?>(() => ((dynamic)project).Object as object, null);
            if (sysObj is null) continue;

            dynamic? tipc = LookupTreeItemOrNull(sysObj, "TIPC");
            if (tipc is null) continue;

            foreach (object plcNode in Children(tipc))
            {
                object? nested = ComCall.ReadProp<object?>(() => ((dynamic)plcNode).NestedProject, null);
                if (nested is null || !LooksLikePlcSourceProject(nested)) continue;
                string name = NameOf(plcNode);
                string path = ComCall.ReadProp(() => (string?)((dynamic)plcNode).PathName ?? name, name);
                string projectName = ComCall.ReadProp(() => (string?)((dynamic)nested).Name ?? name, name);
                found.Add(new PlcSourceProject(name, path, projectName, nested));
            }
        }
        return found;
    }

    private static List<LibraryReference> ReferencesFromNode(dynamic refsNode)
    {
        var results = new List<LibraryReference>();
        int count = ComCall.ReadProp(() => (int)refsNode.ChildCount, 0);
        for (int i = 1; i <= count; i++)
        {
            object? item = ChildAt(refsNode, i);
            if (item is null) continue;
            string xml = ComCall.ReadProp(() => (string?)((dynamic)item).ProduceXml(false) ?? string.Empty, string.Empty);
            if (string.IsNullOrWhiteSpace(xml)) continue;
            var entry = LibraryParsing.ParseSingleReferenceXml(xml);
            if (entry is not null) results.Add(entry);
        }
        return results;
    }

    private static List<LibraryRepositoryEntry>? ReadRepositories(object manager, string label, List<string> warnings)
    {
        object? collection;
        try { collection = ((dynamic)manager).Repositories; }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            warnings.Add($"Repositories not readable on the {label}: {ex.Message}");
            return null;
        }
        if (collection is null) return null;

        var entries = new List<LibraryRepositoryEntry>();
        foreach (object item in EnumerateComCollection(collection))
        {
            string name = ComCall.ReadProp(() => (string?)((dynamic)item).Name ?? string.Empty, string.Empty);
            string folder = ComCall.ReadProp(() => (string?)((dynamic)item).Folder ?? string.Empty, string.Empty);
            if (name.Length > 0 || folder.Length > 0) entries.Add(new LibraryRepositoryEntry(name, folder));
        }
        return entries.Count == 0 ? null : entries;
    }

    // A COM collection (_NewEnum, Count, Item): enumerate it, else walk Item(1..Count).
    private static List<object> EnumerateComCollection(object collection)
    {
        var items = new List<object>();
        if (collection is IEnumerable sequence)
        {
            try
            {
                foreach (object? o in sequence)
                    if (o is not null) items.Add(o);
                return items;
            }
            catch (Exception ex) when (ComCall.NotBusy(ex)) { items.Clear(); }
        }
        int count = ComCall.ReadProp(() => (int)((dynamic)collection).Count, 0);
        for (int i = 1; i <= count; i++)
        {
            object? o = ComCall.ReadProp<object?>(() => ((dynamic)collection).Item(i), null);
            if (o is not null) items.Add(o);
        }
        return items;
    }

    // ITcPlcPlaceholderRef: PlaceholderName, EffectiveResolution (ITcPlcLibrary), CurrentLibrary; ITcPlcPlaceholderRef2:
    // DefaultResolution. Whether the entries of ITcPlcLibraryManager.References or the children of the References node
    // answer is confirmed on a live XAE only, so both are tried; a node that is no placeholder simply does not answer.
    private static List<PlaceholderResolution> ReadPlaceholderResolutions(object manager, object? refsNode)
    {
        var found = new List<PlaceholderResolution>();
        foreach (object? source in new object?[] { manager, refsNode })
        {
            if (source is null) continue;
            object? collection = ComCall.ReadProp<object?>(() => ((dynamic)source).References, null);
            if (collection is null) continue;
            foreach (object item in EnumerateComCollection(collection))
                AddPlaceholder(found, item);
        }
        if (found.Count == 0 && refsNode is not null)
            foreach (object child in Children(refsNode))
                AddPlaceholder(found, child);
        return found;
    }

    private static void AddPlaceholder(List<PlaceholderResolution> into, object item)
    {
        string name = ComCall.ReadProp(() => (string?)((dynamic)item).PlaceholderName ?? string.Empty, string.Empty);
        if (name.Length == 0 || into.Any(p => string.Equals(p.Placeholder, name, StringComparison.OrdinalIgnoreCase))) return;
        into.Add(new PlaceholderResolution(name,
            LibraryOf(ComCall.ReadProp<object?>(() => ((dynamic)item).EffectiveResolution, null)),
            LibraryOf(ComCall.ReadProp<object?>(() => ((dynamic)item).CurrentLibrary, null)),
            LibraryOf(ComCall.ReadProp<object?>(() => ((dynamic)item).DefaultResolution, null))));
    }

    // ITcPlcLibrary: LibraryName, Version, Distributor.
    private static ResolvedLibrary? LibraryOf(object? library)
    {
        if (library is null) return null;
        string name = ComCall.ReadProp(() => (string?)((dynamic)library).LibraryName ?? string.Empty, string.Empty);
        if (name.Length == 0) return null;
        string version = ComCall.ReadProp(() => (string?)((dynamic)library).Version ?? string.Empty, string.Empty);
        string distributor = ComCall.ReadProp(() => (string?)((dynamic)library).Distributor ?? string.Empty, string.Empty);
        return new ResolvedLibrary(name, version, distributor);
    }
}
