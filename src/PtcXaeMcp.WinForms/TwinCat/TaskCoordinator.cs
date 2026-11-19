using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// twincat_task_list / twincat_task_configure / twincat_io_task_create: every real-time task under TIRT and TINC (PLC, I/O and
/// NC SAF/SVB tasks) with priority, cycle time, AMS port and disabled flag; changing those values of one task; and a new
/// task with process image for I/O devices (e.g. a Profinet device with ADS port 301). Every refusal comes before the
/// logout and before anything changes; every written value is read back from a fresh ProduceXml.
/// </summary>
internal sealed class TaskCoordinator(ITwinCatSession session)
{
    internal const int MinAmsPort = 1;
    internal const int MaxAmsPort = 65535;
    private const int ImageItemType = 3;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One task as the list reports it.</summary>
    internal sealed record TaskEntry(TaskSettings Settings, bool HasImage, bool PlcLinked)
    {
        public string Kind => TaskKind.Of(Settings.Path, HasImage, PlcLinked);
    }

    // ---- twincat_task_list ----------------------------------------------------------------

    public string List()
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        List<TaskEntry> tasks;
        var warnings = new List<string>();
        try { tasks = ReadAll(warnings); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        int tirt = tasks.Count(t => t.Settings.Path.StartsWith("TIRT^", StringComparison.OrdinalIgnoreCase));
        string summary = $"{tasks.Count} real-time task(s): {tirt} under TIRT, {tasks.Count - tirt} under TINC (NC).";
        var json = JsonSerializer.Serialize(new
        {
            tasks = tasks.Select(t => new
            {
                name = t.Settings.Name,
                path = t.Settings.Path,
                kind = t.Kind,
                image = t.HasImage,
                priority = t.Settings.Priority,
                cycleTimeMs = t.Settings.CycleTime100ns is uint c ? c / PlcTaskCoordinator.TicksPerMs : (double?)null,
                cycleTime100ns = t.Settings.CycleTime100ns,
                amsPort = t.Settings.AmsPort,
                autoStart = t.Settings.AutoStart,
                disabled = t.Settings.Disabled,
            }),
            warnings,
        }, JsonOpts);
        return summary + Environment.NewLine + json;
    }

    // ---- twincat_task_configure -----------------------------------------------------------

