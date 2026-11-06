using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

/// <summary>event-classes — MCP-only, no GUI tab.</summary>
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_event_class_create", ReadOnly = false, Destructive = false)]
    [Description("Create an EventLogger event class in the TwinCAT type system (ITcTypeSystem.CreateType) and save it in the .tsproj " +
                 "(PersistentType, kept although nothing references it). The PLC reaches each event as TC_EVENTS.<class>.<event> " +
                 "(TcEventEntry) at once. The type system generates the GUID. Refused with [Error] before anything changes: a class " +
                 "name or event name that is no IEC identifier, no events, an id outside 0..4294967295, an id or name used twice, an " +
                 "unknown severity, a class of that name that already exists. The result shows the class as read back. A PLC library " +
                 "carries the class only after twincat_plc_add_global_data_type on its PLC project.")]
    public string EventClassCreate(
        [Description("Name of the event class (IEC identifier), e.g. 'MyLibEvents'.")] string name,
        [Description("The events: [{id, name, severity, text}], at least one. severity: Verbose, Info (default), Warning, Error, Critical; " +
                     "text: the display text, {0} {1} … take the event's arguments (default: the name).")] EventClassEventInput[] events,
        [Description("Display text of the class. Default: its name.")] string? displayText = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_event_class_create", new { name, events = events?.Length, displayText }, () =>
            bridge.Dispatcher.Run(() =>
                new EventClassCoordinator(bridge.Dispatcher.Session).Create(name, displayText, events)), cancellationToken);

    [McpServerTool(Name = "twincat_event_class_read", ReadOnly = true, Destructive = false)]
    [Description("Read the EventLogger event classes of the type system as JSON. Without name: every event class of the project " +
                 "(name, GUID, display text, origin: project (.tsproj) or TMC file, event count, PLC projects that pin it via " +
                 "ExternalTypes.tmc); built-in TwinCAT classes only with includeBuiltIn. With name: that class with all its events " +
                 "(id, name, severity, text) and, per pinning PLC project, whether its ExternalTypes.tmc copy is up to date.")]
    public string EventClassRead(
        [Description("Name of one event class. Omit for the list.")] string? name = null,
        [Description("List the built-in TwinCAT event classes too (TcSystemEventClass …). Default false.")] bool includeBuiltIn = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_event_class_read", new { name, includeBuiltIn }, () =>
            bridge.Dispatcher.Run(() =>
                new EventClassCoordinator(bridge.Dispatcher.Session).Read(name, includeBuiltIn)), cancellationToken);

    [McpServerTool(Name = "twincat_event_class_update", ReadOnly = false, Destructive = false)]
    [Description("Change an event class of the project (one kept in the .tsproj): its display text and/or its complete event list. " +
                 "The GUID stays; the class is replaced in the type system (DeleteType + CreateType), so the PLC's TC_EVENTS follows " +
                 "at once, and the project is saved. Events are matched by id: details this tool does not manage (cause/remedy, " +
                 "description) stay with an id that is kept; ids missing from the new list are removed. When PLC projects pin the class " +
                 "(ExternalTypes.tmc), the type system drops that pin on the update; it is written again with the new content while " +
                 "the solution is saved, closed and reopened. Refused with [Error] before anything changes: invalid arguments as in " +
                 "twincat_event_class_create, an unknown or built-in class, a class from a TMC file (change it there: a second " +
                 "definition with the same GUID is what makes the XAE report \"A different data type … with same GUID exists already\"). " +
                 "Logs out of the PLC first.")]
    public string EventClassUpdate(
        [Description("Name of the event class to change.")] string name,
        [Description("The complete new event list [{id, name, severity, text}] (replaces the old one). Omit to keep the events.")] EventClassEventInput[]? events = null,
        [Description("New display text of the class. Omit to keep it.")] string? displayText = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_event_class_update", new { name, events = events?.Length, displayText }, () =>
            bridge.Dispatcher.Run(() =>
                new EventClassCoordinator(bridge.Dispatcher.Session).Update(name, displayText, events)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_add_global_data_type", ReadOnly = false, Destructive = false)]
    [Description("\"Add Global Data Type\": pins a data type of the project's type system (STRUCT, ENUM, ALIAS or an event class — " +
                 "the types kept in the .tsproj) into a PLC project, so a library saved from it carries the type (externaltypes.auxiliary); " +
                 "for an event class, projects using the library get TC_EVENTS.<class>. Every project data type it uses (struct members, " +
                 "enum and alias base types, nested) is taken along so ExternalTypes.tmc is self-contained; built-in types are not " +
                 "copied, and a type from a library, a TMC file or a PLC is not either (the result warns). Writes what the XAE command " +
                 "writes: the types into ExternalTypes.tmc next to the .plcproj and its <None Include=\"ExternalTypes.tmc\"> entry into the " +
                 ".plcproj. The XAE has no automation call for it, so the solution is saved, closed, the files written and the solution " +
                 "reopened; the result shows, for every pinned type, its ExternalTypes.tmc node read back from the tree. A pin that is out " +
                 "of date is refreshed; when all are up to date nothing changes. Refused with [Error] before anything changes: an unknown " +
                 "type, a built-in one or one that is not the project's (library, TMC file, a PLC's own DUT), an unknown PLC project, " +
                 "several PLC projects without plcProjectName, and a collision: the PLC project already has a DUT, POU or interface of " +
                 "the name of a type to pin, or its ExternalTypes.tmc holds another type of that name or GUID. Logs out of the PLC first.")]
    public string PlcAddGlobalDataType(
        [Description("Name of the data type or event class in the type system, e.g. 'ST_AxisData' or 'MyLibEvents'.")] string typeName,
        [Description("The PLC by its name under PLC or its .plcproj name. Optional when the solution has exactly one PLC project.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_add_global_data_type", new { typeName, plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
                new EventClassCoordinator(bridge.Dispatcher.Session).AddGlobalDataType(typeName, plcProjectName)), cancellationToken);
}
