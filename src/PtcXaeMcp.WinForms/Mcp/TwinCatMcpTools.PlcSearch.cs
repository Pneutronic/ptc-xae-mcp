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
    [Description("Search the declarations and implementations of every POU, METHOD, PROPERTY (Get/Set), ACTION, TRANSITION, INTERFACE, DUT and GVL " +
                 "of the open PLC project(s) — instead of reading every element. The text is the one twincat_plc_read returns (unsaved editor " +
                 "changes included). Matching is case-insensitive by default (ST is); regex=true takes a .NET regular expression (^ and $ per line), " +
                 "wholeWord=true skips hits inside longer identifiers. Each hit: element kind and tree path, section, line:column (the line numbers of " +
                 "twincat_plc_read), the element's revision (pass it to twincat_plc_replace_text / twincat_plc_write as expectedRevision) and an " +
                 "excerpt with the hit lines marked '>'. Graphical implementations (SFC, FBD, LD, CFC) are XML and not searched; they are listed as " +
                 "skipped, their declarations are searched. Paged: limit (default 100) and offset; the summary says the next offset.")]
    public string PlcSearch(
        [Description("Text to find; a regular expression when regex=true.")] string pattern,
        [Description("True: pattern is a .NET regular expression. Default false (literal text).")] bool regex = false,
        [Description("True: match case exactly. Default false (ST identifiers are case-insensitive).")] bool caseSensitive = false,
        [Description("True: only whole identifiers (no letter, digit or '_' directly before or after the hit).")] bool wholeWord = false,
        [Description("declaration, implementation or both (default).")] string? scope = null,
        [Description("Optional caret-separated tree path: only this element and the elements below it, e.g. a folder or a POU with its members.")] string? pathPrefix = null,
        [Description("Optional element kind filter: POU, INTERFACE, DUT, GVL or MEMBER.")] string? kind = null,
        [Description("Lines of context before and after each hit, 0–10. Default 1.")] int contextLines = 1,
        [Description("Hits per page, 1–1000. Default 100.")] int limit = 100,
        [Description("Hits to skip (paging). Default 0.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_search",
            new { pattern, regex, caseSensitive, wholeWord, scope, pathPrefix, kind, contextLines, limit, offset }, () =>
            bridge.Dispatcher.Run(() =>
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
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_plc_replace_text", ReadOnly = false, Destructive = true)]
    [Description("Replace one exact piece of text in the declaration or the implementation of one PLC element, leaving the rest byte-identical — " +
                 "instead of rewriting the whole section with twincat_plc_write. oldText must occur exactly once (case and whitespace as " +
                 "twincat_plc_read returns them; LF in oldText/newText also matches CRLF), or pass occurrence to pick the n-th. Refused with [Error], " +
                 "before the automatic logout and without a change: oldText not found (the message says where it nearly matched), found several " +
                 "times (with the lines), a section the element does not have, a graphical implementation (SFC/FBD/LD/CFC: use twincat_plc_write), " +
                 "a METHOD/PROPERTY/ACTION header put into a POU/INTERFACE/GVL/DUT declaration, or expectedRevision (from twincat_plc_read or " +
                 "twincat_plc_search) that no longer matches. The write is verified by an independent read-back. Returns the changed lines with " +
                 "two lines of context and the new revision for the next edit. " + PlcKindHelp)]
    public string PlcReplaceText(
        [Description("Caret-separated tree path of the element.")] string path,
        [Description("Element kind the node must have: POU, INTERFACE, DUT, GVL or MEMBER.")] string kind,
        [Description("declaration or implementation.")] string section,
        [Description("The exact text to replace (may span lines).")] string oldText,
        [Description("The replacement; an empty string deletes oldText.")] string newText,
        [Description("Optional 1-based occurrence when oldText occurs several times. Omit to require exactly one.")] int? occurrence = null,
        [Description("Revision twincat_plc_read or twincat_plc_search reported for this element. When given, a different current revision refuses the replacement (someone else changed it).")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_replace_text",
            new { path, kind, section, oldText, newText, occurrence, expectedRevision }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session)
                    .ReplaceText(path, kind, section, oldText, newText, occurrence, expectedRevision)), cancellationToken);
}
