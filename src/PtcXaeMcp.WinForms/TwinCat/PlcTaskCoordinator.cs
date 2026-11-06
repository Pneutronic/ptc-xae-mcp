using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// twincat_plc_task_create: a real-time task under TIRT with cycle time and priority, the PLC task reference linked to it
/// and optionally the PROGRAM it calls. Every refusal (name, cycle time, priority collision, unknown PLC or program) comes
/// before the logout and before anything is created. The XAE may adjust cycle time or priority without an error, so the
/// result reports the values read back. When a later step fails, the result names exactly what already exists.
/// </summary>
internal sealed class PlcTaskCoordinator(ITwinCatSession session)
{
    internal const int MinPriority = 1;
    internal const int MaxPriority = 63;

    /// <summary>The priority of the first task when TIRT has none (the XAE's usual PLC task priority).</summary>
    internal const int FirstTaskPriority = 20;

    /// <summary>100 ns units per millisecond (ITcTaskModule.CycleTime).</summary>
    internal const double TicksPerMs = 10_000;

    private const string ProgramLabel = "TASKREF (PROGRAM)";

    public string Create(string name, double cycleTimeMs, int? priority, string? plcProjectName, string? program)
    {
        // ---- arguments: no XAE needed ----
        var nameError = IecIdentifier.Validate(name, "Task name");
        if (nameError is not null) return nameError;
        name = name.Trim();

        var cycleError = TryCycleTicks(cycleTimeMs, out uint ticks);
        if (cycleError is not null) return cycleError;

        if (priority is int requested && (requested < MinPriority || requested > MaxPriority))
            return $"[Error] priority {requested} is outside {MinPriority}..{MaxPriority}. Nothing was changed.";

        if (program is not null)
        {
            var programError = IecIdentifier.Validate(program, "program");
            if (programError is not null) return programError;
            program = program.Trim();
        }

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        // ---- current state: refusals before the logout ----
        IReadOnlyList<RealtimeTaskInfo> tasks;
        PlcTaskContext context;
        try
        {
            tasks = session.ListRealtimeTasks();
            context = session.ReadPlcTaskContext(plcProjectName);
        }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        var sameName = tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (sameName is not null)
            return $"[Error] A real-time task '{sameName.Name}' already exists ({sameName.Path}). Choose another name. Nothing was changed.";

        var reference = context.TaskReferences.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (reference is not null && !string.IsNullOrWhiteSpace(reference.LinkedTask) && !LinksTo(reference.LinkedTask, name))
            return $"[Error] PLC '{context.PlcName}' already has a task reference '{reference.Name}' ({reference.Path}) linked to " +
                   $"'{reference.LinkedTask}'. Choose another name. Nothing was changed.";

        if (program is not null)
        {
            string? known = context.Programs.FirstOrDefault(p => string.Equals(p, program, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                return $"[Error] PLC '{context.PlcName}' has no PROGRAM '{program}' (PROGRAMs: {Join(context.Programs)}). " +
                       "Create it first (twincat_plc_create kind=POU subtype=PROGRAM) or omit program. Nothing was changed.";
            program = known; // the name as the project spells it
        }

        var warnings = new List<string>();
        var unreadable = tasks.Where(t => t.Priority is null).Select(t => t.Name).ToList();
        if (unreadable.Count > 0)
            warnings.Add($"[Warn] The priority of {Join(unreadable)} could not be read; a collision with it is not excluded.");

        int chosen;
        if (priority is int given)
        {
            var users = tasks.Where(t => t.Priority == given).Select(t => t.Name).ToList();
            if (users.Count > 0)
                return $"[Error] priority {given} is already used by {Join(users)}. Used priorities: {DescribeUsed(tasks)}. " +
                       $"Pass a free one ({MinPriority}..{MaxPriority}) or omit priority for the next free one. Nothing was changed.";
            chosen = given;
        }
        else
        {
            int? next = NextFreePriority(tasks.Where(t => t.Priority is not null).Select(t => t.Priority!.Value));
            if (next is null)
                return $"[Error] No free priority left in {MinPriority}..{MaxPriority} (used: {DescribeUsed(tasks)}). Nothing was changed.";
            chosen = next.Value;
        }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        // ---- 1. the real-time task ----
        string taskPath;
        try { taskPath = session.CreateRealtimeTask(name); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        var created = new List<string> { $"real-time task '{taskPath}'" };
        string taskName = LastSegment(taskPath);
        if (!string.Equals(taskName, name, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"[Warn] The XAE named the task '{taskName}', not '{name}'.");
        string expectedLink = taskPath; // "TIRT^<task>" as the XAE reports it

        // ---- 2. cycle time and priority, read back ----
        RealtimeTaskInfo timing;
        try { timing = session.SetRealtimeTaskTiming(taskPath, ticks, chosen); }
        catch (Exception ex) { return Partial(created, "setting cycle time and priority", ex); }
        warnings.AddRange(TimingDifferences(ticks, chosen, timing));

        // ---- 3. the PLC task reference, linked ----
        string referencePath;
        if (reference is null)
        {
            try { referencePath = session.CreatePlcTaskReference(context.ProjectPath, taskName); }
            catch (Exception ex) { return Partial(created, $"creating the PLC task reference under '{context.ProjectPath}'", ex); }
            created.Add($"PLC task reference '{referencePath}'");
        }
        else
        {
            referencePath = reference.Path;
        }

        PlcTaskReferenceInfo link;
        try { link = session.SetPlcTaskLink(referencePath, expectedLink); }
        catch (Exception ex) { return Partial(created, $"linking '{referencePath}' to '{expectedLink}'", ex); }
        if (string.IsNullOrWhiteSpace(link.LinkedTask) || !LinksTo(link.LinkedTask, taskName))
            return Partial(created, $"linking '{referencePath}' to '{expectedLink}'",
                $"the read-back shows LinkedTask '{link.LinkedTask ?? "<unreadable>"}'");

        // ---- 4. the program call ----
        string? programPath = null;
        if (program is not null)
        {
            try { programPath = session.CreatePlcChild(referencePath, program, PlcItemType.ProgRef, ProgramLabel); }
            catch (Exception ex) { return Partial(created, $"adding the call of PROGRAM '{program}'", ex, referencePath, program); }
        }

        string save;
        try { session.SaveAll(); save = "saved"; }
        catch (Exception ex) { save = $"save failed: {ComCall.Describe(ex)} — call twincat_save_project"; }

        var lines = new List<string>
        {
            $"Created real-time task '{taskPath}' (read-back: cycle time {DescribeCycle(timing.CycleTime100ns)}, priority {Show(timing.Priority)}).",
            reference is null
                ? $"Created PLC task reference '{link.Path}' of PLC '{context.PlcName}', linked to '{link.LinkedTask}' (read-back)."
                : $"Linked the existing PLC task reference '{link.Path}' of PLC '{context.PlcName}' to '{link.LinkedTask}' (read-back).",
            programPath is not null
                ? $"The task calls PROGRAM '{program}' ({programPath})."
                : $"No program assigned: add one with twincat_plc_create (kind=TASKREF, parentPath='{referencePath}', name=<PROGRAM>).",
            $"Project {save}. Takes effect with the next Activate Configuration (twincat_activate_config).",
        };
        lines.AddRange(warnings);
        return string.Join(Environment.NewLine, lines);
    }

    // ---- validation helpers (unit-tested) ---------------------------------------------------

    /// <summary>Null and the cycle time in 100 ns units, or the "[Error] ..." for a value that is not a positive whole multiple of 100 ns.</summary>
    internal static string? TryCycleTicks(double cycleTimeMs, out uint ticks)
    {
        ticks = 0;
        if (double.IsNaN(cycleTimeMs) || double.IsInfinity(cycleTimeMs) || cycleTimeMs <= 0)
            return $"[Error] cycleTimeMs must be greater than 0 (got {cycleTimeMs.ToString(CultureInfo.InvariantCulture)}). Nothing was changed.";
        double exact = cycleTimeMs * TicksPerMs;
        double rounded = Math.Round(exact);
        if (rounded < 1 || Math.Abs(exact - rounded) > 1e-6)
            return $"[Error] cycleTimeMs {cycleTimeMs.ToString(CultureInfo.InvariantCulture)} is not a whole multiple of 0.0001 ms " +
                   "(100 ns, the unit of the task cycle time). Nothing was changed.";
        if (rounded > uint.MaxValue)
            return $"[Error] cycleTimeMs {cycleTimeMs.ToString(CultureInfo.InvariantCulture)} is too large. Nothing was changed.";
        ticks = (uint)rounded;
        return null;
    }

    /// <summary>
    /// The highest used priority + 1; when that is past <see cref="MaxPriority"/>, the highest free one below it (a new
    /// task never gets more urgent than necessary); <see cref="FirstTaskPriority"/> when none is used; null when all are.
    /// </summary>
    internal static int? NextFreePriority(IEnumerable<int> used)
    {
        var set = used.ToHashSet();
        if (set.Count == 0) return FirstTaskPriority;
        int next = set.Max() + 1;
        if (next >= MinPriority && next <= MaxPriority) return next;
        for (int p = MaxPriority; p >= MinPriority; p--)
            if (!set.Contains(p)) return p;
        return null;
    }

    // What the XAE set differently from what was asked (the setters do not fail on an adjusted value).
    internal static IEnumerable<string> TimingDifferences(uint ticks, int priority, RealtimeTaskInfo readBack)
    {
        if (readBack.CycleTime100ns is null)
            yield return "[Warn] The cycle time could not be read back; check it in the XAE.";
        else if (readBack.CycleTime100ns != ticks)
            yield return $"[Warn] The XAE set cycle time {DescribeCycle(readBack.CycleTime100ns)}, not the requested {DescribeCycle(ticks)} " +
                         "(it is a multiple of the base time of the real-time settings).";
        if (readBack.Priority is null)
            yield return "[Warn] The priority could not be read back; check it in the XAE.";
        else if (readBack.Priority != priority)
            yield return $"[Warn] The XAE set priority {readBack.Priority}, not the requested {priority}.";
    }

    internal static string DescribeCycle(uint? ticks) =>
        ticks is uint t
            ? $"{(t / TicksPerMs).ToString("0.####", CultureInfo.InvariantCulture)} ms ({t.ToString(CultureInfo.InvariantCulture)} x 100 ns)"
            : "<unreadable>";

    // LinkedTask may come back as "TIRT^<task>" or as the bare task name.
    private static bool LinksTo(string linkedTask, string taskName)
    {
        string link = linkedTask.Trim();
        return string.Equals(link, taskName, StringComparison.OrdinalIgnoreCase)
            || link.EndsWith("^" + taskName, StringComparison.OrdinalIgnoreCase);
    }

    private static string Partial(List<string> created, string step, Exception ex, string? referencePath = null, string? program = null) =>
        Partial(created, step, ComCall.Describe(ex), referencePath, program);

    // A later step failed: name what exists now and how to finish or remove it. Nothing is saved.
    private static string Partial(List<string> created, string step, string cause, string? referencePath = null, string? program = null)
    {
        string remedy = referencePath is not null && program is not null
            ? $"Add the call with twincat_plc_create (kind=TASKREF, parentPath='{referencePath}', name='{program}'), "
              + "or remove what was created in the XAE (right-click > Delete: the task under SYSTEM > Tasks, the PLC task reference under the PLC project)."
            : "Remove what was created in the XAE (right-click > Delete: the task under SYSTEM > Tasks, the PLC task reference under the PLC project), "
              + "or finish the step there.";
        return $"[Error] {step} failed: {cause.Replace("[Error] ", "")} Already created: {string.Join("; ", created)}. " +
               $"The project was not saved. {remedy}";
    }

    private static string LastSegment(string path)
    {
        int i = path.LastIndexOf('^');
        return i < 0 ? path : path[(i + 1)..];
    }

    private static string DescribeUsed(IReadOnlyList<RealtimeTaskInfo> tasks)
    {
        var used = tasks.Where(t => t.Priority is not null).OrderBy(t => t.Priority).Select(t => $"{t.Priority} ({t.Name})").ToList();
        return used.Count == 0 ? "none" : string.Join(", ", used);
    }

    private static string Show(int? value) => value is int v ? v.ToString(CultureInfo.InvariantCulture) : "<unreadable>";

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "<none>" : string.Join(", ", list);
    }
}
