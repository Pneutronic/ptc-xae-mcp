using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    public List<LibraryReference> LibraryReferences { get; } = new();
    public List<InstalledLibrary> InstalledLibraries { get; } = new();
    public Exception? ListReferencesThrows { get; set; }
    public Exception? ListInstalledThrows { get; set; }

    public LibrarySignatureExport LibrarySignatures { get; set; } = new("", "References", "TcXaeShell 64-bit");
    public Exception? ProduceLibrarySignaturesThrows { get; set; }

    public LibrarySignatureExport ProduceLibrarySignatures(string? plcProjectName = null)
    {
        CallLog.Add($"ProduceLibrarySignatures({plcProjectName})");
        if (ProduceLibrarySignaturesThrows is not null) throw ProduceLibrarySignaturesThrows;
        return LibrarySignatures;
    }

    public IReadOnlyList<LibraryReference> ListLibraryReferences(string? plcProjectName = null)
    {
        CallLog.Add($"ListLibraryReferences({plcProjectName})");
        if (ListReferencesThrows is not null) throw ListReferencesThrows;
        return LibraryReferences;
    }

    public IReadOnlyList<InstalledLibrary> ListInstalledLibraries()
    {
        CallLog.Add("ListInstalledLibraries()");
        if (ListInstalledThrows is not null) throw ListInstalledThrows;
        return InstalledLibraries;
    }

    public Exception? AddLibraryReferenceThrows { get; set; }
    public Exception? RemoveLibraryReferenceThrows { get; set; }
    public Exception? SetLibraryVersionThrows { get; set; }

    public IReadOnlyList<LibraryReference> AddLibraryReference(string name, string version, string? company, bool asPlaceholder, string? plcProjectName = null)
    {
        CallLog.Add($"AddLibraryReference({name},{version},{company},{asPlaceholder}{Plc(plcProjectName)})");
        if (AddLibraryReferenceThrows is not null) throw AddLibraryReferenceThrows;
        if (NoOpMutations.Contains(nameof(AddLibraryReference))) return LibraryReferences;
        LibraryReferences.Add(new LibraryReference(
            Name: name, Version: AddLibraryResolvesToVersion ?? version, Publisher: company ?? string.Empty,
            Placeholder: name,
            EffectiveResolution: $"{name}, {version} ({company ?? string.Empty})",
            Source: LibrarySource.Xae,
            FilePinnedVersion: null,
            VersionMatchesFile: null));
        return LibraryReferences;
    }

    public IReadOnlyList<LibraryReference> RemoveLibraryReference(string name, string? plcProjectName = null)
    {
        CallLog.Add($"RemoveLibraryReference({name}{Plc(plcProjectName)})");
        if (RemoveLibraryReferenceThrows is not null) throw RemoveLibraryReferenceThrows;
        if (NoOpMutations.Contains(nameof(RemoveLibraryReference))) return LibraryReferences;
        // Like ITcPlcLibraryManager.RemoveReference: addressed by the placeholder name.
        LibraryReferences.RemoveAll(r => string.Equals(r.Placeholder, name, StringComparison.OrdinalIgnoreCase));
        return LibraryReferences;
    }

    public IReadOnlyList<LibraryReference> SetLibraryVersion(string name, string version, string? company, string? plcProjectName = null)
    {
        CallLog.Add($"SetLibraryVersion({name},{version},{company}{Plc(plcProjectName)})");
        if (SetLibraryVersionThrows is not null) throw SetLibraryVersionThrows;
        int idx = LibraryReferences.FindIndex(r =>
            string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new InvalidOperationException($"Reference '{name}' not found.");
        var old = LibraryReferences[idx];
        LibraryReferences[idx] = old with { Version = version, Publisher = company ?? old.Publisher };
        return LibraryReferences;
    }

    // Logged only when given, so the log of a call without a PLC name stays as it was.
    private static string Plc(string? plcProjectName) => plcProjectName is null ? "" : $",plc={plcProjectName}";

    public Exception? InstallLibraryThrows { get; set; }
    public IReadOnlyList<InstalledLibrary> InstallLibrary(string libraryPath, bool overwrite)
    {
        CallLog.Add($"InstallLibrary({libraryPath},{overwrite})");
        if (InstallLibraryThrows is not null) throw InstallLibraryThrows;
        if (!NoOpMutations.Contains(nameof(InstallLibrary)))
            InstalledLibraries.Add(new InstalledLibrary(Path.GetFileNameWithoutExtension(libraryPath), "1.0.0.0", "Fake", "Fake"));
        return InstalledLibraries;
    }

    public Exception? SaveAsLibraryThrows { get; set; }
    public string FakeSaveAsLibraryResult { get; set; } = "Saved library.";
    public string SaveAsLibrary(string outputPath, bool install)
    {
        CallLog.Add($"SaveAsLibrary({outputPath},{install})");
        if (SaveAsLibraryThrows is not null) throw SaveAsLibraryThrows;
        if (!NoOpMutations.Contains(nameof(SaveAsLibrary)))
        {
            File.WriteAllText(outputPath, "fake library");
            if (install && !SaveAsLibraryInstallIsNoOp)
                InstalledLibraries.Add(new InstalledLibrary(Path.GetFileNameWithoutExtension(outputPath), "1.0.0.0", "Fake", "Fake"));
        }
        return FakeSaveAsLibraryResult;
    }

    public Exception? SaveAsLibrary2Throws { get; set; }
    /// <summary>SaveAsLibrary2 is not on the XAE's IDispatch (an older XAE).</summary>
    public bool SaveAsLibrary2NotOffered { get; set; }
    /// <summary>What SaveAsLibrary2 writes ("" = an empty file).</summary>
    public string SaveAsLibrary2Content { get; set; } = "fake compiled library";
    public string SaveAsLibrary2(string outputPath, bool encrypt, bool install)
    {
        CallLog.Add($"SaveAsLibrary2({outputPath},{encrypt},{install})");
        if (SaveAsLibrary2NotOffered)
            throw new NotSupportedException("ITcPlcIECProject6.SaveAsLibrary2 is not offered by this XAE (fake).");
        if (SaveAsLibrary2Throws is not null) throw SaveAsLibrary2Throws;
        if (File.Exists(outputPath))
            throw new InvalidOperationException($"ArgumentException: file already exists: {outputPath} (fake)");
        if (!NoOpMutations.Contains(nameof(SaveAsLibrary2)))
        {
            File.WriteAllText(outputPath, SaveAsLibrary2Content);
            if (install && !SaveAsLibraryInstallIsNoOp)
                InstalledLibraries.Add(new InstalledLibrary(Path.GetFileNameWithoutExtension(outputPath), "1.0.0.0", "Fake", "Fake"));
        }
        return $"Saved compiled library: {outputPath}";
    }

    public List<LibraryParameter> LibraryParameters { get; } = new();
    public Exception? SetLibraryParameterThrows { get; set; }

    public IReadOnlyList<LibraryParameter> ListLibraryParameters(string placeholder)
    {
        CallLog.Add($"ListLibraryParameters({placeholder})");
        return LibraryParameters
            .Where(p => string.Equals(p.Placeholder, placeholder, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public IReadOnlyList<LibraryParameter> SetLibraryParameter(
        string placeholder, string listName, string key, string value)
    {
        CallLog.Add($"SetLibraryParameter({placeholder},{listName},{key},{value})");
        if (SetLibraryParameterThrows is not null) throw SetLibraryParameterThrows;
        int idx = LibraryParameters.FindIndex(p =>
            string.Equals(p.Placeholder, placeholder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.ListName, listName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            LibraryParameters[idx] = LibraryParameters[idx] with { Value = value };
        else
            LibraryParameters.Add(new LibraryParameter(placeholder, listName, key, value));
        return ListLibraryParameters(placeholder);
    }
}
