using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Globalization;

namespace PtcXaeMcp.WinForms;

// feature/plc-objects: one schema for every manipulation of every PLC element — generic verbs plus a
// mandatory `kind` that is checked against the node's real ItemType.
// Reading verbs (list, read) and writing verbs (create, write, delete, rename) stay separate tools:
// McpServerTests.NonMutatingTools decides per tool name whether a test may call it against an open solution.
internal sealed partial class TwinCatMcpTools
{
    private const string PlcKindHelp =
        "kind is one of POU | INTERFACE | DUT | GVL | MEMBER | FOLDER (text elements: declaration and/or implementation) or " +
        "TEXTLIST | IMAGEPOOL | VISU | RECIPE | UML | TASKREF (list, create, rename, delete only: XAE does not expose their content " +
        "through the Automation Interface, so read/write/entries answer [Error]; TREND exists but cannot be created). " +
        "LIBRARY has its own tools (twincat_*_library_*).";

    [McpServerTool(Name = "twincat_plc_list", ReadOnly = true)]
    [Description("List PLC elements. Without arguments: every POU, INTERFACE, DUT, GVL and FOLDER of the open PLC project. " +
                 "kind filters by element kind; parentPath restricts to the subtree below a tree path. Under a POU, an INTERFACE or a " +
                 "PROPERTY, parentPath lists its MEMBERs (METHOD, ACTION, PROPERTY, Get/Set), including members inside folders under a POU; " +
                 "kind=MEMBER requires such a parentPath. recursive=false lists only the direct children. One line per element: " +
                 "kind/subtype, tree path, language. " + PlcKindHelp)]
    public string PlcList(
        [Description("Optional element kind filter, e.g. POU, DUT, GVL, MEMBER.")] string? kind = null,
        [Description("Optional caret-separated tree path; only elements below it are listed. Required for kind=MEMBER.")] string? parentPath = null,
        [Description("False lists only the direct children of parentPath. Default true.")] bool recursive = true,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_list", new { kind, parentPath, recursive }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, entries) = new PlcObjectCoordinator(bridge.Dispatcher.Session).List(kind, parentPath, recursive);
                if (McpErrors.IsError(summary) || entries.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var e in entries)
                {
                    string what = e.Subtype is null ? e.Kind : $"{e.Kind}/{e.Subtype}";
                    string lang = string.IsNullOrEmpty(e.Language) ? "" : $" [{e.Language}]";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {what,-24} {e.Path}{lang}");
                }
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_plc_create", ReadOnly = false, Destructive = false)]
    [Description("Create a PLC element under parentPath. kind + subtype pick the element: POU/PROGRAM|FUNCTION|FUNCTION_BLOCK, DUT/STRUCT|ENUM|UNION|ALIAS (default STRUCT; ALIAS needs a " +
                 "'TYPE <name> : <baseType>; END_TYPE' declaration), GVL/NORMAL|PARAMLIST (default NORMAL; persistent variables are a 'VAR_GLOBAL PERSISTENT' " +
                 "section of a NORMAL GVL — put it in the declaration), INTERFACE, MEMBER/METHOD|ACTION|PROPERTY|GET|SET (parentPath is the POU or INTERFACE; " +
                 "GET/SET are named Get/Set; PROPERTY gets its accessors per 'accessors'; TRANSITION under a POU is ST, implementation only), " +
                 "FOLDER (under a folder, the PLC project node, a POU or an INTERFACE), TEXTLIST/LOCAL, IMAGEPOOL/LOCAL, UML/CLASSDIAGRAM, " +
                 "VISU/MANAGER|VISUALIZATION and RECIPE/MANAGER (all under a folder or the PLC project node), VISU/TARGET|WEB (under a visualization " +
                 "manager), RECIPE/DEFINITION (under a recipe manager), TASKREF/PROGRAM (under a PLC task node; name = the PROGRAM to run). " +
                 "The GLOBAL text list / image pool, the text-list enumeration and the trend elements are refused by XAE and not offered. " +
                 "declaration optionally fills the new element (not for FOLDER, ACTION). returnType applies to FUNCTION, METHOD, PROPERTY (default BOOL); implLanguage " +
                 "(ST, IL, SFC, FBD, CFC, LD; default ST) to POUs and members; accessors (GET | SET | BOTH, default BOTH) to PROPERTY only. A parameter that does " +
                 "not apply to the element is an [Error]. Logs the XAE out first. Returns the new element's tree path. " +
                 "A new GVL, an FB nobody instantiates or a function is not compiled until a task's program uses it (or it carries " +
                 "{attribute 'linkalways'}); twincat_build_plc(expectSymbols=[...]) proves what landed in the compiled PLC. " + PlcKindHelp)]
    public string PlcCreate(
        [Description("Element kind: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER.")] string kind,
        [Description("Caret-separated tree path of the parent: a folder or the PLC project node; for kind=MEMBER the POU or INTERFACE.")] string parentPath,
        [Description("Name of the new element.")] string name,
        [Description("Subtype within the kind (required for POU and MEMBER), e.g. FUNCTION_BLOCK, STRUCT, PARAMLIST, METHOD. Optional for DUT (STRUCT), GVL (NORMAL), INTERFACE and FOLDER.")] string? subtype = null,
        [Description("Optional declaration text for the new element. TwinCAT's default skeleton remains when omitted.")] string? declaration = null,
        [Description("Return type for FUNCTION, METHOD and PROPERTY. Defaults to BOOL.")] string? returnType = null,
        [Description("Implementation language: ST, IL, SFC, FBD, CFC or LD. Defaults to ST.")] string? implLanguage = null,
        [Description("For subtype=PROPERTY only: GET, SET or BOTH (default).")] string? accessors = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_create",
            new { kind, subtype, parentPath, name, declaration, returnType, implLanguage, accessors }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session)
                    .Create(kind, subtype, parentPath, name, declaration, returnType, implLanguage, accessors)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_read", ReadOnly = true)]
    [Description("Read a PLC element's text by its caret-separated tree path: the declaration and, for elements that have one, the implementation " +
                 "(with its language; for SFC, FBD, CFC and LD the implementation is XAE's native XML). Only the sections the element has are returned (a DUT, GVL or INTERFACE has no implementation, an ACTION or " +
                 "TRANSITION no declaration). kind is checked against the node's real type before anything is read: a mismatch is an [Error] " +
                 "such as \"'<path>' is a GVL, not a DUT\". The first line ends with 'Revision: <16 hex digits>', a hash of the text read; pass it to " +
                 "twincat_plc_write(expectedRevision): the write is then refused when someone else changed the element in between. When openInEditor=true, also opens the element in the visible XAE editor (video mode), " +
                 "best-effort. " + PlcKindHelp)]
    public string PlcRead(
        [Description("Caret-separated tree path of the element to read.")] string path,
        [Description("Element kind the node must have: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER.")] string kind,
        [Description("When true, also opens the element in the visible XAE editor (video mode), best-effort. Default false.")] bool openInEditor = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_read", new { path, kind, openInEditor }, () =>
            bridge.Dispatcher.Run(() =>
            {
                if (openInEditor)
                {
                    try { bridge.Dispatcher.Session.OpenPlcObject(path); } catch (Exception ex) when (!ComCall.IsFinal(ex)) { } // a blocked XAE fails the tool now, not a second time in the read
                }

                var (summary, content) = new PlcObjectCoordinator(bridge.Dispatcher.Session).Read(path, kind);
                if (McpErrors.IsError(summary) || content is null)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                if (content.HasDeclaration)
                {
                    sb.AppendLine("--- Declaration ---");
                    sb.AppendLine(content.Declaration);
                }
                if (content.HasImplementation)
                {
                    sb.AppendLine("--- Implementation ---");
                    sb.AppendLine(content.Implementation);
                }
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_plc_write", ReadOnly = false, Destructive = true)]
    [Description("Replace the declaration and/or the implementation of an existing PLC element. A section that is null stays unchanged. " +
                 "Each section can be given inline (declaration/implementation) or read from a file (declarationFile/implementationFile); giving both " +
                 "forms for one section is an error. kind is checked against the node's real type before the automatic logout. A section the element " +
                 "does not have (an implementation for a DUT, GVL or INTERFACE; a declaration for an ACTION) is an [Error] before the logout. The " +
                 "implementation is text for ST and IL; for SFC, FBD, CFC and LD it is XAE's native XML exactly as twincat_plc_read returns it (root " +
                 "<NWL> for LD and FBD, <SFC>, <CFC>) — anything else is refused before the logout. A declaration can be written in every language. " +
                 "A POU/INTERFACE/GVL/DUT declaration that " +
                 "contains a METHOD/PROPERTY/ACTION header line is refused (members are separate elements — create them with twincat_plc_create). " +
                 "The write is verified by an independent read-back. With expectedRevision (from twincat_plc_read) the element is read first and " +
                 "the write is refused with [Error] — before the logout, nothing changed — when its text moved on since that read. The result " +
                 "ends with the new 'Revision: …' for the next edit. " + PlcKindHelp)]
    public string PlcWrite(
        [Description("Caret-separated tree path of the element to write.")] string path,
        [Description("Element kind the node must have: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER.")] string kind,
        [Description("New declaration text. Null leaves it unchanged. Alternative to 'declarationFile'.")] string? declaration = null,
        [Description("New implementation (ST source). Null leaves it unchanged. Alternative to 'implementationFile'.")] string? implementation = null,
        [Description("Path to a file whose contents become the declaration. Alternative to 'declaration'.")] string? declarationFile = null,
        [Description("Path to a file whose contents become the implementation. Alternative to 'implementation'.")] string? implementationFile = null,
        [Description("Revision twincat_plc_read reported for this element. When given, a different current revision refuses the write (someone else changed it). Omit to write unconditionally.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_write",
            new { path, kind, declaration, implementation, declarationFile, implementationFile, expectedRevision }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckOptionalFile(declarationFile, bridge.Dispatcher.Session, "declarationFile")
                ?? McpPathGuard.CheckOptionalFile(implementationFile, bridge.Dispatcher.Session, "implementationFile")
                ?? new PlcObjectCoordinator(bridge.Dispatcher.Session)
                    .Write(path, kind, declaration, implementation, declarationFile, implementationFile, expectedRevision)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_delete", ReadOnly = false, Destructive = true)]
    [Description("Delete a PLC element by its caret-separated tree path. kind is checked against the node's real type before the automatic logout. " +
                 "Whitelist: the PLC project root, the PLC instance, References and any node whose type cannot be read are refused. A FOLDER is deleted only " +
                 "when it is empty, or with recursive=true (then with everything in it). The delete is verified by an independent lookup. Deleting an " +
                 "element that is referenced elsewhere may break the build — follow with twincat_build_plc if that matters. " + PlcKindHelp)]
    public string PlcDelete(
        [Description("Caret-separated tree path of the element to delete.")] string path,
        [Description("Element kind the node must have: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER.")] string kind,
        [Description("FOLDER only: true deletes a non-empty folder with everything in it. Default false.")] bool recursive = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_delete", new { path, kind, recursive }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session).Delete(path, kind, recursive)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_rename", ReadOnly = false, Destructive = true)]
    [Description("Rename a PLC element by its caret-separated tree path. kind is checked against the node's real type before the automatic logout. " +
                 "Property accessors (Get/Set) have fixed names and cannot be renamed. Returns the new tree path. " +
                 "Renaming may make TwinCAT ask whether to update all references to the old name (the XAE's rename-via-refactoring prompt); the tool answers that prompt itself — " +
                 "\"Nein\" by default (only the element is renamed; callers keep the old name), \"Ja\" with updateReferences=true — and the result says whether it appeared and what was answered. Rebuild afterwards. " + PlcKindHelp)]
    public string PlcRename(
        [Description("Caret-separated tree path of the element to rename.")] string path,
        [Description("Element kind the node must have: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER.")] string kind,
        [Description("New name of the element.")] string newName,
        [Description(UpdateReferencesHelp)] bool updateReferences = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_rename", new { path, kind, newName, updateReferences }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session).Rename(path, kind, newName, updateReferences)), cancellationToken);

    internal const string UpdateReferencesHelp =
        "How to answer TwinCAT's prompt to update all references to the old name, if it asks: false (default) = \"Nein\", only the element is renamed; " +
        "true = \"Ja\", TwinCAT rewrites the references in the project.";
}
