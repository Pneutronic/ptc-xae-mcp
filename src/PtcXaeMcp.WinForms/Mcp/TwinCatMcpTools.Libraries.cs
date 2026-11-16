using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Globalization;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    internal const string PlcProjectNameHelp =
        "PLC name or its project name (e.g. 'PLC'). Empty = first PLC project; name it when there are several.";

    [McpServerTool(Name = "twincat_list_library_references", ReadOnly = true)]
    [Description("List the open project's PLC library references and what each resolves to. " +
                 "Reads the XAE library manager and compares with the .plcproj on disk (the .plcproj alone if the XAE is not " +
                 "ready). resolution: 'effective' (what the project compiles against), 'fixed' (concrete version referenced), " +
                 "'newest-installed' ('*' taken as the highest installed version, an approximation) or 'unresolved'. " +
                 "JSON: { success, summary, count, references [{ placeholder, name, version, publisher, source, filePinnedVersion, " +
                 "versionMatchesFile, resolvedName, resolvedVersion, resolvedDistributor, resolution }], warnings }.")]
    public string ListLibraryReferences(
        [Description("'text' (default, one line per reference) or 'json' (full records).")] string? format = null,
        [Description(PlcProjectNameHelp)] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_library_references", new { format, plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                string? formatError = LibraryReferenceFormat.Validate(format, out bool json);
                if (formatError is not null) return formatError;

                var (summary, items, warnings) = new LibraryCoordinator(bridge.Dispatcher.Session).ListReferencesResolved(plcProjectName);
                if (McpErrors.IsError(summary)) return summary;
                if (items.Count == 0)
                    return json ? LibraryReferenceFormat.Json(summary, items, warnings) : summary;
                return json ? LibraryReferenceFormat.Json(summary, items, warnings) : LibraryReferenceFormat.Text(summary, items);
            }), cancellationToken);

    [McpServerTool(Name = "twincat_list_installed_libraries", ReadOnly = true)]
    [Description("List the libraries installed in the local XAE library repository. Needs an open solution.")]
    public string ListInstalledLibraries(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_installed_libraries", null, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, libs) = new LibraryCoordinator(bridge.Dispatcher.Session).ListInstalled();
                if (McpErrors.IsError(summary) || libs.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var l in libs)
                {
                    string title = string.IsNullOrWhiteSpace(l.Title) ? string.Empty : $" — {l.Title}";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {l.Name} {l.Version} ({l.Publisher}){title}");
                }
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_check_libraries_installed", ReadOnly = true)]
    [Description("Check that every PLC library reference is installed in the local repository. " +
                 "Each one is Installed, Missing or VersionMismatch; version '*' matches any installed version of that name.")]
    public string CheckLibrariesInstalled(
        [Description(PlcProjectNameHelp)] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_check_libraries_installed", new { plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, items) = new LibraryCoordinator(bridge.Dispatcher.Session).CheckAvailability(plcProjectName);
                if (McpErrors.IsError(summary) || items.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var i in items)
                {
                    string installedNote = i.Match is null
                        ? "installed=-"
                        : $"installed={i.Match.Version}";
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"  [{i.Status}] {i.Reference.Name} {i.Reference.Version} ({i.Reference.Publisher}) [{installedNote}]");
                }
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_add_library_reference", ReadOnly = false, Destructive = false)]
    [Description("Add a PLC library or placeholder reference to the open project. " +
                 "A library that is not installed is refused with [Error] unless force=true. Returns the updated reference list.")]
    public string AddLibraryReference(
        [Description("Library name, e.g. 'Tc2_MC2'.")] string name,
        [Description("Version or '*' for latest (default).")] string version = "*",
        [Description("Distributor, e.g. 'Beckhoff Automation GmbH'. Optional.")] string? company = null,
        [Description("Add as placeholder instead of a direct library reference. Default false.")] bool asPlaceholder = false,
        [Description("Add even if the library is not installed locally. Default false.")] bool force = false,
        [Description(PlcProjectNameHelp)] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_library_reference",
            new { name, version, company, asPlaceholder, force, plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, refs) = new LibraryCoordinator(bridge.Dispatcher.Session)
                    .AddReference(name, version, company, asPlaceholder, force, plcProjectName);
                if (McpErrors.IsError(summary) || refs.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var r in refs)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {r.Placeholder}: {r.Name} {r.Version} ({r.Publisher})");
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_remove_library_reference", ReadOnly = false, Destructive = true)]
    [Description("Remove a PLC library or placeholder reference by name. " +
                 "No-op if absent. Returns the updated reference list.")]
    public string RemoveLibraryReference(
        [Description("Library or placeholder name to remove, e.g. 'Tc2_MC2'.")] string name,
        [Description(PlcProjectNameHelp)] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_remove_library_reference", new { name, plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, refs) = new LibraryCoordinator(bridge.Dispatcher.Session)
                    .RemoveReference(name, plcProjectName);

                if (McpErrors.IsError(summary))
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                if (refs.Count > 0)
                {
                    foreach (var r in refs)
                        sb.AppendLine(CultureInfo.InvariantCulture, $"  {r.Placeholder}: {r.Name} {r.Version} ({r.Publisher})");
                }
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_install_library", ReadOnly = false, Destructive = true)]
    [Description("Install a .library/.compiled-library file into the local 'System' repository. " +
                 "Needs an open solution.")]
    public string InstallLibrary(
        [Description("Absolute path to the .library/.compiled-library file.")] string libraryPath,
        [Description("Overwrite if the same version is already installed. Default false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_install_library", new { libraryPath, overwrite }, () =>
            bridge.Dispatcher.Run(() =>
            {
                if (McpPathGuard.CheckRequiredFile(libraryPath, bridge.Dispatcher.Session, "libraryPath") is { } pathError)
                    return pathError;

                var (summary, libs) = new LibraryCoordinator(bridge.Dispatcher.Session)
                    .Install(libraryPath, overwrite);
                if (McpErrors.IsError(summary) || libs.Count == 0)
                    return summary;
                return summary + $" ({libs.Count} libraries installed in total)";
            }), cancellationToken);

    [McpServerTool(Name = "twincat_save_as_library", ReadOnly = false, Destructive = true)]
    [Description("Export the open PLC project as a library file, optionally installing it. " +
                 "The outputPath extension selects '.library' (source) or '.compiled-library' (compiled); others are refused. " +
                 "The PLC project's Title (Project Information) must be set and the target directory must exist. Verified by " +
                 "the file existing and not being empty (size reported) and, with install, by the installed-library list. " +
                 "Needs an open solution.")]
    public string SaveAsLibrary(
        [Description("Absolute path ending in .library or .compiled-library, e.g. 'C:\\Libs\\PTC_Library.compiled-library'.")] string outputPath,
        [Description("Also install into the local XAE library repository. Default false.")] bool install = false,
        [Description("Replace an existing file at outputPath. Default false refuses.")] bool overwrite = false,
        [Description("Encrypt the compiled library (.compiled-library only; needs software protection on the project). Default false.")] bool encrypt = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_save_as_library", new { outputPath, install, overwrite, encrypt }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(outputPath, bridge.Dispatcher.Session, "outputPath")
                ?? new LibraryCoordinator(bridge.Dispatcher.Session).SaveAsLibrary(outputPath, install, overwrite, encrypt)), cancellationToken);

    [McpServerTool(Name = "twincat_library_signatures", ReadOnly = true)]
    [Description("Read the public API (FBs, functions, types) of the referenced PLC libraries. " +
                 "Use it before calling a library FB instead of guessing its inputs; compiled vendor libraries (Tc2_MC2, " +
                 "Tc3_EventLogger, …) have no readable source. Writes the XML to outputPath (outside the project) and returns per " +
                 "library name, version, distributor and type counts per kind; typeName adds that type's inputs, outputs and " +
                 "constants. Unresolved referenced libraries are named. Changes nothing in the project. FB methods and " +
                 "properties are probably not included.")]
    public string LibrarySignatures(
        [Description("Library name or placeholder, e.g. Tc2_MC2. Omit for all referenced libraries.")] string? library = null,
        [Description("Path of the XML file to write. Default %LOCALAPPDATA%\\ptc-xae-mcp\\library-signatures\\<solution>.library-signatures.xml.")] string? outputPath = null,
        [Description("Type whose inputs/outputs/constants are returned inline, case-insensitive, e.g. MC_MoveAbsolute.")] string? typeName = null,
        [Description("PLC name or its project name. Empty = first PLC project.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_library_signatures", new { library, outputPath, typeName, plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckOptionalFile(outputPath, bridge.Dispatcher.Session, "outputPath")
                ?? new LibrarySignatureCoordinator(bridge.Dispatcher.Session).Export(library, outputPath, typeName, plcProjectName)), cancellationToken);

    [McpServerTool(Name = "twincat_get_library_parameters", ReadOnly = true)]
    [Description("List the parameter-list overrides of a referenced PLC library placeholder. " +
                 "Such as GVL_PARAMETER constants like NUMBER_OF_ITEMS, as stored in the .plcproj; none = library defaults apply.")]
    public string GetLibraryParameters(
        [Description("Library or placeholder name, e.g. 'Sample-Library'.")] string library,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_library_parameters", new { library }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, pars) = new LibraryCoordinator(bridge.Dispatcher.Session)
                    .GetParameters(library);
                if (McpErrors.IsError(summary) || pars.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var p in pars)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {p.ListName}/{p.Key} = {p.Value}");
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_set_library_parameter", ReadOnly = false, Destructive = true)]
    [Description("Set or add one parameter-list override on a referenced PLC library placeholder. " +
                 "Saves and verifies the value in the .plcproj. Returns the updated override list.")]
    public string SetLibraryParameter(
        [Description("Library or placeholder name, e.g. 'Sample-Library'.")] string library,
        [Description("Parameter list name, e.g. 'GVL_PARAMETER'.")] string listName,
        [Description("Constant name, e.g. 'NUMBER_OF_ITEMS'.")] string key,
        [Description("New value, e.g. '101'.")] string value,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_library_parameter", new { library, listName, key, value }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, pars) = new LibraryCoordinator(bridge.Dispatcher.Session)
                    .SetParameter(library, listName, key, value);
                if (McpErrors.IsError(summary) || pars.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var p in pars)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {p.ListName}/{p.Key} = {p.Value}");
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_set_library_version", ReadOnly = false, Destructive = true)]
    [Description("Change the pinned version of an existing PLC library reference. " +
                 "Returns the updated reference list.")]
    public string SetLibraryVersion(
        [Description("Library name, e.g. 'Tc2_MC2'.")] string name,
        [Description("New version string, e.g. '3.3.73.0' or '*'.")] string version,
        [Description("Distributor override (optional).")] string? company = null,
        [Description(PlcProjectNameHelp)] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_library_version", new { name, version, company, plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, refs) = new LibraryCoordinator(bridge.Dispatcher.Session)
                    .SetVersion(name, version, company, plcProjectName);
                if (McpErrors.IsError(summary) || refs.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var r in refs)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {r.Placeholder}: {r.Name} {r.Version} ({r.Publisher})");
                return sb.ToString().TrimEnd();
            }), cancellationToken);
}
