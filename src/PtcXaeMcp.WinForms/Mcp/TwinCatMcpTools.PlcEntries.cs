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
    [Description("List the entries of an element's declaration, structured: STRUCT/UNION fields, ENUM values, GVL variables, and the variables of a POU or " +
                 "MEMBER (METHOD, Get/Set) per section (VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR, VAR_STAT, VAR_TEMP, VAR_INST, VAR_GLOBAL, each optionally " +
                 "CONSTANT, RETAIN or PERSISTENT). One line per entry: line number, section, name, AT address, type, initial value, trailing comment, " +
                 "attributes ({attribute '…'} pragmas). An entry the editor cannot change safely (several names in one entry, several entries on one " +
                 "line, no ';') is listed with a note. section filters to one section. DUT ALIAS, INTERFACE bodies and ACTION/TRANSITION have no entries. " +
                 "kind is checked against the node's real type. " + PlcKindHelp)]
    public string PlcListEntries(
        [Description("Caret-separated tree path of the element (DUT, GVL, POU or MEMBER).")] string path,
        [Description("Element kind the node must have: DUT, GVL, POU or MEMBER.")] string kind,
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
    [Description("Add, change or remove ONE entry of an element's declaration, leaving the rest of the text byte-identical. op is add, set or remove. " +
                 "Entries: STRUCT/UNION fields, ENUM values (use value; the base type and attributes stay untouched), GVL variables (NORMAL and PARAMLIST), " +
                 "and the variables of a POU or MEMBER per section — a missing section is created by op=add. section (e.g. VAR_INPUT, 'VAR_GLOBAL PERSISTENT', " +
                 "'VAR RETAIN') is required when it is ambiguous. add: name and type (not for enum values); afterName inserts after that entry instead of at " +
                 "the end of the section. set: pass what changes — type, initialValue (\"\" removes it), value (enum, \"\" removes it), address (AT address such " +
                 "as %I*, \"\" removes it), comment (\"\" removes it), newName; attributes replaces the entry's attribute pragmas (a name such as hide, " +
                 "\"attribute 'hide'\" or a full {attribute 'hide'}), an empty list removes them, null leaves them unchanged. remove: name only; the entry's " +
                 "attribute lines go with it. Refused with an [Error], before the automatic logout and without a change: an unknown kind, a duplicate name, " +
                 "several names in one entry or several entries on one line, DUT ALIAS and INTERFACE bodies, a parameter that does not apply. The write is " +
                 "verified by read-back. With expectedRevision (from twincat_plc_read) the edit is refused, before the logout, when the element " +
                 "changed since that read; the result ends with the new 'Revision: …'. Renamed or removed entries break code and ADS callers — " +
                 "follow with twincat_rebuild_plc. " + PlcKindHelp)]
    public string PlcEditEntries(
        [Description("Caret-separated tree path of the element (DUT, GVL, POU or MEMBER).")] string path,
        [Description("Element kind the node must have: DUT, GVL, POU or MEMBER.")] string kind,
        [Description("Operation: add, set or remove.")] string op,
        [Description("Name of the entry (for add: the new entry).")] string name,
        [Description("Section of a POU/MEMBER/GVL declaration, e.g. VAR_INPUT or 'VAR_GLOBAL PERSISTENT'. Required when ambiguous; add creates a missing one.")] string? section = null,
        [Description("Data type, e.g. INT, ARRAY[0..3] OF REAL, POINTER TO BYTE. Required for add (not for enum values).")] string? type = null,
        [Description("Enum value (only for ENUM entries). Empty string removes an explicit value.")] string? value = null,
        [Description("Initial value (not for enum values). Empty string removes it.")] string? initialValue = null,
        [Description("AT address such as %I* or %QX0.0 (not for enum values). Empty string removes it.")] string? address = null,
        [Description("Trailing comment text. Empty string removes it.")] string? comment = null,
        [Description("op=add: insert after this existing entry instead of at the end of the section.")] string? afterName = null,
        [Description("op=set: new name for the entry.")] string? newName = null,
        [Description("Attribute pragmas of the entry. Each is a name (hide), \"attribute 'hide'\" or {attribute 'hide'}. Null leaves them unchanged, an empty array removes them.")] string[]? attributes = null,
        [Description("Revision twincat_plc_read reported for this element. When given, a different current revision refuses the edit (someone else changed it). Omit to edit unconditionally.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_edit_entries",
            new { path, kind, op, name, section, type, value, initialValue, address, comment, afterName, newName, attributes, expectedRevision }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session).EditEntries(
                    path, kind, op, name, section, type, value, initialValue, address, comment, afterName, newName, attributes,
                    expectedRevision)), cancellationToken);
}
