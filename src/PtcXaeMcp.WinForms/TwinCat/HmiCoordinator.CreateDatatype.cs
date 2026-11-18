using System.Text.Json;
using System.Text.Json.Nodes;
using PtcXaeMcp.WinForms.Security;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// hmi-offline-types: TE2000's "Create new project datatype → based on a remote type" for a PLC type of an
/// attached TMC file. There is no Automation Interface member for it, so this writes what the engineering writes
/// (decompiled 14.17.22.1, <c>SerializeProjectDatatypes</c>): the definitions into
/// <c>Properties/tchmi.project.Schema.json</c>, then the server request <c>RefreshDefinitions "project"</c>, because
/// nothing watches the file. <c>tchmi.project.Schema.tpl.json</c> is written as well when it exists (engineering 1.12
/// was seen keeping both). Success only when every written definition reads back from every written file.
/// </summary>
internal sealed partial class HmiCoordinator
{
    internal const string ProjectDefinitionsDomain = "project";

    /// <summary>Reads a .tmc file. A seam so tests can prove a refused path is never opened.</summary>
    internal Func<string, string> TmcReader { get; init; } = File.ReadAllText;

    /// <summary>
    /// Fixed, fail-closed order: parse remoteType → resolve → the namespace must be an attached TMC → its file
    /// against <see cref="PathPolicy"/> → build the definitions from the TMC → read both schema files (an unreadable
    /// one refuses) → duplicate rules per file → popup → write → refresh → read-back.
    /// </summary>
    public (string Summary, string Json) CreateProjectDatatype(string? hmiProject, string? remoteType, int confirmTimeoutSeconds)
    {
        string wanted = remoteType?.Trim() ?? "";
        int dot = wanted.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == wanted.Length - 1)
            return ("[Error] remoteType is required as '<virtual PLC>.<Type>', e.g. 'PLC_TMC.ST_Recipe' — the virtual PLC " +
                    "name a TMC file is attached under (twincat_hmi_list_tmc_files) and a STRUCT or ENUM of that PLC.", "{}");
        string ns = wanted[..dot];
        string typeName = wanted[(dot + 1)..];
        if (!HmiTmcPaths.IsValidVirtualPlcName(ns))
            return ($"[Error] '{ns}' cannot be a virtual PLC name — letters, digits, '_' or '-' only.", "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        var (attachments, attachError) = ReadAttachments(project);
        if (attachments is null) return (attachError!, "{}");
        HmiTmcAttachment? source = attachments.FirstOrDefault(a => a.RootNamespace.Equals(ns, StringComparison.OrdinalIgnoreCase));
        if (source is null)
            return ($"[Error] '{ns}' is not a TMC file attached to '{project.Name}' " +
                    (attachments.Count == 0 ? "(none is attached)" : $"(attached: {string.Join(", ", attachments.Select(a => a.RootNamespace))})") +
                    ". Attach the PLC project's .tmc first (twincat_hmi_attach_tmc_file).", "{}");
        if (!source.FileExists)
            return ($"[Error] The TMC file attached as '{source.RootNamespace}' no longer exists ('{source.ResolvedPath}'). " +
                    "Rebuild the PLC project or attach the file again.", "{}");
        if (McpPathGuard.CheckOptionalFile(source.ResolvedPath, SolutionDirectory(), "tmcFile") is { } pathError)
            return (pathError, "{}");

        string tmcXml;
        try { tmcXml = TmcReader(source.ResolvedPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"[Error] '{source.ResolvedPath}' could not be read ({ex.Message}).", "{}");
        }
        var (generated, buildError) = HmiRemoteTypes.Build(tmcXml, source.RootNamespace, typeName);
        if (generated is null)
            return ($"[Error] No project datatype for '{wanted}': {buildError}. Nothing was written.", "{}");

        // Both files are planned before anything is written, so a refusal in the second never leaves the first changed.
        var files = new List<(HmiSchemaFile File, HmiProjectDatatypes.CreatePlan Plan)>();
        foreach (string path in new[] { HmiProjectDatatypes.SchemaPath(project.ProjectDirectory), HmiProjectDatatypes.TemplatePath(project.ProjectDirectory) })
        {
            HmiSchemaFile file = HmiProjectDatatypes.Read(path);
            if (!file.Exists)
            {
                if (path.EndsWith(HmiProjectDatatypes.TemplateFileName, StringComparison.OrdinalIgnoreCase)) continue;
                return ($"[Error] '{path}' does not exist — every TwinCAT HMI project has it. Nothing was written.", "{}");
            }
            if (file.Error is not null)
                return ($"[Error] '{path}' could not be read ({file.Error}). Nothing was written.", "{}");
            HmiProjectDatatypes.CreatePlan plan = HmiProjectDatatypes.PlanCreate(file.Raw!, generated);
            if (plan.Error is not null)
                return ($"[Error] {Path.GetFileName(path)}: {plan.Error}. Nothing was written.", "{}");
            files.Add((file, plan));
        }

        var main = files[0].Plan;
        var request = new HmiWriteRequest(HmiWriteKind.CreateProjectDatatype, project.Name, project.ProjectDirectory,
            generated.RootName, null,
            NewValue: $"{main.ToAdd.Count} definition(s): {string.Join(", ", main.ToAdd.Select(d => d.Key))}" +
                      (main.Reused.Count == 0 ? "" : $"; reused (identical): {string.Join(", ", main.Reused)}") +
                      $" — from {source.ResolvedPath}, into {string.Join(" and ", files.Select(f => Path.GetFileName(f.File.Path)))}",
            CurrentValue: $"{files[0].File.Definitions.Count} project datatype(s), '{generated.RootName}' not among them",
            WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        var written = new List<string>();
        foreach (var (file, plan) in files)
        {
            string? text;
            try { text = HmiProjectDatatypes.AddDefinitions(File.ReadAllText(file.Path), plan.ToAdd); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ($"[Error] Reading '{file.Path}' again failed ({ex.Message}). " + WrittenSoFar(written), "{}");
            }
            if (text is null)
                return ($"[Error] '{file.Path}' changed into something that is not a schema object since it was checked. " +
                        WrittenSoFar(written), "{}");
            try { HmiProjectDatatypes.WriteAtomically(file.Path, text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ($"[Error] Writing '{file.Path}' failed ({ex.Message}). " + WrittenSoFar(written), "{}");
            }
            written.Add(file.Path);
        }

        // Read-back from disk, independent of what was just serialised.
        foreach (var (file, plan) in files)
        {
            HmiSchemaFile after = HmiProjectDatatypes.Read(file.Path);
            var missing = plan.ToAdd.Where(d => after.Raw is null || !after.Raw.TryGetPropertyValue(d.Key, out JsonNode? n)
                                                 || !JsonNode.DeepEquals(n, d.Value)).Select(d => d.Key).ToList();
            if (after.Raw is null || missing.Count > 0)
                return ($"[Error] '{file.Path}' was written, but on read-back " +
                        (after.Raw is null ? $"it is unreadable ({after.Error})" : $"these definitions are missing or differ: {string.Join(", ", missing)}") +
                        " — not treated as success.", "{}");
        }

        HmiOperationResult refresh;
        try { refresh = Session.RefreshHmiDefinitions(project.Name, ProjectDefinitionsDomain); }
        catch (Exception ex) when (IsHmiFailure(ex)) { refresh = new HmiOperationResult(false, ex.Message); }
        string refreshText = refresh.Ok
            ? "the HMI server was asked to read the project datatypes again (RefreshDefinitions \"project\")"
            : $"WARNING: RefreshDefinitions \"project\" failed ({refresh.LastError ?? "no error text"}) — the engineering " +
              "sees the new types only after the project is reloaded or the HMI server restarted";
        string? dialog = Session.FindHmiBlockingDialog();

        string summary = $"Project datatype '{generated.RootName}' created in '{project.Name}' from the PLC type '{typeName}' " +
                         $"of {source.ResolvedPath} — {Describe(outcome)}, verified by reading " +
                         $"{string.Join(" and ", files.Select(f => Path.GetFileName(f.File.Path)))} back. " +
                         $"Added: {string.Join(", ", main.ToAdd.Select(d => d.Key))}" +
                         (main.Reused.Count == 0 ? "" : $"; reused (identical, already there): {string.Join(", ", main.Reused)}") +
                         $". Use it as \"type\": \"{HmiProjectDatatypes.ProjectRefPrefix}{generated.RootName}\" for a user-control " +
                         $"parameter. {char.ToUpperInvariant(refreshText[0])}{refreshText[1..]}. Changed files (review before " +
                         $"committing): {string.Join(", ", written)}." +
                         (dialog is null ? "" : " " + DialogHint(dialog));
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            remoteType = generated.RootName,
            parameterType = HmiProjectDatatypes.ProjectRefPrefix + generated.RootName,
            tmcFile = source.ResolvedPath,
            added = main.ToAdd.Select(d => d.Key),
            reused = main.Reused,
            changedFiles = written,
            refreshed = refresh.Ok,
            refreshError = refresh.Ok ? null : refresh.LastError,
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts);
        return (summary, json);
    }

    private static string WrittenSoFar(List<string> written) => written.Count == 0
        ? "Nothing was written."
        : $"Already written: {string.Join(", ", written)} — check it with twincat_hmi_list_project_datatypes.";
}
