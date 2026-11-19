using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// The project datatypes of an HMI project (<c>Properties/tchmi.project.Schema.json</c> and
/// <c>tchmi.project.Schema.tpl.json</c>): what TE2000's "Create new project datatype" writes, and what a
/// user-control parameter references as <c>tchmi:project#/definitions/&lt;Name&gt;</c>. Read from disk; no
/// Automation Interface member exposes them.
/// </summary>
internal sealed partial class HmiCoordinator
{
    public (string Summary, string Json) ListProjectDatatypes(string? hmiProject)
    {
        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        HmiSchemaFile schema = HmiProjectDatatypes.Read(HmiProjectDatatypes.SchemaPath(project.ProjectDirectory));
        if (!schema.Exists)
            return ($"[Error] '{schema.Path}' does not exist — every TwinCAT HMI project has it. Is " +
                    $"'{project.ProjectDirectory}' the project folder?", "{}");
        if (schema.Error is not null)
            return ($"[Error] '{schema.Path}' could not be read: {schema.Error}. Nothing is reported rather than " +
                    "an incomplete list.", "{}");
        HmiSchemaFile template = HmiProjectDatatypes.Read(HmiProjectDatatypes.TemplatePath(project.ProjectDirectory));

        var (sources, sourceNote) = RemoteTypeSources(project);
        var templateNames = template.Raw?.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var definitions = schema.Definitions.Select(d => new
        {
            d.Name,
            d.Kind,
            d.Namespace,
            source = d.Namespace is null ? null
                : sources is null ? "unknown"
                : sources.TryGetValue(d.Namespace, out string? s) ? s : "none",
            inTemplate = templateNames?.Contains(d.Name),
            d.Refs,
            d.DanglingRefs,
        }).ToList();

        HmiSchemaDiff? diff = template.Raw is null ? null : HmiProjectDatatypes.Diff(schema.Raw!, template.Raw);
        int dangling = schema.Definitions.Count(d => d.DanglingRefs.Count > 0);
        var byKind = schema.Definitions.GroupBy(d => d.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key}");

        string summary = schema.Definitions.Count == 0
            ? $"HMI project '{project.Name}' has no project datatype ({HmiProjectDatatypes.SchemaFileName} has an empty 'definitions')."
            : $"{schema.Definitions.Count} project datatype(s) in '{project.Name}' ({string.Join(", ", byKind)}).";
        if (dangling > 0)
            summary += $" WARNING: {dangling} definition(s) reference a project type that does not exist (dangling $ref): " +
                       string.Join("; ", schema.Definitions.Where(d => d.DanglingRefs.Count > 0).Take(5)
                           .Select(d => $"{d.Name} → {string.Join(", ", d.DanglingRefs)}")) +
                       (dangling > 5 ? $" … +{dangling - 5} more." : ".") +
                       " TE2000 leaves these behind when a type is deleted; restore or remove them with the project closed.";
        summary += !template.Exists
            ? $" {HmiProjectDatatypes.TemplateFileName} does not exist."
            : template.Error is not null
                ? $" WARNING: {HmiProjectDatatypes.TemplateFileName} could not be read ({template.Error}) — whether it matches is unknown."
                : diff!.Same
                    ? $" {HmiProjectDatatypes.TemplateFileName} has the same definitions."
                    : $" WARNING: {HmiProjectDatatypes.TemplateFileName} differs — " + DescribeDiff(diff) + ".";
        if (sourceNote is not null) summary += " " + sourceNote;

        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            schemaFile = schema.Path,
            templateFile = template.Exists ? template.Path : null,
            templateReadable = template.Readable,
            count = schema.Definitions.Count,
            danglingCount = dangling,
            definitions,
            templateDiff = diff,
        }, JsonOpts);
        return (summary, json);
    }

    private static string DescribeDiff(HmiSchemaDiff diff)
    {
        var parts = new List<string>();
        if (diff.OnlyInSchema.Count > 0) parts.Add($"only in {HmiProjectDatatypes.SchemaFileName}: {string.Join(", ", diff.OnlyInSchema.Take(5))}");
        if (diff.OnlyInTemplate.Count > 0) parts.Add($"only in {HmiProjectDatatypes.TemplateFileName}: {string.Join(", ", diff.OnlyInTemplate.Take(5))}");
        if (diff.Different.Count > 0) parts.Add($"different content: {string.Join(", ", diff.Different.Take(5))}");
        return string.Join("; ", parts);
    }

    /// <summary>Where a type-name namespace comes from: an attached TMC file (engineering-side) or an ADS
    /// runtime of the server. Null map = unknown (both sources unreadable), with the reason as a note.</summary>
    private (Dictionary<string, string>? Sources, string? Note) RemoteTypeSources(HmiProjectEntry project)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var (attachments, attachError) = ReadAttachments(project);
        if (attachments is null) notes.Add($"attached TMC files unknown ({WithoutErrorPrefix(attachError)})");
        else foreach (HmiTmcAttachment a in attachments) sources.TryAdd(a.RootNamespace, "attachedTmc");
        var (runtimes, runtimeError) = ReadAdsRuntimes(project.Name);
        if (runtimes is null) notes.Add($"ADS runtimes unknown ({WithoutErrorPrefix(runtimeError)})");
        else foreach (HmiAdsRuntime r in runtimes) sources.TryAdd(r.Name, "adsRuntime");
        if (attachments is null && runtimes is null) return (null, "Source of the type namespaces unknown: " + string.Join("; ", notes) + ".");
        return (sources, notes.Count == 0 ? null : "Note: " + string.Join("; ", notes) + ".");
    }
}
