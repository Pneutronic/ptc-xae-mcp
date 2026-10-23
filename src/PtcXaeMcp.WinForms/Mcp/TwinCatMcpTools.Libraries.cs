using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Globalization;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_list_library_references", ReadOnly = true)]
    [Description("List PLC library references for the open project. Reads the live XAE library manager when available and compares against the .plcproj on disk; falls back to the .plcproj alone when XAE is not ready. " +
                 "Each reference also shows the concrete library it resolves to (resolvedName, resolvedVersion, resolvedDistributor) and how that was determined (resolution): " +
                 "'effective' = the library manager's effective resolution of the placeholder (what the project compiles against), 'fixed' = the reference names a concrete version, " +
                 "'newest-installed' = '*'/newest resolved to the highest installed version (a computed approximation, see warnings), 'unresolved' = no matching library. " +
                 "format='text' (default): one line per reference with the suffix [resolved: <name> <version> (<distributor>), <resolution>]. " +
                 "format='json': { success, summary, count, references [{ placeholder, name, version, publisher, source, filePinnedVersion, versionMatchesFile, resolvedName, resolvedVersion, resolvedDistributor, resolution }], warnings }.")]
    public string ListLibraryReferences(
        [Description("Output format: 'text' (default) or 'json' (full records).")] string? format = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_library_references", new { format }, () =>
            bridge.Dispatcher.Run(() =>
            {
                string? formatError = LibraryReferenceFormat.Validate(format, out bool json);
                if (formatError is not null) return formatError;

                var (summary, items, warnings) = new LibraryCoordinator(bridge.Dispatcher.Session).ListReferencesResolved();
                if (McpErrors.IsError(summary)) return summary;
                if (items.Count == 0)
                    return json ? LibraryReferenceFormat.Json(summary, items, warnings) : summary;
                return json ? LibraryReferenceFormat.Json(summary, items, warnings) : LibraryReferenceFormat.Text(summary, items);
            }), cancellationToken);

    [McpServerTool(Name = "twincat_list_installed_libraries", ReadOnly = true)]
    [Description("List libraries installed in the local TwinCAT XAE library repository (requires XAE running with a solution open).")]
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
    [Description("Check whether every PLC library reference of the open project is installed in the local XAE repository. Reports each reference as Installed, Missing, or VersionMismatch. Reference version '*' is satisfied by any installed version with the same name.")]
    public string CheckLibrariesInstalled(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_check_libraries_installed", null, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, items) = new LibraryCoordinator(bridge.Dispatcher.Session).CheckAvailability();
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
    [Description("Add a PLC library (or placeholder) reference to the open project's References node. " +
                 "name: library name (required). version: pinned version or '*' for latest (default '*'). " +
                 "company: distributor name (optional). asPlaceholder: add as a placeholder reference (default false). " +
                 "force: bypass the install-guard and add even if the library is not installed (default false). " +
                 "Returns the updated reference list.")]
    public string AddLibraryReference(
        [Description("Library name, e.g. 'Tc2_MC2'.")] string name,
        [Description("Version string or '*' for latest.")] string version = "*",
        [Description("Distributor/company, e.g. 'Beckhoff Automation GmbH'.")] string? company = null,
        [Description("Add as placeholder reference instead of a direct library reference.")] bool asPlaceholder = false,
        [Description("Add even if the library is not installed in the local XAE repository.")] bool force = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_library_reference",
            new { name, version, company, asPlaceholder, force }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, refs) = new LibraryCoordinator(bridge.Dispatcher.Session)
                    .AddReference(name, version, company, asPlaceholder, force);
                if (McpErrors.IsError(summary) || refs.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var r in refs)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {r.Placeholder}: {r.Name} {r.Version} ({r.Publisher})");
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_remove_library_reference", ReadOnly = false, Destructive = true)]
    [Description("Remove a PLC library or placeholder reference from the open project by name. " +
                 "No-op if the reference is not present. Returns the updated reference list.")]
    public string RemoveLibraryReference(
        [Description("Library or placeholder name to remove, e.g. 'Tc2_MC2'.")] string name,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_remove_library_reference", new { name }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, refs) = new LibraryCoordinator(bridge.Dispatcher.Session)
                    .RemoveReference(name);

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
    [Description("Install a .library/.compiled-library file into the local XAE library repository " +
                 "(repository 'System'). Requires XAE running with a solution open.")]
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
    [Description("Save as library / Save as library and install: export the open PLC project as a .library " +
                 "file via ITcPlcIECProject.SaveAsLibrary — the counterpart to twincat_install_library. " +
                 "install: also register it in the local XAE library repository (default false). " +
                 "Requires XAE running with a solution open; the target directory must already exist.")]
    public string SaveAsLibrary(
        [Description("Absolute path the .library file should be written to, e.g. 'C:\\Libs\\PTC_Library.library'.")] string outputPath,
        [Description("Also install into the local XAE library repository. Default false.")] bool install = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_save_as_library", new { outputPath, install }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(outputPath, bridge.Dispatcher.Session, "outputPath")
                ?? new LibraryCoordinator(bridge.Dispatcher.Session).SaveAsLibrary(outputPath, install)), cancellationToken);

    [McpServerTool(Name = "twincat_get_library_parameters", ReadOnly = true)]
    [Description("List the parameter-list overrides of a referenced PLC library placeholder " +
                 "(e.g. GVL_PARAMETER constants like NUMBER_OF_ITEMS), as persisted in the .plcproj. " +
                 "No overrides means the library defaults apply.")]
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
    [Description("Set (or add) one parameter-list override on a referenced PLC library placeholder — " +
                 "e.g. GVL_PARAMETER/NUMBER_OF_ITEMS = 101 on 'Sample-Library'. Writes via the XAE " +
                 "References node (ProduceXml→ConsumeXml), saves, and verifies the value persisted " +
                 "to the .plcproj. Returns the updated override list.")]
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
                 "name: library name (required). version: new version string or '*' (required). " +
                 "company: distributor override (optional). Returns the updated reference list.")]
    public string SetLibraryVersion(
        [Description("Library name, e.g. 'Tc2_MC2'.")] string name,
        [Description("New version string, e.g. '3.3.73.0' or '*'.")] string version,
        [Description("Distributor override (optional).")] string? company = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_library_version", new { name, version, company }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, refs) = new LibraryCoordinator(bridge.Dispatcher.Session)
                    .SetVersion(name, version, company);
                if (McpErrors.IsError(summary) || refs.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var r in refs)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {r.Placeholder}: {r.Name} {r.Version} ({r.Publisher})");
                return sb.ToString().TrimEnd();
            }), cancellationToken);
}
