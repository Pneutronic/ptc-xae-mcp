using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace PtcXaeMcp.WinForms;

// Project-wide search over the PLC code and the exact replacement of one piece of an element's text. Searching is a
// reading tool, replacing a writing tool; they stay separate tools (McpServerTests.NonMutatingTools).
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_search", ReadOnly = true)]
    [Description("Search declarations and implementations across the open PLC project(s). Covers every POU, METHOD, PROPERTY " +
                 "(Get/Set), ACTION, TRANSITION, INTERFACE, DUT and GVL, with unsaved editor changes. Each hit: kind and tree path, section, " +
                 "line:column (as in twincat_plc_read), the element's revision (expectedRevision for twincat_plc_replace_text / " +
                 "twincat_plc_write) and an excerpt with hit lines marked '>'. Graphical implementations (SFC/FBD/LD/CFC) are not searched " +
                 "and listed as skipped; their declarations are. Paged with limit/offset (last line: total, has_more, next_offset).")]
    public string PlcSearch(
        [Description("Text to find; a regular expression when regex=true.")] string pattern,
        [Description("True: pattern is a .NET regular expression (^ and $ per line). Default false.")] bool regex = false,
        [Description("True: match case exactly. Default false.")] bool caseSensitive = false,
        [Description("True: skip hits inside longer identifiers. Default false.")] bool wholeWord = false,
        [Description("declaration, implementation or both (default).")] string? scope = null,
        [Description("Optional caret-separated tree path: only this element and below (a folder, or a POU with its members).")] string? pathPrefix = null,
        [Description("Optional kind filter: POU, INTERFACE, DUT, GVL or MEMBER.")] string? kind = null,
        [Description("Context lines before and after each hit, 0–10. Default 1.")] int contextLines = 1,
        [Description("Hits per page: default 100, max 1000 (0 = 1000).")] int? limit = null,
        [Description("Hits to skip (next_offset of the previous page). Default 0.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_search",
            new { pattern, regex, caseSensitive, wholeWord, scope, pathPrefix, kind, contextLines, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
            {
                var r = new PlcObjectCoordinator(bridge.Dispatcher.Session)
                    .Search(pattern, regex, caseSensitive, wholeWord, scope, pathPrefix, kind, contextLines, limit, offset);
                if (McpErrors.IsError(r.Summary)) return r.Summary;

                var sb = new StringBuilder();
                sb.AppendLine(r.Summary);
                foreach (var m in r.Matches)
                {
                    string what = m.Subtype is null ? m.Kind : $"{m.Kind}/{m.Subtype}";
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"{what} {m.Path} — {m.Section} L{m.Line}:{m.Column} (Revision: {m.Revision})");
                    foreach (string line in m.Excerpt) sb.Append("    ").AppendLine(line);
                }
                if (r.Skipped.Count > 0)
                {
                    sb.AppendLine("Skipped:");
                    foreach (var s in r.Skipped) sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.Path}: {s.Reason}");
                }
                if (r.Page is not null) sb.AppendLine(r.Page.Footer());
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_plc_replace_text", ReadOnly = false, Destructive = true)]
    [Description("Replace an exact text snippet in a PLC element's declaration or implementation. The rest stays byte-identical " +
                 "(no need to rewrite the section with twincat_plc_write). oldText must occur exactly once (case and whitespace as " +
                 "twincat_plc_read returns them; LF also matches CRLF) unless occurrence picks one. Refused with [Error] before the logout, " +
                 "nothing changed: oldText not found (nearest match shown) or found several times (lines listed), a missing section, a " +
                 "graphical implementation (SFC/FBD/LD/CFC: use twincat_plc_write), a METHOD/PROPERTY/ACTION header in a " +
                 "POU/INTERFACE/GVL/DUT declaration, a stale expectedRevision. Logs the XAE out first (refused if that fails); verified by " +
                 "read-back. Returns the changed lines with two lines of context and the new revision. " + PlcKindHelp)]
    public string PlcReplaceText(
        [Description("Caret-separated tree path of the element.")] string path,
        [Description("Kind the node must have: POU, INTERFACE, DUT, GVL or MEMBER.")] string kind,
        [Description("declaration or implementation.")] string section,
        [Description("Exact text to replace (may span lines).")] string oldText,
        [Description("Replacement; \"\" deletes oldText.")] string newText,
        [Description("1-based occurrence when oldText occurs several times; omit to require exactly one.")] int? occurrence = null,
        [Description("Revision from twincat_plc_read or twincat_plc_search; a different current revision refuses the replacement.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_replace_text",
            new { path, kind, section, oldText, newText, occurrence, expectedRevision }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session)
                    .ReplaceText(path, kind, section, oldText, newText, occurrence, expectedRevision)), cancellationToken);
}
