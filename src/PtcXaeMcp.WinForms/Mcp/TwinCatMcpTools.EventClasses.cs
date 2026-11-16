using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

/// <summary>event-classes — MCP-only, no GUI tab.</summary>
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_event_class_create", ReadOnly = false, Destructive = false)]
    [Description("Create an EventLogger event class in the project (.tsproj) type system. " +
                 "Saved as persistent (kept although nothing references it); the PLC reaches each event as " +
                 "TC_EVENTS.<class>.<event> at once. The GUID is generated. Refused with [Error] before anything changes: " +
                 "a non-IEC class or event name, no events, an id outside 0..4294967295, a duplicate id or name, an unknown " +
                 "severity, an existing class of that name. The result shows the class as read back. A PLC library carries " +
                 "the class only after twincat_plc_add_global_data_type.")]
    public string EventClassCreate(
        [Description("Name of the event class (IEC identifier), e.g. 'MyLibEvents'.")] string name,
        [Description("The events [{id, name, severity, text}], at least one.")] EventClassEventInput[] events,
        [Description("Display text of the class. Default: its name.")] string? displayText = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_event_class_create", new { name, events = events?.Length, displayText }, () =>
            bridge.Dispatcher.Run(() =>
                new EventClassCoordinator(bridge.Dispatcher.Session).Create(name, displayText, events)), cancellationToken);

    [McpServerTool(Name = "twincat_event_class_read", ReadOnly = true, Destructive = false)]
    [Description("Read the EventLogger event classes of the type system as JSON. " +
                 "Without name: every project class (name, GUID, display text, origin .tsproj or TMC file, event count, PLC " +
                 "projects pinning it via ExternalTypes.tmc). With name: that class with all events (id, name, severity, text) " +
                 "and, per pinning PLC project, whether its ExternalTypes.tmc copy is up to date.")]
    public string EventClassRead(
        [Description("Name of one event class. Omit for the list.")] string? name = null,
        [Description("Also list built-in TwinCAT classes (TcSystemEventClass …). Default false.")] bool includeBuiltIn = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_event_class_read", new { name, includeBuiltIn }, () =>
            bridge.Dispatcher.Run(() =>
                new EventClassCoordinator(bridge.Dispatcher.Session).Read(name, includeBuiltIn)), cancellationToken);

    [McpServerTool(Name = "twincat_event_class_update", ReadOnly = false, Destructive = false)]
    [Description("Change an event class of the project: display text and/or event list. " +
                 "Only classes kept in the .tsproj. Logs out of the PLC first. The GUID stays, the PLC's TC_EVENTS follows at " +
                 "once and the project is saved. Events are matched by id: unmanaged details (cause/remedy, description) stay " +
                 "with a kept id; ids missing from the new list are removed. Pins in PLC projects (ExternalTypes.tmc) are " +
                 "rewritten with the new content while the solution is saved, closed and reopened. Refused with [Error] before " +
                 "anything changes: invalid arguments as in twincat_event_class_create, an unknown or built-in class, a class " +
                 "from a TMC file (change it in that file).")]
    public string EventClassUpdate(
        [Description("Name of the event class to change.")] string name,
        [Description("The complete new event list [{id, name, severity, text}], replacing the old one. Omit to keep the events.")] EventClassEventInput[]? events = null,
        [Description("New display text of the class. Omit to keep it.")] string? displayText = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_event_class_update", new { name, events = events?.Length, displayText }, () =>
            bridge.Dispatcher.Run(() =>
                new EventClassCoordinator(bridge.Dispatcher.Session).Update(name, displayText, events)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_add_global_data_type", ReadOnly = false, Destructive = false)]
    [Description("Pin a project data type or event class into a PLC project. " +
                 "Same as the XAE command \"Add Global Data Type\" for STRUCT, ENUM, ALIAS or event classes kept in the .tsproj: " +
                 "a library saved from the PLC then carries the type (for an event class, its users get TC_EVENTS.<class>). " +
                 "Project types it uses (members, base types, nested) are taken along; built-in types and types from a library, " +
                 "TMC file or PLC are not (the result warns). Logs out of the PLC first, then saves and closes the solution, " +
                 "writes ExternalTypes.tmc next to the .plcproj plus its .plcproj entry, and reopens it. Result: each pinned " +
                 "type's ExternalTypes.tmc node read back. Out-of-date pins are refreshed; if all are current nothing changes. " +
                 "Refused with [Error] before anything changes: an unknown, built-in or non-project type, an unknown PLC project, " +
                 "several PLC projects without plcProjectName, or a name collision (a DUT, POU or interface of that name in the " +
                 "PLC, or another type of that name or GUID in its ExternalTypes.tmc).")]
    public string PlcAddGlobalDataType(
        [Description("Name of the data type or event class in the type system, e.g. 'ST_AxisData' or 'MyLibEvents'.")] string typeName,
        [Description("The PLC by its name under PLC or its .plcproj name. Optional when the solution has exactly one PLC project.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_add_global_data_type", new { typeName, plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
                new EventClassCoordinator(bridge.Dispatcher.Session).AddGlobalDataType(typeName, plcProjectName)), cancellationToken);
}