    public string Configure(string task, int? priority, double? cycleTimeMs, int? amsPort, bool? disabled)
    {
        if (string.IsNullOrWhiteSpace(task))
            return "[Error] task is required: its name or tree path from twincat_task_list. Nothing was changed.";
        if (priority is null && cycleTimeMs is null && amsPort is null && disabled is null)
            return "[Error] Nothing to change: pass at least one of priority, cycleTimeMs, amsPort, disabled. Nothing was changed.";
        if (priority is int p && (p < PlcTaskCoordinator.MinPriority || p > PlcTaskCoordinator.MaxPriority))
            return $"[Error] priority {p} is outside {PlcTaskCoordinator.MinPriority}..{PlcTaskCoordinator.MaxPriority}. Nothing was changed.";
        uint? ticks = null;
        if (cycleTimeMs is double ms)
        {
            var cycleError = PlcTaskCoordinator.TryCycleTicks(ms, out uint t);
            if (cycleError is not null) return cycleError;
            ticks = t;
        }
        if (amsPort is int port && (port < MinAmsPort || port > MaxAmsPort))
            return $"[Error] amsPort {port} is outside {MinAmsPort}..{MaxAmsPort}. Nothing was changed.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        List<TaskEntry> tasks;
        var warnings = new List<string>();
        try { tasks = ReadAll(warnings); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        var resolved = Resolve(tasks, task.Trim(), out error);
        if (resolved is null) return error!;
        var target = resolved.Settings;
        var others = tasks.Where(t => !ReferenceEquals(t, resolved)).ToList();

        if (priority is int wanted)
        {
            var users = others.Where(t => t.Settings.Priority == wanted).Select(t => t.Settings.Name).ToList();
            if (users.Count > 0)
                return $"[Error] priority {wanted} is already used by {Join(users)}. Used priorities: {DescribeUsed(tasks)}. " +
                       "Priorities must be unique across TIRT and TINC. Nothing was changed.";
        }
        if (amsPort is int wantedPort)
        {
            var users = others.Where(t => t.Settings.AmsPort == wantedPort).Select(t => t.Settings.Name).ToList();
            if (users.Count > 0)
                return $"[Error] AMS port {wantedPort} is already used by {Join(users)}. Nothing was changed.";
        }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string? xml = TaskDefXml.Build(priority, amsPort, ticks);
        try
        {
            if (xml is not null) session.ConsumeTreeItemXml(target.Path, xml);
            if (disabled is bool d && d != target.Disabled) session.SetDeviceDisabled(target.Path, d);
        }
        catch (Exception ex)
        {
            return McpErrors.EnsureError($"Changing task '{target.Path}' failed: {ComCall.Describe(ex).Replace("[Error] ", "")} " +
                                         "The project was not saved; check the task in the XAE.");
        }

        TaskSettings? after;
        try { after = TaskDefXml.Parse(session.ProduceTreeItemXml(target.Path), target.Path); }
        catch (Exception ex) { return McpErrors.EnsureError($"The change on '{target.Path}' could not be read back: {ComCall.Describe(ex).Replace("[Error] ", "")} The project was not saved."); }
        if (after is null)
            return $"[Error] The change on '{target.Path}' could not be read back (no TaskDef in its XML). The project was not saved.";

        var lines = new List<string>();
        var missed = new List<string>();
        Compare("priority", priority, target.Priority, after.Priority, v => v.ToString(CultureInfo.InvariantCulture), lines, missed);
        Compare("cycle time", ticks, target.CycleTime100ns, after.CycleTime100ns, v => PlcTaskCoordinator.DescribeCycle(v), lines, missed);
        Compare("AMS port", amsPort, target.AmsPort, after.AmsPort, v => v.ToString(CultureInfo.InvariantCulture), lines, missed);
        Compare("disabled", disabled, target.Disabled, after.Disabled, v => v ? "true" : "false", lines, missed);

        if (lines.Count == 0)
            return $"[Error] The XAE did not take any of the changes on '{target.Path}' ({string.Join("; ", missed)}). " +
                   "The project was not saved.";

        string save;
        try { session.SaveAll(); save = "saved"; }
        catch (Exception ex) { save = $"save failed: {ComCall.Describe(ex)} — call twincat_save_project"; }

        var result = new List<string>
        {
            $"Changed task '{target.Path}' (read-back): {string.Join("; ", lines)}.",
            $"Project {save}. Takes effect with the next Activate Configuration (twincat_activate_config).",
        };
        result.AddRange(missed.Select(m => $"[Warn] {m}"));
        result.AddRange(warnings);
        return string.Join(Environment.NewLine, result);
    }

    // ---- twincat_io_task_create -----------------------------------------------------------

    public string CreateIoTask(string name, double cycleTimeMs, int? priority, int? amsPort)
    {
        var nameError = IecIdentifier.Validate(name, "Task name");
        if (nameError is not null) return nameError;
        name = name.Trim();

        var cycleError = PlcTaskCoordinator.TryCycleTicks(cycleTimeMs, out uint ticks);
        if (cycleError is not null) return cycleError;
        if (priority is int p && (p < PlcTaskCoordinator.MinPriority || p > PlcTaskCoordinator.MaxPriority))
            return $"[Error] priority {p} is outside {PlcTaskCoordinator.MinPriority}..{PlcTaskCoordinator.MaxPriority}. Nothing was changed.";
        if (amsPort is int port && (port < MinAmsPort || port > MaxAmsPort))
            return $"[Error] amsPort {port} is outside {MinAmsPort}..{MaxAmsPort}. Nothing was changed.";

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        List<TaskEntry> tasks;
        var warnings = new List<string>();
        try { tasks = ReadAll(warnings); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        var sameName = tasks.FirstOrDefault(t => string.Equals(t.Settings.Name, name, StringComparison.OrdinalIgnoreCase));
        if (sameName is not null)
            return $"[Error] A real-time task '{sameName.Settings.Name}' already exists ({sameName.Settings.Path}). Choose another name. Nothing was changed.";

        int chosen;
        if (priority is int given)
        {
            var users = tasks.Where(t => t.Settings.Priority == given).Select(t => t.Settings.Name).ToList();
            if (users.Count > 0)
                return $"[Error] priority {given} is already used by {Join(users)}. Used priorities: {DescribeUsed(tasks)}. " +
                       $"Pass a free one ({PlcTaskCoordinator.MinPriority}..{PlcTaskCoordinator.MaxPriority}) or omit priority for the next free one. Nothing was changed.";
            chosen = given;
        }
        else
        {
            int? next = PlcTaskCoordinator.NextFreePriority(tasks.Where(t => t.Settings.Priority is not null).Select(t => t.Settings.Priority!.Value));
            if (next is null)
                return $"[Error] No free priority left in {PlcTaskCoordinator.MinPriority}..{PlcTaskCoordinator.MaxPriority} (used: {DescribeUsed(tasks)}). Nothing was changed.";
            chosen = next.Value;
        }
        if (amsPort is int wantedPort)
        {
            var users = tasks.Where(t => t.Settings.AmsPort == wantedPort).Select(t => t.Settings.Name).ToList();
            if (users.Count > 0)
                return $"[Error] AMS port {wantedPort} is already used by {Join(users)}. Nothing was changed.";
        }
        var unreadable = tasks.Where(t => t.Settings.Priority is null).Select(t => t.Settings.Name).ToList();
        if (unreadable.Count > 0)
            warnings.Add($"[Warn] The priority of {Join(unreadable)} could not be read; a collision with it is not excluded.");

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string path;
        try { path = session.CreateImageTask(name); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        TaskSettings? after;
        try
        {
            // The XAE gives a new image task priority 1 and AMS port 301 by itself: both are always written.
            session.ConsumeTreeItemXml(path, TaskDefXml.Build(chosen, amsPort, ticks)!);
            after = TaskDefXml.Parse(session.ProduceTreeItemXml(path), path);
        }
        catch (Exception ex)
        {
            return $"[Error] Setting cycle time and priority of '{path}' failed: {ComCall.Describe(ex).Replace("[Error] ", "")} " +
                   $"Already created: real-time task '{path}' (with image). The project was not saved. Set the values with " +
                   "twincat_task_configure, or remove the task in the XAE (SYSTEM > Tasks, right-click > Delete).";
        }
        if (after is null)
            return $"[Error] The new task '{path}' could not be read back (no TaskDef in its XML). Already created: real-time task " +
                   $"'{path}'. The project was not saved; check it in the XAE.";

        if (after.CycleTime100ns != ticks)
            warnings.Add($"[Warn] The XAE set cycle time {PlcTaskCoordinator.DescribeCycle(after.CycleTime100ns)}, not the requested {PlcTaskCoordinator.DescribeCycle(ticks)}.");
        if (after.Priority != chosen)
            warnings.Add($"[Warn] The XAE set priority {Show(after.Priority)}, not {chosen}; fix it with twincat_task_configure, since priorities must be unique.");
        if (amsPort is int requestedPort && after.AmsPort != requestedPort)
            warnings.Add($"[Warn] The XAE set AMS port {Show(after.AmsPort)}, not the requested {requestedPort}.");

        string save;
        try { session.SaveAll(); save = "saved"; }
        catch (Exception ex) { save = $"save failed: {ComCall.Describe(ex)} — call twincat_save_project"; }

        var lines = new List<string>
        {
            $"Created I/O task '{path}' with process image (read-back: cycle time {PlcTaskCoordinator.DescribeCycle(after.CycleTime100ns)}, " +
            $"priority {Show(after.Priority)}, AMS port {Show(after.AmsPort)}).",
            "Make it the sync task of an I/O device with twincat_io_device_sync_task.",
            $"Project {save}. Takes effect with the next Activate Configuration (twincat_activate_config).",
        };
        lines.AddRange(warnings);
        return string.Join(Environment.NewLine, lines);
    }

    // ---- helpers ----------------------------------------------------------------------------

    internal List<TaskEntry> ReadAll(List<string> warnings)
    {
        var links = new List<string>();
        try { links.AddRange(session.ListPlcTaskLinks()); }
        catch (Exception ex) when (!ComCall.IsBusy(ex))
        {
            warnings.Add($"[Warn] The PLC task references could not be read ({ComCall.Describe(ex).Replace("[Error] ", "")}); kind 'plc' is not reported.");
        }

        var tasks = new List<TaskEntry>();
        foreach (string path in session.ListTaskPaths())
        {
            var settings = TaskDefXml.Parse(session.ProduceTreeItemXml(path), path);
            if (settings is null) continue;
            bool image = session.ListAllTreeChildren(path).Any(c => c.ItemType == ImageItemType);
            bool linked = links.Any(l => LinksTo(l, settings));
            tasks.Add(new TaskEntry(settings, image, linked));
        }
        return tasks;
    }

    private static bool LinksTo(string link, TaskSettings task)
    {
        string l = link.Trim();
        return string.Equals(l, task.Path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(l, task.Name, StringComparison.OrdinalIgnoreCase)
            || (task.Path.StartsWith("TIRT^", StringComparison.OrdinalIgnoreCase) && l.EndsWith("^" + task.Name, StringComparison.OrdinalIgnoreCase));
    }

    // A path (contains '^') must match exactly; a name must match exactly one task.
    internal static TaskEntry? Resolve(List<TaskEntry> tasks, string task, out string? error)
    {
        error = null;
        if (task.Contains('^'))
        {
            var byPath = tasks.FirstOrDefault(t => string.Equals(t.Settings.Path, task, StringComparison.OrdinalIgnoreCase));
            if (byPath is null)
                error = $"[Error] No real-time task at '{task}'. Tasks: {Join(tasks.Select(t => t.Settings.Path))}. Nothing was changed.";
            return byPath;
        }
        var byName = tasks.Where(t => string.Equals(t.Settings.Name, task, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        error = byName.Count == 0
            ? $"[Error] No real-time task named '{task}'. Tasks: {Join(tasks.Select(t => t.Settings.Path))}. Nothing was changed."
            : $"[Error] '{task}' names {byName.Count} tasks ({Join(byName.Select(t => t.Settings.Path))}); pass the tree path. Nothing was changed.";
        return null;
    }

    // Requested value null: not part of the change. Otherwise "landed" when the read-back equals it.
    private static void Compare<T>(string what, T? requested, T? before, T? after, Func<T, string> show, List<string> lines, List<string> missed)
        where T : struct
    {
        if (requested is not T want) return;
        if (after is T got && EqualityComparer<T>.Default.Equals(got, want))
            lines.Add($"{what} {(before is T b ? show(b) : "<unreadable>")} -> {show(got)}");
        else
            missed.Add($"{what}: requested {show(want)}, the read-back shows {(after is T a ? show(a) : "<unreadable>")}");
    }

    private static string DescribeUsed(IEnumerable<TaskEntry> tasks)
    {
        var used = tasks.Where(t => t.Settings.Priority is not null).OrderBy(t => t.Settings.Priority)
            .Select(t => $"{t.Settings.Priority} ({t.Settings.Name})").ToList();
        return used.Count == 0 ? "none" : string.Join(", ", used);
    }

    private static string Show(int? value) => value is int v ? v.ToString(CultureInfo.InvariantCulture) : "<unreadable>";

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "<none>" : string.Join(", ", list);
    }
}
