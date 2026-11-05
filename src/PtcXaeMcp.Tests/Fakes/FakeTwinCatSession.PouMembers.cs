using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- Structured members & object lifecycle (feature/pou-handling) -------

    public Dictionary<string, List<PouMemberEntry>> MembersByPou { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Exception? ListPouMembersThrows { get; set; }
    public Exception? AddPouMemberThrows { get; set; }
    public Exception? ReadPouMemberThrows { get; set; }
    public Exception? WritePouMemberThrows { get; set; }
    public Exception? DeletePlcObjectThrows { get; set; }
    public Exception? RenamePlcObjectThrows { get; set; }
    public Exception? PlcOpenExportThrows { get; set; }
    public Exception? PlcOpenImportThrows { get; set; }

    public IReadOnlyList<PouMemberEntry> ListPouMembers(string pouPath)
    {
        CallLog.Add($"ListPouMembers({pouPath})");
        if (ListPouMembersThrows is not null) throw ListPouMembersThrows;
        return MembersByPou.TryGetValue(pouPath, out var m)
            ? m
            : throw new InvalidOperationException($"POU tree path not found: {pouPath}");
    }

    /// <summary>Folders under a POU (path of the folder), returned by <see cref="ListPouElements"/> only.</summary>
    public Dictionary<string, List<string>> FoldersByPou { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PouMemberEntry> ListPouElements(string pouPath)
    {
        var members = ListPouMembers(pouPath).ToList();
        if (FoldersByPou.TryGetValue(pouPath, out var folders))
            members.AddRange(folders.Select(f => new PouMemberEntry(f, f[(f.LastIndexOf('^') + 1)..], PlcKind.Folder, PlcItemType.Folder)));
        return members;
    }

    public string AddPouMember(string pouPath, string kind, string name, string? returnType, string? implLanguage) =>
        AddPouMember(pouPath, kind, name, returnType, implLanguage, PropertyAccessors.Both);

    public string AddPouMember(string pouPath, string kind, string name, string? returnType, string? implLanguage,
        PropertyAccessors accessors)
    {
        CallLog.Add($"AddPouMember(pou={pouPath}, kind={kind}, name={name}, ret={returnType ?? "<null>"}, lang={implLanguage ?? "<null>"}, accessors={accessors})");
        if (AddPouMemberThrows is not null) throw AddPouMemberThrows;
        if (!MembersByPou.TryGetValue(pouPath, out var list))
            throw new InvalidOperationException($"POU tree path not found: {pouPath}");
        var path = $"{pouPath}^{name}";
        list.Add(new PouMemberEntry(path, name, kind));
        PouBodiesByPath[path] = new PouBody($"{kind} {name}", string.Empty, implLanguage ?? "ST");
        if (kind == PlcMemberKind.Property)
        {
            if (accessors is PropertyAccessors.Get or PropertyAccessors.Both)
                list.Add(new PouMemberEntry($"{path}^Get", "Get", PlcMemberKind.Get));
            if (accessors is PropertyAccessors.Set or PropertyAccessors.Both)
                list.Add(new PouMemberEntry($"{path}^Set", "Set", PlcMemberKind.Set));
        }
        return path;
    }

    public PouBody ReadPouMember(string memberPath)
    {
        CallLog.Add($"ReadPouMember({memberPath})");
        if (ReadPouMemberThrows is not null) throw ReadPouMemberThrows;
        return PouBodiesByPath.TryGetValue(memberPath, out var b)
            ? b
            : throw new InvalidOperationException($"Member tree path not found: {memberPath}");
    }

    public void WritePouMember(string memberPath, string? declaration, string? implementation)
    {
        CallLog.Add($"WritePouMember({memberPath}, decl={(declaration is null ? "<null>" : "<set>")}, impl={(implementation is null ? "<null>" : "<set>")})");
        if (WritePouMemberThrows is not null) throw WritePouMemberThrows;
        if (!PouBodiesByPath.TryGetValue(memberPath, out var existing))
            throw new InvalidOperationException($"Member tree path not found: {memberPath}");
        PouBodiesByPath[memberPath] = existing with
        {
            Declaration = declaration ?? existing.Declaration,
            Implementation = implementation ?? existing.Implementation,
        };
    }

    /// <summary>ItemType per path for <see cref="ProbePlcNode"/>; a null value = unreadable ItemType.</summary>
    public Dictionary<string, int?> PlcNodeItemTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Direct child count per path for <see cref="ProbePlcNode"/>; a path not listed has an unknown count.</summary>
    public Dictionary<string, int> PlcNodeChildCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Paths <see cref="ProbePlcNode"/> reports as not existing.</summary>
    public HashSet<string> MissingPlcPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>ItemType reported for any other (existing) path. Default: FUNCTION_BLOCK.</summary>
    public int? DefaultPlcItemType { get; set; } = PlcItemType.PouFunctionBlock;

    public PlcNodeInfo ProbePlcNode(string treePath)
    {
        CallLog.Add($"ProbePlcNode({treePath})");
        if (PlcTreeMutated && ProbeThrowsOnceMutated is not null) throw ProbeThrowsOnceMutated;
        if (MissingPlcPaths.Contains(treePath)) return PlcNodeInfo.Missing;
        return new PlcNodeInfo(true,
            PlcNodeItemTypes.TryGetValue(treePath, out var it) ? it : DefaultPlcItemType,
            PlcNodeChildCounts.TryGetValue(treePath, out var count) ? count : null);
    }

    public void DeletePlcObject(string treePath)
    {
        CallLog.Add($"DeletePlcObject({treePath})");
        if (DeletePlcObjectThrows is not null) throw DeletePlcObjectThrows;
        PlcTreeMutated = true;
        if (NoOpMutations.Contains(nameof(DeletePlcObject))) return;
        Pous.RemoveAll(p => p.Path == treePath);
        PouBodiesByPath.Remove(treePath);
        MissingPlcPaths.Add(treePath);
    }

    public Exception? DeletePlcFolderThrows { get; set; }

    public void DeletePlcFolder(string treePath)
    {
        CallLog.Add($"DeletePlcFolder({treePath})");
        if (DeletePlcFolderThrows is not null) throw DeletePlcFolderThrows;
        PlcTreeMutated = true;
        if (NoOpMutations.Contains(nameof(DeletePlcFolder))) return;
        Pous.RemoveAll(p => p.Path == treePath || p.Path.StartsWith(treePath + "^", StringComparison.OrdinalIgnoreCase));
        MissingPlcPaths.Add(treePath);
    }

    /// <summary>updateReferences of the last RenamePlcObject call.</summary>
    public bool? LastRenameUpdateReferences { get; private set; }

    /// <summary>What the session reports about the XAE's refactoring prompt (null = none appeared).</summary>
    public string? RenameRefactoringPrompt { get; set; }

    /// <summary>True: the refactoring prompt could not be answered as asked.</summary>
    public bool RenameRefactoringPromptFailed { get; set; }

    public PlcRenameResult RenamePlcObject(string treePath, string newName, bool updateReferences = false)
    {
        CallLog.Add($"RenamePlcObject({treePath}, {newName})");
        LastRenameUpdateReferences = updateReferences;
        if (RenamePlcObjectThrows is not null) throw RenamePlcObjectThrows;
        int caret = treePath.LastIndexOf('^');
        string newPath = caret > 0 ? treePath[..(caret + 1)] + newName : newName;
        PlcTreeMutated = true;
        if (NoOpMutations.Contains(nameof(RenamePlcObject)))
        {
            MissingPlcPaths.Add(newPath);
            return new PlcRenameResult(newPath, RenameRefactoringPrompt, RenameRefactoringPromptFailed);
        }
        MissingPlcPaths.Remove(newPath);
        if (!RenameKeepsOldPath) MissingPlcPaths.Add(treePath);
        return new PlcRenameResult(newPath, RenameRefactoringPrompt, RenameRefactoringPromptFailed);
    }

    public string PlcOpenExport(string filePath, string selector)
    {
        CallLog.Add($"PlcOpenExport({filePath}, {selector})");
        if (PlcOpenExportThrows is not null) throw PlcOpenExportThrows;
        return filePath;
    }

    /// <summary>What <see cref="PlcOpenExportFrom"/> writes into the export file.</summary>
    public string PlcOpenExportXml { get; set; } = "<project />";
    public Exception? PlcOpenExportFromThrows { get; set; }
    /// <summary>Thrown by the next <see cref="PlcOpenImportInto"/> calls, one per call (then the import succeeds).</summary>
    public Queue<Exception> PlcOpenImportIntoThrows { get; } = new();
    /// <summary>The content of every file <see cref="PlcOpenImportInto"/> was given, in order.</summary>
    public List<string> PlcOpenImportedXml { get; } = new();
    /// <summary>Runs inside a successful <see cref="PlcOpenImportInto"/> with the file content: the test puts the tree in place.</summary>
    public Action<string>? OnPlcOpenImportInto { get; set; }

    public void PlcOpenExportFrom(string plcProjectPath, string filePath, string selector)
    {
        CallLog.Add($"PlcOpenExportFrom({plcProjectPath}, {selector})");
        if (PlcOpenExportFromThrows is not null) throw PlcOpenExportFromThrows;
        File.WriteAllText(filePath, PlcOpenExportXml);
    }

    public void PlcOpenImportInto(string plcProjectPath, string filePath)
    {
        CallLog.Add($"PlcOpenImportInto({plcProjectPath}, {Path.GetFileName(filePath)})");
        if (PlcOpenImportIntoThrows.Count > 0) throw PlcOpenImportIntoThrows.Dequeue();
        PlcTreeMutated = true;
        string xml = File.ReadAllText(filePath);
        PlcOpenImportedXml.Add(xml);
        OnPlcOpenImportInto?.Invoke(xml);
    }

    public string PlcOpenImport(string filePath, int conflictMode)
    {
        CallLog.Add($"PlcOpenImport({filePath}, {conflictMode})");
        if (PlcOpenImportThrows is not null) throw PlcOpenImportThrows;
        if (NoOpMutations.Contains(nameof(PlcOpenImport))) return filePath;
        foreach (string name in PlcOpenImportCheck.ObjectNames(File.ReadAllText(filePath)))
        {
            bool exists = Pous.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            string? add = !exists ? name : conflictMode == 1 ? name + "_1" : null;
            if (add is not null) Pous.Add(new PouEntry("^POUs^" + add, add, PouKind.Pou, "ST"));
        }
        return filePath;
    }
}
