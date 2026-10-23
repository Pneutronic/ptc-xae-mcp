using System.Security.Cryptography;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>A library repository; <see cref="Source"/> is "com" (library manager) or "filesystem-fallback".</summary>
internal sealed record LibraryRepositoryEntry(string Name, string Folder, string Source = LibraryRepositorySource.Com);

internal static class LibraryRepositorySource
{
    public const string Com = "com";
    public const string FilesystemFallback = "filesystem-fallback";
}

internal sealed record LibraryRepositoryListing(IReadOnlyList<LibraryRepositoryEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>A concrete library (name, version, distributor) as the library manager reports it.</summary>
internal sealed record ResolvedLibrary(string Name, string Version, string Distributor);

/// <summary>
/// What the library manager says about one placeholder reference: the effective resolution (the library the project
/// actually compiles against), the current library and the default resolution; each null when it was not readable.
/// </summary>
internal sealed record PlaceholderResolution(string Placeholder, ResolvedLibrary? Effective, ResolvedLibrary? Current, ResolvedLibrary? Default);

/// <summary>The references, placeholder resolutions and installed libraries of one PLC project.</summary>
internal sealed record PlcLibraryContext(
    string PlcProject, IReadOnlyList<LibraryReference> References,
    IReadOnlyList<PlaceholderResolution> Placeholders, IReadOnlyList<InstalledLibrary> Installed);

internal static class LibraryResolutionKind
{
    public const string Fixed = "fixed";
    public const string NewestInstalled = "newest-installed";
    public const string Effective = "effective";
    public const string Unresolved = "unresolved";
}

internal sealed record ResolvedReference(string? Name, string? Version, string? Distributor, string Resolution);

internal sealed record ResolvedLibraryReference(LibraryReference Reference, ResolvedReference Resolved);

internal sealed record LibraryRepositoryFile(string Path, string Sha256, long SizeBytes);

/// <summary>The library files found for one resolved library; <see cref="SearchedPaths"/> is always the folders looked in.</summary>
internal sealed record LibraryFileLookup(bool FileFound, IReadOnlyList<LibraryRepositoryFile> Files, IReadOnlyList<string> SearchedPaths);

internal sealed record LibraryReferenceFileInfo(
    string PlcProject, string Placeholder, string Name, string RequestedVersion,
    ResolvedReference Resolved, LibraryFileLookup Lookup);

internal sealed record LibraryRepositoriesResult(
    IReadOnlyList<LibraryRepositoryEntry> Repositories, IReadOnlyList<LibraryReferenceFileInfo>? References,
    IReadOnlyList<string> Warnings);

/// <summary>Version-number comparison of library versions such as "3.4.10.0".</summary>
internal static class LibraryVersion
{
    public static int Compare(string? a, string? b)
    {
        string[] pa = (a ?? string.Empty).Trim().Split('.');
        string[] pb = (b ?? string.Empty).Trim().Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            string x = i < pa.Length ? pa[i] : "0";
            string y = i < pb.Length ? pb[i] : "0";
            bool nx = x.Length > 0 && x.All(char.IsAsciiDigit);
            bool ny = y.Length > 0 && y.All(char.IsAsciiDigit);
            int c;
            if (nx && ny)
            {
                string tx = x.TrimStart('0'), ty = y.TrimStart('0');
                c = tx.Length != ty.Length ? tx.Length.CompareTo(ty.Length) : string.CompareOrdinal(tx, ty);
            }
            else if (nx != ny) c = nx ? 1 : -1; // a numeric segment ranks above a text one
            else c = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c < 0 ? -1 : 1;
        }
        return 0;
    }
}

