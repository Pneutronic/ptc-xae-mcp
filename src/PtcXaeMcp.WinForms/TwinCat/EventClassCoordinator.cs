using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// twincat_event_class_create / _read / _update and twincat_plc_add_global_data_type: EventLogger event classes in the
/// TwinCAT type system, and the "Add Global Data Type" pin that makes a PLC library carry one. Every argument and state
/// refusal comes before anything changes. Create and update are live type-system calls (the PLC's TC_EVENTS follows at
/// once); the pin — and the refresh of a pin after an update — is written as files while the solution is closed, because
/// the XAE offers no automation call for it.
/// </summary>
internal sealed class EventClassCoordinator(ITwinCatSession session)
{
    // Relaxed escaping: event texts are often German; "ü" reads better than "ü".
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Known(EventClassInfo Info, string Xml);

    // ---- create -------------------------------------------------------------------------------

    public string Create(string name, string? displayText, IReadOnlyList<EventClassEventInput>? events)
    {
        var nameError = IecIdentifier.Validate(name, "Event class name");
        if (nameError is not null) return nameError;
        name = name.Trim();
        if (events is null || events.Count == 0)
            return "[Error] events is required: at least one event (id, name, severity, text). Nothing was changed.";
        var (checkedEvents, eventError) = CheckEvents(events);
        if (eventError is not null) return eventError;
        string display = string.IsNullOrWhiteSpace(displayText) ? name : displayText;

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        List<Known> classes;
        try { classes = ReadClasses(); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        var same = classes.FirstOrDefault(c => string.Equals(c.Info.Name, name, StringComparison.OrdinalIgnoreCase));
        if (same is not null)
            return $"[Error] An event class '{same.Info.Name}' {same.Info.Guid} already exists ({Origin(same.Info)}). " +
                   "Change it with twincat_event_class_update or choose another name. Nothing was changed.";

        string readBack;
        try { readBack = session.CreateTypeSystemType(name, EventClassXml.BuildDataType(name, null, display, checkedEvents!)); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)) + " Nothing was saved."; }

        var created = EventClassXml.Parse(readBack);
        if (created is null)
            return $"[Error] The type system has a type '{name}' after CreateType, but it reads back without events: {Shorten(readBack)}. " +
                   "Nothing was saved; close the solution without saving to drop it.";

