using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>sbom: the library repositories and, on request, the resolved library files of every reference with their SHA-256.</summary>
internal sealed class LibraryRepositoryCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string List(bool includeFiles, string? plcProjectPath)
    {
        string? guard = SessionGuards.RequireOpenSolution(session);
        if (guard is not null) return guard;

        try
        {
            var listing = session.ListLibraryRepositoryEntries();
            var repositories = listing.Entries;
            var warnings = new List<string>(listing.Warnings);
            IReadOnlyList<LibraryReferenceFileInfo>? references = null;
            if (includeFiles)
            {
                var contexts = session.ReadPlcLibraryContexts(string.IsNullOrWhiteSpace(plcProjectPath) ? null : plcProjectPath.Trim());
                foreach (var c in contexts.Where(c => c.Placeholders.Count == 0 && c.References.Any(r => r.Source == LibrarySource.Xae)))
                    warnings.Add($"{c.PlcProject}: the library manager exposes no placeholder resolution; versions fall back to the reference or the newest installed.");
                references = LibraryRepositoryReport.Build(contexts, repositories);
            }
            return Format(new LibraryRepositoriesResult(repositories, references, warnings));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpErrors.EnsureError(ex.Message);
        }
    }

    internal static string Format(LibraryRepositoriesResult r) => JsonSerializer.Serialize(new
    {
        success = true,
        repositories = r.Repositories.Select(x => new { name = x.Name, folder = x.Folder, folderExists = Directory.Exists(x.Folder), source = x.Source }),
        references = r.References?.Select(x => new
        {
            plcProject = x.PlcProject,
            placeholder = x.Placeholder,
            name = x.Name,
            requestedVersion = x.RequestedVersion,
            resolvedName = x.Resolved.Name,
            resolvedVersion = x.Resolved.Version,
            resolvedDistributor = x.Resolved.Distributor,
            resolution = x.Resolved.Resolution,
            fileFound = x.Lookup.FileFound,
            files = x.Lookup.Files.Select(f => new { path = f.Path, sha256 = f.Sha256, hashScope = "file", sizeBytes = f.SizeBytes }),
            searchedPaths = x.Lookup.SearchedPaths,
        }),
        warnings = r.Warnings,
    }, JsonOpts);
}