/// <summary>Decides which concrete library a reference resolves to.</summary>
internal static class LibraryResolver
{
    /// <summary>
    /// Order: (1) the library manager's effective resolution of the placeholder, (2) the reference's own concrete version,
    /// (3) for "*"/"newest" the highest installed version of that name (and distributor, if given), (4) unresolved.
    /// </summary>
    public static ResolvedReference Resolve(
        LibraryReference reference, PlaceholderResolution? fromManager, IReadOnlyList<InstalledLibrary> installed)
    {
        var effective = fromManager?.Effective;
        if (effective is not null && !string.IsNullOrWhiteSpace(effective.Version) && !string.IsNullOrWhiteSpace(effective.Name))
            return new ResolvedReference(effective.Name, effective.Version, effective.Distributor, LibraryResolutionKind.Effective);

        if (!reference.IsAnyVersion && !string.IsNullOrWhiteSpace(reference.Version))
            return new ResolvedReference(reference.Name, reference.Version, NullIfEmpty(reference.Publisher), LibraryResolutionKind.Fixed);

        var best = installed
            .Where(l => string.Equals(l.Name, reference.Name, StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrWhiteSpace(reference.Publisher)
                            || string.Equals(l.Publisher, reference.Publisher, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(l => l.Version, Comparer<string>.Create(LibraryVersion.Compare))
            .FirstOrDefault();
        return best is null
            ? new ResolvedReference(null, null, null, LibraryResolutionKind.Unresolved)
            : new ResolvedReference(best.Name, best.Version, NullIfEmpty(best.Publisher), LibraryResolutionKind.NewestInstalled);
    }

    /// <summary>The manager's resolution entry for a reference: matched by placeholder name, then by library name.</summary>
    public static PlaceholderResolution? FindResolution(IReadOnlyList<PlaceholderResolution> all, LibraryReference reference) =>
        all.FirstOrDefault(p => string.Equals(p.Placeholder, reference.Placeholder, StringComparison.OrdinalIgnoreCase))
        ?? all.FirstOrDefault(p => string.Equals(p.Placeholder, reference.Name, StringComparison.OrdinalIgnoreCase));

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Finds the library files of a resolved library in the library repositories and hashes them.</summary>
internal static class LibraryRepositoryFiles
{
    public static bool IsLibraryFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".library", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".compiled-library", StringComparison.OrdinalIgnoreCase);
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Looks in <c>&lt;repository&gt;\&lt;distributor&gt;\&lt;name&gt;\&lt;version&gt;</c> of every repository; without a distributor,
    /// in every distributor folder. The hash is that of the file itself.
    /// </summary>
    public static LibraryFileLookup Find(IEnumerable<LibraryRepositoryEntry> repositories, string? distributor, string name, string version)
    {
        var searched = new List<string>();
        var files = new List<LibraryRepositoryFile>();
        foreach (var repo in repositories)
        {
            if (string.IsNullOrWhiteSpace(repo.Folder)) continue;
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(distributor))
                candidates.Add(Path.Combine(repo.Folder, distributor, name, version));
            else if (Directory.Exists(repo.Folder))
            {
                try
                {
                    foreach (string d in Directory.EnumerateDirectories(repo.Folder))
                        candidates.Add(Path.Combine(d, name, version));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else candidates.Add(Path.Combine(repo.Folder, "*", name, version));

            foreach (string dir in candidates)
            {
                searched.Add(dir);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir).Where(IsLibraryFile).Order(StringComparer.OrdinalIgnoreCase))
                        files.Add(new LibraryRepositoryFile(file, Sha256Of(file), new FileInfo(file).Length));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return new LibraryFileLookup(files.Count > 0, files, searched);
    }
}

/// <summary>Puts references, the manager's resolutions, the installed libraries and the repositories together.</summary>
internal static class LibraryRepositoryReport
{
    public static IReadOnlyList<LibraryReferenceFileInfo> Build(
        IEnumerable<PlcLibraryContext> contexts, IReadOnlyList<LibraryRepositoryEntry> repositories)
    {
        var result = new List<LibraryReferenceFileInfo>();
        foreach (var ctx in contexts)
        {
            foreach (var reference in ctx.References)
            {
                var resolved = LibraryResolver.Resolve(reference, LibraryResolver.FindResolution(ctx.Placeholders, reference), ctx.Installed);
                var lookup = resolved.Version is null || resolved.Name is null
                    ? new LibraryFileLookup(false, [], [])
                    : LibraryRepositoryFiles.Find(repositories, resolved.Distributor, resolved.Name, resolved.Version);
                result.Add(new LibraryReferenceFileInfo(ctx.PlcProject, reference.Placeholder, reference.Name, reference.Version, resolved, lookup));
            }
        }
        return result;
    }
}