        var lines = new List<string>
        {
            $"Created event class '{created.Name}' {created.Guid} in the type system: {Describe(created)}.",
        };
        lines.AddRange(Differences(display, checkedEvents!, created));
        if (!created.Persistent)
            lines.Add("[Warn] The class reads back without PersistentType — the .tsproj may drop it on save; check it after reopening.");
        lines.Add(Save("The .tsproj keeps it (PersistentType)"));
        lines.Add($"PLC code: TC_EVENTS.{created.Name}.{created.Events[0].Name} (TcEventEntry). To ship it with a PLC library, " +
                  $"pin it into the library's PLC project with twincat_plc_add_global_data_type typeName='{created.Name}'.");
        return string.Join(Environment.NewLine, lines);
    }

    // ---- read ---------------------------------------------------------------------------------

    public string Read(string? name, bool includeBuiltIn)
    {
        if (name is not null)
        {
            var nameError = IecIdentifier.Validate(name, "Event class name");
            if (nameError is not null) return nameError;
            name = name.Trim();
        }

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        List<Known> classes;
        IReadOnlyList<PlcExternalTypesInfo> plcs;
        try
        {
            classes = ReadClasses();
            plcs = session.ReadPlcExternalTypes();
        }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        if (name is null)
        {
            var shown = classes.Where(c => includeBuiltIn || !c.Info.BuiltIn)
                .OrderBy(c => c.Info.BuiltIn).ThenBy(c => c.Info.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new
                {
                    name = c.Info.Name,
                    guid = c.Info.Guid,
                    displayText = c.Info.DisplayText,
                    origin = Origin(c.Info),
                    events = c.Info.Events.Count,
                    pinnedIn = PinStates(c.Info, plcs).Select(p => p.Plc).ToList(),
                })
                .ToList();
            int builtIn = classes.Count(c => c.Info.BuiltIn);
            return JsonSerializer.Serialize(new
            {
                count = shown.Count,
                builtInHidden = includeBuiltIn ? 0 : builtIn,
                eventClasses = shown,
            }, Json);
        }

        var found = classes.FirstOrDefault(c => string.Equals(c.Info.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found is null)
            return $"[Error] No event class '{name}' in the type system. Event classes of the project: " +
                   $"{Join(classes.Where(c => !c.Info.BuiltIn).Select(c => c.Info.Name))}.";

        var info = found.Info;
        return JsonSerializer.Serialize(new
        {
            name = info.Name,
            guid = info.Guid,
            displayText = info.DisplayText,
            origin = Origin(info),
            plcAccess = $"TC_EVENTS.{info.Name}.<event>",
            events = info.Events.Select(e => new { id = e.Id, name = e.Name, severity = e.Severity, text = e.DisplayText }).ToList(),
            pinnedIn = PinStates(info, plcs).Select(p => new { plc = p.Plc, externalTypes = p.Path, upToDate = p.UpToDate }).ToList(),
        }, Json);
    }

    // ---- update -------------------------------------------------------------------------------

    public string Update(string name, string? displayText, IReadOnlyList<EventClassEventInput>? events)
    {
        var nameError = IecIdentifier.Validate(name, "Event class name");
        if (nameError is not null) return nameError;
        name = name.Trim();
        if (displayText is null && events is null)
            return "[Error] Nothing to update: pass displayText and/or events (the complete new event list). Nothing was changed.";
        List<EventClassEvent>? checkedEvents = null;
        if (events is not null)
        {
            if (events.Count == 0)
                return "[Error] events must not be empty: an event class needs at least one event. Nothing was changed.";
            (checkedEvents, var eventError) = CheckEvents(events);
            if (eventError is not null) return eventError;
        }

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        List<Known> classes;
        IReadOnlyList<PlcExternalTypesInfo> plcs;
        try
        {
            classes = ReadClasses();
            plcs = session.ReadPlcExternalTypes();
        }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        var found = classes.FirstOrDefault(c => string.Equals(c.Info.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found is null)
            return $"[Error] No event class '{name}' in the type system (event classes of the project: " +
                   $"{Join(classes.Where(c => !c.Info.BuiltIn).Select(c => c.Info.Name))}). Nothing was changed.";
        var old = found.Info;
        if (old.BuiltIn)
            return $"[Error] '{old.Name}' is a built-in TwinCAT event class and cannot be changed. Nothing was changed.";
        if (!old.Persistent)
            return $"[Error] '{old.Name}' {old.Guid} is not kept in the .tsproj: it comes from a TMC file (an AdditionalTmcFile of the " +
                   "type system, a C++ module or a library). Change it there — replacing it here would leave two definitions with " +
                   "the same GUID (\"A different data type … with same GUID exists already\"). Nothing was changed.";
        if (old.Guid.Length == 0 || !Guid.TryParse(old.Guid, out _))
            return $"[Error] The GUID of '{old.Name}' is not readable ('{old.Guid}'). Nothing was changed.";

        string newXml = EventClassXml.ApplyUpdate(found.Xml, displayText, checkedEvents);
        var planned = EventClassXml.Parse(newXml)!;
        if (SameContent(old, planned))
            return $"Event class '{old.Name}' {old.Guid} is unchanged: display text and events are already as given. Nothing was changed.";

        var pinned = PinStates(old, plcs).ToList();

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string readBack;
        try { readBack = session.ReplaceTypeSystemType(old.Guid, old.Name, newXml, EventClassXml.DataTypeElement(found.Xml) ?? found.Xml); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)) + " Nothing was saved."; }

        var updated = EventClassXml.Parse(readBack);
        if (updated is null)
            return $"[Error] '{old.Name}' reads back without events after the update: {Shorten(readBack)}. Nothing was saved; " +
                   "close the solution without saving to return to the saved state.";

        var lines = new List<string>
        {
            $"Updated event class '{updated.Name}' {updated.Guid} (GUID kept): {Describe(updated)}.",
        };
        lines.AddRange(Differences(planned.DisplayText, planned.Events, updated));
        lines.AddRange(EventChanges(old, updated));
        lines.Add(Save("The PLC's TC_EVENTS follows at once"));

        if (pinned.Count > 0)
        {
            // The save after DeleteType rewrites ExternalTypes.tmc without the class; the other pins of the file are written
            // back as they were, in case the XAE dropped them too.
            var type = GlobalDataTypeXml.Parse(readBack, updated.Name)!;
            var plans = pinned.Select(p => new PinPlan(p.Info, [type],
                GlobalDataTypeXml.DataTypesInTmc(p.Info.ExternalTypesXml)
                    .Where(k => !GlobalDataTypeXml.SameType(k, type) && !string.Equals(k.Name, type.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList())).ToList();
            lines.AddRange(Repin(plans, type, "the update dropped the pin"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    // ---- add global data type -------------------------------------------------------------------

    public string AddGlobalDataType(string typeName, string? plcProjectName)
    {
        var nameError = IecIdentifier.Validate(typeName, "typeName");
        if (nameError is not null) return nameError;
        typeName = typeName.Trim();

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        List<TypeSystemType> types;
        IReadOnlyList<PlcExternalTypesInfo> plcs;
        try
        {
            types = session.ReadTypeSystemXml().Select(x => GlobalDataTypeXml.Parse(x)).OfType<TypeSystemType>().ToList();
            plcs = session.ReadPlcExternalTypes(withPlcObjects: true);
        }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        // Several types may carry the name (a library's, a PLC's own DUT); the project's own one counts first.
        var found = types.Where(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Persistent ? 0 : t.BuiltIn ? 1 : 2).FirstOrDefault();
        if (found is null)
            return $"[Error] No data type '{typeName}' in the type system. Data types of the project (kept in the .tsproj): " +
                   $"{Join(ProjectTypes(types))}. Nothing was changed.";
        if (found.BuiltIn)
            return $"[Error] '{found.Name}' is a built-in TwinCAT {found.Kind}; every project has it, a library need not carry it. Nothing was changed.";
        if (!found.Persistent)
            return $"[Error] '{found.Name}' ({found.Kind}) comes from {found.Origin}, not from the project's type system. Only the " +
                   "project's own data types (kept in the .tsproj, PersistentType) are pinned; a library type comes with its library, " +
                   $"a DUT with its PLC project. Data types of the project: {Join(ProjectTypes(types))}. Nothing was changed.";
        if (found.Kind == GlobalDataTypeXml.KindFunctionBlock)
            return $"[Error] '{found.Name}' is a function block, not a data type. Nothing was changed.";

        var closure = GlobalDataTypeClosure.Resolve(found, types);

        if (plcs.Count == 0)
            return "[Error] No PLC project with a .plcproj in the solution (a referenced stand-alone PLC project is pinned in its own solution). Nothing was changed.";
        PlcExternalTypesInfo plc;
        if (string.IsNullOrWhiteSpace(plcProjectName))
        {
            if (plcs.Count > 1)
                return $"[Error] {plcs.Count} PLC projects ({Join(plcs.Select(p => p.PlcName))}) — pass plcProjectName. Nothing was changed.";
            plc = plcs[0];
        }
        else
        {
            var wanted = plcProjectName.Trim();
            var match = plcs.FirstOrDefault(p => string.Equals(p.PlcName, wanted, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(Path.GetFileNameWithoutExtension(p.PlcprojPath), wanted, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return $"[Error] PLC project '{wanted}' not found (PLCs: {Join(plcs.Select(p => p.PlcName))}). Nothing was changed.";
            plc = match;
        }

        var collisions = Collisions(closure, plc);
        if (collisions.Count > 0)
            return $"[Error] {Capitalize(found.Label)} cannot be pinned into PLC '{plc.PlcName}': " + string.Join("; ", collisions) +
                   ". Two definitions of one name or GUID are what makes the XAE report \"A different data type … with same GUID " +
                   "exists already\" or a duplicate type. Rename or remove the other one first. Nothing was changed.";

        if (closure.Types.All(t => PinUpToDate(t, plc)))
            return $"{Capitalize(found.Label)} is already pinned in PLC '{plc.PlcName}' and up to date ({plc.ExternalTypesPath})" +
                   (closure.Types.Count > 1 ? $", with its dependencies {Join(closure.Dependencies.Select(d => d.Label))}" : "") +
                   ". Nothing was changed.";

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        var lines = new List<string>();
        lines.AddRange(Repin([new PinPlan(plc, closure.Types, [])], found, null));
        if (closure.Types.Count > 1)
            lines.Add($"Dependencies taken along: {Join(closure.Dependencies.Select(d => d.Label))}.");
        foreach (var s in closure.Skipped)
            lines.Add($"[Warn] '{s.Reference.Name}' (used by '{s.UsedBy}') is not taken along: {s.Why}. A project that uses the pinned " +
                      "type from a library needs it from there too.");
        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> ProjectTypes(IEnumerable<TypeSystemType> types) =>
        types.Where(t => t.Persistent && !t.BuiltIn).Select(t => $"{t.Name} ({t.Kind})")
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    // What the PLC project already has under a name or GUID of a type to pin: a DUT/POU/interface, or a different pinned type.
    internal static List<string> Collisions(GlobalDataTypeClosure closure, PlcExternalTypesInfo plc)
    {
        var lines = new List<string>();
        var pinned = GlobalDataTypeXml.DataTypesInTmc(plc.ExternalTypesXml);
        foreach (var t in closure.Types)
        {
            foreach (var o in plc.PlcObjects ?? [])
                if (string.Equals(o.Name, t.Name, StringComparison.OrdinalIgnoreCase)
                    || (t.IsEventClass && string.Equals(o.Name, "ST_" + t.Name, StringComparison.OrdinalIgnoreCase)))
                    lines.Add($"the PLC project has a {o.Kind} '{o.Name}' ({o.Path}) of the same name as {t.Label}");
            foreach (var p in pinned)
            {
                bool sameName = string.Equals(p.Name, t.Name, StringComparison.OrdinalIgnoreCase);
                bool sameGuid = p.Guid.Length > 0 && string.Equals(p.Guid, t.Guid, StringComparison.OrdinalIgnoreCase);
                if (sameName && !sameGuid && !(p.Guid.Length == 0 && GlobalDataTypeXml.SameDefinition(p.DataTypeXml, t.DataTypeXml)))
                    lines.Add($"its ExternalTypes.tmc already holds a different {p.Kind} '{p.Name}' " +
                              $"{(p.Guid.Length == 0 ? "without GUID" : p.Guid)} — {t.Label} has the same name");
                else if (sameGuid && !sameName)
                    lines.Add($"its ExternalTypes.tmc already holds {p.Label} with the GUID of {t.Label}");
            }
        }
        return lines;
    }

    // Pinned (file + tree node) with the definition the type system has now.
    private static bool PinUpToDate(TypeSystemType type, PlcExternalTypesInfo plc)
    {
        var inFile = GlobalDataTypeXml.DataTypesInTmc(plc.ExternalTypesXml).FirstOrDefault(p =>
            GlobalDataTypeXml.SameType(p, type) && string.Equals(p.Name, type.Name, StringComparison.OrdinalIgnoreCase));
        return inFile is not null
               && plc.PinnedNodes.Any(n => GlobalDataTypeXml.PinNodeIs(n, type))
               && SameDefinition(type, inFile);
    }

    private static bool SameDefinition(TypeSystemType a, TypeSystemType b)
    {
        if (a.IsEventClass && b.IsEventClass
            && EventClassXml.Parse(a.DataTypeXml) is { } ea && EventClassXml.Parse(b.DataTypeXml) is { } eb)
            return SameContent(ea, eb);
        return GlobalDataTypeXml.SameDefinition(a.DataTypeXml, b.DataTypeXml);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---- shared ---------------------------------------------------------------------------------

    private List<Known> ReadClasses()
    {
        var list = new List<Known>();
        foreach (string xml in session.ReadEventClassXml())
            if (EventClassXml.Parse(xml) is { } info) list.Add(new Known(info, xml));
        return list;
    }

    private sealed record PinState(PlcExternalTypesInfo Info, string Plc, string Path, bool UpToDate);

    // The PLC projects that pin the class: a node under ExternalTypes.tmc or a DataType of that GUID/name in the file.
    private static IEnumerable<PinState> PinStates(EventClassInfo info, IEnumerable<PlcExternalTypesInfo> plcs)
    {
        foreach (var plc in plcs)
        {
            var inFile = EventClassXml.EventClassesInTmc(plc.ExternalTypesXml).FirstOrDefault(c =>
                (c.Guid.Length > 0 && string.Equals(c.Guid, info.Guid, StringComparison.OrdinalIgnoreCase))
                || string.Equals(c.Name, info.Name, StringComparison.OrdinalIgnoreCase));
            bool node = plc.PinnedNodes.Any(n => EventClassXml.PinNodeIsClass(n, info.Name));
            if (inFile is null && !node) continue;
            yield return new PinState(plc, plc.PlcName, plc.ExternalTypesPath, inFile is not null && SameContent(info, inFile));
        }
    }

    /// <summary>What one PLC's ExternalTypes.tmc gets: <c>Types</c> (dependencies first) and <c>Kept</c>, pins it had that are written back unchanged.</summary>
    private sealed record PinPlan(PlcExternalTypesInfo Plc, IReadOnlyList<TypeSystemType> Types, IReadOnlyList<TypeSystemType> Kept);

    // Writes the types into ExternalTypes.tmc (+ the .plcproj item) of each PLC while the solution is closed, then reads
    // every pinned type back: its node under ExternalTypes.tmc in the tree and its DataType in the file.
    private List<string> Repin(IReadOnlyList<PinPlan> plans, TypeSystemType target, string? why)
    {
        var lines = new List<string>();
        var edits = new List<SolutionFileEdit>();
        foreach (var plan in plans)
        {
            var dataTypes = plan.Kept.Concat(plan.Types).Select(t => t.DataTypeXml).ToList();
            edits.Add(new SolutionFileEdit(plan.Plc.ExternalTypesPath,
                current => dataTypes.Aggregate(current, (text, dataType) => EventClassXml.UpsertIntoTmc(text, dataType))!));
            edits.Add(new SolutionFileEdit(plan.Plc.PlcprojPath, current => current is null
                ? throw new InvalidOperationException($"{plan.Plc.PlcprojPath} does not exist.")
                : EventClassXml.EnsureExternalTypesItem(current)));
        }

        string names = Join(plans.Select(p => $"'{p.Plc.PlcName}'"));
        string because = why is null ? "" : $" ({why})";
        try { session.EditFilesWithSolutionClosed(edits); }
        catch (Exception ex)
        {
            lines.Add(McpErrors.EnsureError(ComCall.Describe(ex)) +
                      $" Pinning '{target.Name}' into PLC {names} did not complete{because}: check ExternalTypes.tmc " +
                      "next to the .plcproj and the solution state (twincat_get_active_project), or pin it again with twincat_plc_add_global_data_type.");
            return lines;
        }

        IReadOnlyList<PlcExternalTypesInfo> after;
        try { after = session.ReadPlcExternalTypes(); }
        catch (Exception ex)
        {
            lines.Add(McpErrors.EnsureError(ComCall.Describe(ex)) + " The files were written and the solution reopened, but the pin could not be read back.");
            return lines;
        }

        foreach (var plan in plans)
        {
            var plc = plan.Plc;
            var now = after.FirstOrDefault(a => string.Equals(a.PlcprojPath, plc.PlcprojPath, StringComparison.OrdinalIgnoreCase));
            var inFileNow = GlobalDataTypeXml.DataTypesInTmc(now?.ExternalTypesXml);
            var missingKept = new List<string>();
            foreach (var type in plan.Types.Reverse().Concat(plan.Kept))
            {
                bool kept = plan.Kept.Contains(type);
                string? node = now?.PinnedNodes.FirstOrDefault(n => GlobalDataTypeXml.PinNodeIs(n, type));
                var inFile = inFileNow.FirstOrDefault(p => string.Equals(p.Name, type.Name, StringComparison.OrdinalIgnoreCase));
                if (node is null || inFile is null)
                {
                    if (kept) { missingKept.Add(type.Label); continue; }
                    lines.Add($"[Error] PLC '{plc.PlcName}': ExternalTypes.tmc was written, but after the reopen " +
                              (node is null ? $"the XAE shows no ExternalTypes.tmc node for {type.Label}" : $"the file no longer holds {type.Label}") +
                              $" ({plc.ExternalTypesPath}). Check the PLC project in the XAE.");
                    continue;
                }
                if (kept) continue;
                string content = SameDefinition(type, inFile) ? "." : " — [Warn] but its content differs from the type system.";
                lines.Add(ReferenceEquals(type, target) || type.Name == target.Name
                    ? $"Pinned {type.Kind} '{type.Name}' {type.Guid} into PLC '{plc.PlcName}' (\"Add Global Data Type\"): " +
                      $"{plc.ExternalTypesPath} and its .plcproj entry; the tree shows ExternalTypes.tmc^{node}{content}"
                    : $"Pinned its dependency {type.Label} into PLC '{plc.PlcName}' too; the tree shows ExternalTypes.tmc^{node}{content}");
            }
            if (missingKept.Count > 0)
                lines.Add($"[Error] PLC '{plc.PlcName}': {Join(missingKept)} were pinned before and were written back, but after the reopen " +
                          "the tree or the file does not show them. Pin them again with twincat_plc_add_global_data_type.");
        }
        lines.Add($"The solution was saved, closed and reopened for this{because}. " +
                  (target.IsEventClass
                      ? $"A library saved from that PLC project carries the class (externaltypes.auxiliary), so projects using the library get TC_EVENTS.{target.Name}."
                      : "A library saved from that PLC project carries the pinned types (externaltypes.auxiliary)."));
        return lines;
    }

    private string Save(string what)
    {
        try { session.SaveAll(); return $"Project saved. {what}."; }
        catch (Exception ex) { return $"[Warn] Save failed: {ComCall.Describe(ex)} — call twincat_save_project."; }
    }

    internal static (List<EventClassEvent>? Events, string? Error) CheckEvents(IReadOnlyList<EventClassEventInput> events)
    {
        var list = new List<EventClassEvent>();
        var ids = new HashSet<uint>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e is null) return (null, $"[Error] events[{i}] is empty. Nothing was changed.");
            var nameError = IecIdentifier.Validate(e.Name, $"events[{i}].name");
            if (nameError is not null) return (null, nameError + " Nothing was changed.");
            string name = e.Name.Trim();
            if (e.Id < 0 || e.Id > uint.MaxValue)
                return (null, $"[Error] events[{i}] ('{name}'): id {e.Id.ToString(CultureInfo.InvariantCulture)} is outside 0..4294967295. Nothing was changed.");
            uint id = (uint)e.Id;
            if (!ids.Add(id))
                return (null, $"[Error] events[{i}] ('{name}'): id {id} is used twice. Nothing was changed.");
            if (!names.Add(name))
                return (null, $"[Error] events[{i}]: the name '{name}' is used twice. Nothing was changed.");
            string? severity = EventClassXml.NormalizeSeverity(e.Severity);
            if (severity is null)
                return (null, $"[Error] events[{i}] ('{name}'): severity '{e.Severity}' is none of {string.Join(", ", EventClassXml.Severities)}. Nothing was changed.");
            list.Add(new EventClassEvent(id, name, severity, string.IsNullOrEmpty(e.Text) ? name : e.Text));
        }
        return (list, null);
    }

    private static bool SameContent(EventClassInfo a, EventClassInfo b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.DisplayText, b.DisplayText, StringComparison.Ordinal)
        && a.Events.Count == b.Events.Count
        && a.Events.Zip(b.Events).All(p => p.First.Id == p.Second.Id
                                           && string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal)
                                           && string.Equals(p.First.Severity, p.Second.Severity, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(p.First.DisplayText, p.Second.DisplayText, StringComparison.Ordinal));

    // What reads back differently from what was written (the XAE does not refuse, it may normalize).
    private static IEnumerable<string> Differences(string displayText, IReadOnlyList<EventClassEvent> wanted, EventClassInfo readBack)
    {
        if (!string.Equals(displayText.Trim(), readBack.DisplayText, StringComparison.Ordinal))
            yield return $"[Warn] The display text reads back as '{readBack.DisplayText}', not '{displayText}'.";
        foreach (var w in wanted)
        {
            var r = readBack.Events.FirstOrDefault(e => e.Id == w.Id);
            if (r is null) { yield return $"[Warn] Event {w.Id} '{w.Name}' is missing in the read-back."; continue; }
            if (!string.Equals(r.Name, w.Name, StringComparison.Ordinal)
                || !string.Equals(r.Severity, w.Severity, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(r.DisplayText, w.DisplayText, StringComparison.Ordinal))
                yield return $"[Warn] Event {w.Id} reads back as '{r.Name}' ({r.Severity}) '{r.DisplayText}', not '{w.Name}' ({w.Severity}) '{w.DisplayText}'.";
        }
        foreach (var extra in readBack.Events.Where(r => wanted.All(w => w.Id != r.Id)))
            yield return $"[Warn] The read-back has an event {extra.Id} '{extra.Name}' that was not written.";
    }

    private static IEnumerable<string> EventChanges(EventClassInfo before, EventClassInfo after)
    {
        var removed = before.Events.Where(b => after.Events.All(a => a.Id != b.Id)).Select(b => $"{b.Id} {b.Name}").ToList();
        var added = after.Events.Where(a => before.Events.All(b => b.Id != a.Id)).Select(a => $"{a.Id} {a.Name}").ToList();
        if (added.Count > 0) yield return $"Added events: {string.Join(", ", added)}.";
        if (removed.Count > 0)
            yield return $"Removed events: {string.Join(", ", removed)} — PLC code that uses TC_EVENTS.{after.Name}.<removed> no longer compiles.";
    }

    private static string Describe(EventClassInfo c) =>
        $"display text '{c.DisplayText}', {c.Events.Count} event(s): " +
        string.Join("; ", c.Events.Select(e => $"{e.Id} {e.Name} ({e.Severity}) '{e.DisplayText}'"));

    private static string Origin(EventClassInfo c) =>
        c.BuiltIn ? "built-in" : c.Persistent ? "project (.tsproj)" : "TMC file (AdditionalTmcFile, C++ module or library)";

    private static string Shorten(string s) => s.Length <= 300 ? s : s[..300] + " …";

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "<none>" : string.Join(", ", list);
    }
}
