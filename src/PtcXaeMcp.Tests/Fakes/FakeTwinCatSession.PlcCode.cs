using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- PLC source-code surface --------------------------------------------

    public List<PouEntry> Pous { get; } = new();
    public Dictionary<string, PouBody> PouBodiesByPath { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> GvlDeclarationsByPath { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Exception? ListPousThrows { get; set; }
    public Exception? ReadPouThrows { get; set; }
    public Exception? ReadGvlThrows { get; set; }
    public Exception? WritePouBodyThrows { get; set; }
    public Exception? CreatePouThrows { get; set; }
    public Exception? CreateDutThrows { get; set; }
    public Exception? CreateGvlThrows { get; set; }
    public Exception? CreateInterfaceThrows { get; set; }

    public IReadOnlyList<PouEntry> ListPlcElements()
    {
        CallLog.Add("ListPlcElements()");
        if (ListPousThrows is not null) throw ListPousThrows;
        return Pous;
    }

    public IReadOnlyList<PouEntry> ListPous()
    {
        CallLog.Add("ListPous()");
        if (ListPousThrows is not null) throw ListPousThrows;
        return Pous;
    }

    /// <summary>Texts the "XAE" holds, keyed "&lt;plcName&gt;|&lt;relative source path&gt;"; a missing key throws like an absent tree node.</summary>
    public Dictionary<string, List<PlcTextSection>> PlcObjectSections { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PlcTextSection> ReadPlcObjectSections(string plcName, string relativeSourcePath)
    {
        CallLog.Add($"ReadPlcObjectSections({plcName},{relativeSourcePath})");
        return PlcObjectSections.TryGetValue($"{plcName}|{relativeSourcePath}", out var sections)
            ? sections
            : throw new InvalidOperationException($"PLC tree path not found: {plcName}|{relativeSourcePath}");
    }

    public PouBody ReadPou(string treePath)
    {
        CallLog.Add($"ReadPou({treePath})");
        if (ReadPouThrows is not null) throw ReadPouThrows;
        return PouBodiesByPath.TryGetValue(treePath, out var body)
            ? body
            : throw new InvalidOperationException($"Tree path not found: {treePath}");
    }

    public string ReadGvl(string treePath)
    {
        CallLog.Add($"ReadGvl({treePath})");
        if (ReadGvlThrows is not null) throw ReadGvlThrows;
        return GvlDeclarationsByPath.TryGetValue(treePath, out var d)
            ? d
            : throw new InvalidOperationException($"Tree path not found: {treePath}");
    }

    public void WritePouBody(string treePath, string? declaration, string? implementation)
    {
        CallLog.Add($"WritePouBody({treePath}, decl={(declaration is null ? "<null>" : "<set>")}, impl={(implementation is null ? "<null>" : "<set>")})");
        if (WritePouBodyThrows is not null) throw WritePouBodyThrows;
        if (!PouBodiesByPath.TryGetValue(treePath, out var existing))
            throw new InvalidOperationException($"Tree path not found: {treePath}");
        PouBodiesByPath[treePath] = existing with
        {
            Declaration = declaration ?? existing.Declaration,
            Implementation = implementation ?? existing.Implementation,
        };
    }

    public string CreatePou(string parentPath, string name, string pouType, string returnType, string implLanguage)
    {
        CallLog.Add($"CreatePou(parent={parentPath}, name={name}, type={pouType}, ret={returnType}, lang={implLanguage})");
        if (CreatePouThrows is not null) throw CreatePouThrows;
        var newPath = parentPath + "^" + name;
        Pous.Add(new PouEntry(newPath, name, PouKind.Pou, implLanguage));
        PouBodiesByPath[newPath] = new PouBody(
            Declaration: $"{pouType} {name}",
            Implementation: string.Empty,
            Language: implLanguage);
        return newPath;
    }

    public string CreateDut(string parentPath, string name, string dutType, string? baseType)
    {
        CallLog.Add($"CreateDut(parent={parentPath}, name={name}, type={dutType}, baseType={baseType ?? "<null>"})");
        if (CreateDutThrows is not null) throw CreateDutThrows;
        var newPath = parentPath + "^" + name;
        Pous.Add(new PouEntry(newPath, name, PouKind.Dut, null));
        var skeleton = $"TYPE {name} :\nSTRUCT\nEND_STRUCT\nEND_TYPE";
        GvlDeclarationsByPath[newPath] = skeleton;
        // Mirror the COM session: the node exists with a declaration the coordinator
        // can overwrite via WritePouBody (declaration-only).
        PouBodiesByPath[newPath] = new PouBody(skeleton, string.Empty, string.Empty);
        return newPath;
    }

    public string CreateGvl(string parentPath, string name)
    {
        CallLog.Add($"CreateGvl(parent={parentPath}, name={name})");
        if (CreateGvlThrows is not null) throw CreateGvlThrows;
        var newPath = parentPath + "^" + name;
        Pous.Add(new PouEntry(newPath, name, PouKind.Gvl, null));
        // Mirrors the COM session: an ordinary GVL (615) always starts with the plain skeleton. The
        // earlier fake returned `VAR_GLOBAL PERSISTENT` for persistent=true, which hid that the real
        // nSubType 629 produced a Parameter List (`VAR_GLOBAL CONSTANT`).
        var skeleton = "VAR_GLOBAL\nEND_VAR";
        GvlDeclarationsByPath[newPath] = skeleton;
        PouBodiesByPath[newPath] = new PouBody(skeleton, string.Empty, string.Empty);
        return newPath;
    }

    public Exception? CreatePlcChildThrows { get; set; }

    public string CreatePlcChild(string parentPath, string name, int subType, string label)
    {
        CallLog.Add($"CreatePlcChild(parent={parentPath}, name={name}, subType={subType}, label={label})");
        if (CreatePlcChildThrows is not null) throw CreatePlcChildThrows;
        var newPath = parentPath + "^" + name;
        string? kind = PlcCodeClassification.PouKindOf(subType);
        if (kind is not null) Pous.Add(new PouEntry(newPath, name, kind, null, subType));
        PlcNodeItemTypes[newPath] = subType;
        if (subType == PlcItemType.GvlParamList)
        {
            var skeleton = "VAR_GLOBAL CONSTANT\nEND_VAR";
            GvlDeclarationsByPath[newPath] = skeleton;
            PouBodiesByPath[newPath] = new PouBody(skeleton, string.Empty, string.Empty);
        }
        return newPath;
    }

    public string CreateInterface(string parentPath, string name)
    {
        CallLog.Add($"CreateInterface(parent={parentPath}, name={name})");
        if (CreateInterfaceThrows is not null) throw CreateInterfaceThrows;
        var newPath = parentPath + "^" + name;
        Pous.Add(new PouEntry(newPath, name, PouKind.Interface, null));
        var skeleton = $"INTERFACE {name}\nEND_INTERFACE";
        // Mirror the COM session: the node exists with a declaration the coordinator
        // can overwrite via WritePouBody (declaration-only).
        PouBodiesByPath[newPath] = new PouBody(skeleton, string.Empty, string.Empty);
        return newPath;
    }
}
