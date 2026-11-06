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
            lines.AddRange(Repin(updated, readBack, pinned.Select(p => p.Info).ToList(), "the update dropped the pin"));
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

        List<Known> classes;
        IReadOnlyList<PlcExternalTypesInfo> plcs;
        try
        {
            classes = ReadClasses();
            plcs = session.ReadPlcExternalTypes();
        }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.Describe(ex)); }

        var found = classes.FirstOrDefault(c => string.Equals(c.Info.Name, typeName, StringComparison.OrdinalIgnoreCase));
        if (found is null)
            return $"[Error] No event class '{typeName}' in the type system. This tool pins event classes only (other global data " +
                   $"types are not supported yet). Event classes of the project: {Join(classes.Where(c => !c.Info.BuiltIn).Select(c => c.Info.Name))}. " +
                   "Nothing was changed.";
        if (found.Info.BuiltIn)
            return $"[Error] '{found.Info.Name}' is a built-in TwinCAT event class; every project has it, a library need not carry it. Nothing was changed.";

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

        var state = PinStates(found.Info, [plc]).FirstOrDefault();
        if (state is not null && state.UpToDate && state.Info.PinnedNodes.Any(n => EventClassXml.PinNodeIsClass(n, found.Info.Name)))
            return $"Event class '{found.Info.Name}' {found.Info.Guid} is already pinned in PLC '{plc.PlcName}' and up to date " +
                   $"({state.Path}). Nothing was changed.";

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        var lines = new List<string>();
        lines.AddRange(Repin(found.Info, found.Xml, [plc], null));
        return string.Join(Environment.NewLine, lines);
    }

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

    // Writes the class into ExternalTypes.tmc (+ the .plcproj item) of each PLC while the solution is closed, then checks the tree.
    private List<string> Repin(EventClassInfo info, string exportXml, IReadOnlyList<PlcExternalTypesInfo> plcs, string? why)
    {
        var lines = new List<string>();
        string dataType = EventClassXml.DataTypeElement(exportXml) ?? exportXml;
        var edits = new List<SolutionFileEdit>();
        foreach (var plc in plcs)
        {
            edits.Add(new SolutionFileEdit(plc.ExternalTypesPath, current => EventClassXml.UpsertIntoTmc(current, dataType)));
            edits.Add(new SolutionFileEdit(plc.PlcprojPath, current => current is null
                ? throw new InvalidOperationException($"{plc.PlcprojPath} does not exist.")
                : EventClassXml.EnsureExternalTypesItem(current)));
        }

        string names = Join(plcs.Select(p => $"'{p.PlcName}'"));
        try { session.EditFilesWithSolutionClosed(edits); }
        catch (Exception ex)
        {
            lines.Add(McpErrors.EnsureError(ComCall.Describe(ex)) +
                      $" Pinning '{info.Name}' into PLC {names} did not complete{(why is null ? "" : $" ({why})")}: check ExternalTypes.tmc " +
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

        foreach (var plc in plcs)
        {
            var now = after.FirstOrDefault(a => string.Equals(a.PlcprojPath, plc.PlcprojPath, StringComparison.OrdinalIgnoreCase));
            string? node = now?.PinnedNodes.FirstOrDefault(n => EventClassXml.PinNodeIsClass(n, info.Name));
            var inFile = EventClassXml.EventClassesInTmc(now?.ExternalTypesXml)
                .FirstOrDefault(c => string.Equals(c.Name, info.Name, StringComparison.OrdinalIgnoreCase));
            if (node is null || inFile is null)
            {
                lines.Add($"[Error] PLC '{plc.PlcName}': ExternalTypes.tmc was written, but after the reopen " +
                          (node is null ? "the XAE shows no ExternalTypes.tmc node for the class" : "the file no longer holds the class") +
                          $" ({plc.ExternalTypesPath}). Check the PLC project in the XAE.");
                continue;
            }
            lines.Add($"Pinned event class '{info.Name}' {info.Guid} into PLC '{plc.PlcName}' (\"Add Global Data Type\"): " +
                      $"{plc.ExternalTypesPath} and its .plcproj entry; the tree shows ExternalTypes.tmc^{node}" +
                      (SameContent(info, inFile) ? "." : " — [Warn] but its content differs from the type system."));
        }
        lines.Add($"The solution was saved, closed and reopened for this{(why is null ? "" : $" ({why})")}. " +
                  "A library saved from that PLC project carries the class (externaltypes.auxiliary), so projects using the library get TC_EVENTS." +
                  $"{info.Name}.");
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
