using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Globalization;

namespace PtcXaeMcp.WinForms;

// feature/plc-objects: the entries of a declaration (struct/union fields, enum values, GVL and POU variables per
// section) as structured data. Listing is a reading tool, editing a writing tool; they stay separate tools
// (McpServerTests.NonMutatingTools).
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_list_entries", ReadOnly = true)]
    [Description("List the declaration entries (fields, enum values, variables) of a PLC element. Covers STRUCT/UNION fields, " +
                 "ENUM values, GVL variables and POU/MEMBER variables per section (VAR_INPUT … VAR_GLOBAL, optionally CONSTANT/RETAIN/" +
                 "PERSISTENT). One line per entry: line number, section, name, AT address, type, initial value, comment, attribute pragmas. " +
                 "Entries twincat_plc_edit_entries cannot change safely (several names in one entry, several entries on one line, no ';') " +
                 "carry a note. DUT ALIAS, INTERFACE and ACTION/TRANSITION have no entries. " + PlcKindHelp)]
    public string PlcListEntries(
        [Description("Caret-separated tree path of a DUT, GVL, POU or MEMBER.")] string path,
        [Description("Kind the node must have: DUT, GVL, POU or MEMBER.")] string kind,
        [Description("Optional section filter, e.g. VAR_INPUT or 'VAR_GLOBAL PERSISTENT'.")] string? section = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_list_entries", new { path, kind, section }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, entries) = new PlcObjectCoordinator(bridge.Dispatcher.Session).ListEntries(path, kind, section);
                if (McpErrors.IsError(summary) || entries.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var e in entries)
                {
                    var line = new StringBuilder();
                    line.Append(CultureInfo.InvariantCulture, $"  L{e.Line,-4} [{e.Section}] {e.Name}");
                    if (e.Address is not null) line.Append(' ').Append(e.Address);
                    if (e.Type is not null) line.Append(" : ").Append(e.Type);
                    if (e.Initial is not null) line.Append(" := ").Append(e.Initial);
                    if (e.Comment is not null) line.Append("  // ").Append(e.Comment);
                    foreach (string a in e.Attributes) line.Append("  ").Append(a);
                    if (e.Problem is not null) line.Append("  [not editable: ").Append(e.Problem).Append(']');
                    sb.AppendLine(line.ToString());
                }
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_plc_edit_entries", ReadOnly = false, Destructive = true)]
    [Description("Add, change or remove one declaration entry, leaving the rest byte-identical. Works on STRUCT/UNION fields, ENUM " +
                 "values (via value; base type and attributes untouched), GVL variables (NORMAL, PARAMLIST) and POU/MEMBER variables per " +
                 "section; op=add creates a missing section. add: name + type (not for enum values). set: pass only what changes. " +
                 "remove: name only; its attribute lines go with it. Refused with [Error] before the logout, nothing changed: wrong kind, " +
                 "duplicate name, several names in one entry or several entries on one line, DUT ALIAS and INTERFACE, a parameter that does " +
                 "not apply, a stale expectedRevision. Logs the XAE out first (refused if that fails); verified by read-back; the result ends " +
                 "with the new 'Revision: …'. Renamed or removed entries break code and ADS callers — follow with twincat_rebuild_plc. " + PlcKindHelp)]
    public string PlcEditEntries(
        [Description("Caret-separated tree path of a DUT, GVL, POU or MEMBER.")] string path,
        [Description("Kind the node must have: DUT, GVL, POU or MEMBER.")] string kind,
        [Description("add, set or remove.")] string op,
        [Description("Entry name (for add: the new entry).")] string name,
        [Description("Section, e.g. VAR_INPUT, 'VAR_GLOBAL PERSISTENT', 'VAR RETAIN'. Required when ambiguous; add creates a missing one.")] string? section = null,
        [Description("Data type, e.g. ARRAY[0..3] OF REAL. Required for add (not for enum values).")] string? type = null,
        [Description("ENUM entries only: the value; \"\" removes it.")] string? value = null,
        [Description("Initial value (not for enum values); \"\" removes it.")] string? initialValue = null,
        [Description("AT address, e.g. %I* or %QX0.0 (not for enum values); \"\" removes it.")] string? address = null,
        [Description("Trailing comment; \"\" removes it.")] string? comment = null,
        [Description("op=add: insert after this entry instead of at the section end.")] string? afterName = null,
        [Description("op=set: new entry name.")] string? newName = null,
        [Description("Replaces the attribute pragmas; each hide, \"attribute 'hide'\" or {attribute 'hide'}. null keeps, [] removes them.")] string[]? attributes = null,
        [Description("Revision from twincat_plc_read; a different current revision refuses the edit. Omit to edit unconditionally.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_edit_entries",
            new { path, kind, op, name, section, type, value, initialValue, address, comment, afterName, newName, attributes, expectedRevision }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session).EditEntries(
                    path, kind, op, name, section, type, value, initialValue, address, comment, afterName, newName, attributes,
                    expectedRevision)), cancellationToken);
}
