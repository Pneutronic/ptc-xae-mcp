namespace PtcXaeMcp.WinForms.TwinCat;

// Orchestrates the feature/nc-axes (Motion) tools: list NC-Tasks/axes,
// create an NC-Task, create an axis under a task, configure axis
// parameters. Same shape as the other coordinators — cheap input
// validation, SessionGuards.RequireOpenSolution, delegate to the session,
// turn exceptions into "[Error] ..." summaries. The browse-never-name
// tree walk lives in the session; this layer is COM-free and unit-tested
// against FakeTwinCatSession.
internal sealed class NcAxesCoordinator(ITwinCatSession session)
{
    public (string Summary, IReadOnlyList<NcTaskEntry> Tasks) ListNcTasksAxes()
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, Array.Empty<NcTaskEntry>());

        IReadOnlyList<NcTaskEntry> tasks;
        try { tasks = session.ListNcTasksAxes(); }
        catch (Exception ex) { return (McpErrors.EnsureError(ComCall.Describe(ex)), Array.Empty<NcTaskEntry>()); }

        if (tasks.Count == 0)
            return ("No NC-Tasks found in the open project.", tasks);

        int axisCount = tasks.Sum(t => t.Axes.Count);
        return ($"Found {tasks.Count} NC-Task{(tasks.Count == 1 ? "" : "s")} " +
                $"with {axisCount} ax{(axisCount == 1 ? "is" : "es")} total.", tasks);
    }

    public string CreateNcTask(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "[Error] name is required.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        try
        {
            var path = session.CreateNcTask(name.Trim());
            return $"Created NC-Task at {path}.";
        }
        catch (Exception ex)
        {
            // Empirically confirmed on TC3 4026.22 (live SampleProject fixture,
            // 2026-05-18): TINC.CreateChild(<any name>, 1) throws COM
            // "Item already exists!" whenever an NC-Task already exists under
            // TINC. The Infosys 242735499 snippet only covers a green-field
            // project (no NC-Task yet). Adding a *second* NC-Task via the
            // Automation Interface on 4026.22 is a separate, unsupported path —
            // tracked as a follow-up (same pattern as the pou-handling METHOD
            // descope). Rewrite the raw COM throw into actionable guidance.
            if (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                return "[Error] TwinCAT 4026.22 only creates an NC-Task via the " +
                       "Automation Interface when none exists yet (green-field project). " +
                       "This project already has an NC-Task — reuse it (see " +
                       "twincat_list_nc_tasks_axes) and add axes with twincat_create_axis, " +
                       "or add the extra NC-Task in TwinCAT XAE. Creating a second NC-Task " +
                       "through the AI is not supported on this build.";
            return McpErrors.EnsureError(ComCall.Describe(ex));
        }
    }

    public string CreateAxis(string ncTaskPath, string axisName)
    {
        if (string.IsNullOrWhiteSpace(ncTaskPath))
            return "[Error] ncTaskPath is required (get it from twincat_list_nc_tasks_axes).";
        if (string.IsNullOrWhiteSpace(axisName))
            return "[Error] axisName is required.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        try
        {
            var path = session.CreateAxis(ncTaskPath.Trim(), axisName.Trim());
            return $"Created axis at {path}.";
        }
        catch (Exception ex)
        {
            return McpErrors.EnsureError(ComCall.Describe(ex));
        }
    }

    public string ConfigureAxis(string axisPath, IReadOnlyDictionary<string, string> fields)
    {
        if (string.IsNullOrWhiteSpace(axisPath))
            return "[Error] axisPath is required (get it from twincat_list_nc_tasks_axes).";
        if (fields is null || fields.Count == 0)
            return $"[Error] Supply at least one axis field. Valid: {string.Join(", ", NcAxisField.Keys)}.";

        foreach (var key in fields.Keys)
            if (!NcAxisField.IsKnownKey(key))
                return $"[Error] Unknown axis field '{key}'. Valid: {string.Join(", ", NcAxisField.Keys)}, " +
                       $"{NcAxisField.AxisTypeKey}.";

        // Unknown axisType → [Error] before the session is touched (issue #148).
        foreach (var (key, value) in fields)
            if (key.Equals(NcAxisField.AxisTypeKey, StringComparison.OrdinalIgnoreCase) &&
                !NcAxisField.AxisTypeMap.ContainsKey((value ?? string.Empty).Trim()))
                return $"[Error] Unknown axisType '{value}'. Expected {string.Join(", ", NcAxisField.AxisTypeMap.Keys)}.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        string applied;
        try { applied = session.ConfigureAxis(axisPath.Trim(), fields); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        // D-9 (issue #152): ConsumeXml returning is not proof. Re-read the axis (a fresh lookup and
        // ProduceXml in a separate call) and compare every requested field.
        NcAxisParams after;
        try { after = session.ReadAxisParams(axisPath.Trim()); }
        catch (Exception ex)
        {
            return McpErrors.EnsureError($"The axis parameters were sent ({applied}), but the read-back of '{axisPath}' " +
                                         $"failed, so they are not confirmed: {ComCall.Describe(ex)}");
        }
        var mismatches = NcAxisXml.VerifyApplied(fields, after.Fields);
        if (mismatches.Count > 0)
            return $"[Error] configure_axis on '{axisPath}' did not land: ProduceXml after ConsumeXml shows " +
                   $"{string.Join("; ", mismatches)}.";
        return $"Configured axis '{axisPath}': {applied} (read-back: all {fields.Count} field(s) confirmed).";
    }

    public (string Summary, NcAxisParams? Params) ReadAxisParams(string axisPath)
    {
        if (string.IsNullOrWhiteSpace(axisPath))
            return ("[Error] axisPath is required (get it from twincat_list_nc_tasks_axes).", null);

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, null);

        try
        {
            var p = session.ReadAxisParams(axisPath.Trim());
            return ($"Axis '{axisPath}': {p.Fields.Count} known parameter(s).", p);
        }
        catch (Exception ex)
        {
            return (McpErrors.EnsureError(ComCall.Describe(ex)), null);
        }
    }
}
