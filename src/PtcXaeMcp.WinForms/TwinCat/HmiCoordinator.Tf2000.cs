using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-tf2000: what the project will do on a TF2000 runtime — group rights for every bound symbol (offline from
// the saved server configuration) and the engineering server endpoint for twincat_hmi_server_diagnostics.
internal sealed partial class HmiCoordinator
{
    // ---- check_access -------------------------------------------------------------

    /// <param name="liveModel">Rights read live from a TF2000 server (<see cref="HmiAccessCheck.ReadLiveModelAsync"/>);
    /// null = the project's saved configuration.</param>
    public (string Summary, string Json) CheckAccess(string? hmiProject, IReadOnlyList<string>? groups, IReadOnlyList<string>? paths,
        HmiAccessModel? liveModel = null, string? liveSource = null)
    {
        var (project, snap, error) = LoadSnapshot(hmiProject);
        if (snap is null) return (error!, "{}");
        var index = HmiComponents.Build(snap);

        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in index.Pages)
            if (snap.Files.TryGetValue(p.Path, out string? text)) pages[p.Path] = text;
        if (paths is { Count: > 0 })
        {
            var only = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in paths)
            {
                string? n = HmiPaths.Normalize(raw);
                if (n is null || !pages.TryGetValue(n, out string? t))
                    return ($"[Error] '{raw}' is not a page of '{project!.Name}' on disk (.view, .content or .usercontrol).", "{}");
                only[n] = t;
            }
            pages = only;
        }

        var model = liveModel ?? HmiAccessCheck.LoadModel(project!.ProjectDirectory);
        if (model.Groups.Count == 0)
            return ($"[Error] no user groups found {(liveSource is null ? $"for '{project!.Name}'" : "on the " + liveSource)} " +
                    $"({string.Join("; ", model.Problems.DefaultIfEmpty("USERGROUPS is empty"))}).", "{}");

        IReadOnlyList<HmiAccessGroup> checkedGroups;
        if (groups is { Count: > 0 })
        {
            var list = new List<HmiAccessGroup>();
            foreach (string g in groups)
            {
                var found = model.Group(g);
                if (found is null)
                    return ($"[Error] '{g}' is not a user group of '{project.Name}'. Groups: {string.Join(", ", model.Groups.Select(x => x.Name))}.", "{}");
                list.Add(found);
            }
            checkedGroups = list;
        }
        else checkedGroups = HmiAccessCheck.DefaultGroups(model);

        var report = HmiAccessCheck.Check(pages, model, checkedGroups);
        int reads = report.Findings.Count(f => f.Problem == "read");
        int writes = report.Findings.Count(f => f.Problem == "write");
        string summary = checkedGroups.Count == 0
            ? $"No group to check in '{project.Name}': no enabled group with users besides the administrators and guests — name groups explicitly."
            : $"Checked {report.UsesChecked} server-symbol use(s) in {report.FilesChecked} page(s) of '{project.Name}' for group(s) " +
              $"{string.Join(", ", report.GroupsChecked)}: " +
              (report.Findings.Count == 0 ? "every group can read every bound symbol and write every symbol a page writes."
                  : $"{reads} finding(s) 'cannot read', {writes} 'cannot write' (one per group, page and symbol).");
        if (report.Findings.Count > 0)
            summary += " First: " + string.Join("; ", report.Findings.Take(3).Select(f => $"{f.File}:{string.Join(",", f.Lines)} {f.Message}"));
        if (report.Notes.Count > 0) summary += " Notes: " + string.Join(" ", report.Notes);
        summary += liveSource is null
            ? " Source: saved server configuration (what a publish takes along); runtime-only changes on a target are not seen."
            : $" Source: rights from the {liveSource}; pages from the project on disk.";

        return (summary, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            source = liveSource ?? "disk (Server\\TcHmiSrv\\TcHmiSrv.Config.default.json, .remote.json, TcHmiUserManagement config)",
            groupsChecked = report.GroupsChecked,
            report.UsesChecked,
            report.FilesChecked,
            requireAuth = liveSource is null
                ? (object)new { projectDefault = model.RequireAuthDefault, remote = model.RequireAuthRemote, onTarget = model.RequireAuthOnTarget }
                : new { onTarget = model.RequireAuthOnTarget },
            groups = model.Groups.Select(g => new { g.Name, g.Enabled, g.SymbolAccess, explicitEntries = g.Symbols.Count, g.Users }),
            users = model.Users,
            findings = report.Findings,
            report.Notes,
        }, JsonOpts));
    }

    // ---- server_diagnostics: where the engineering server is --------------------------

    /// <summary>http://127.0.0.1:&lt;HmiCommunicationServerPort&gt; of an HMI project's engineering server.</summary>
    public (string? Endpoint, string? Error) EngineeringEndpoint(string? hmiProject)
    {
        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (null, error);
        var (project, resolveError) = ResolveProject(hmiProject, requireReady: false);
        if (project is null) return (null, resolveError);
        string? hmiproj = Directory.Exists(project.ProjectDirectory)
            ? Directory.GetFiles(project.ProjectDirectory, "*.hmiproj").FirstOrDefault() : null;
        if (hmiproj is null) return (null, $"[Error] no .hmiproj in {project.ProjectDirectory} — pass endpoint.");
        int? port = ServerPort(File.ReadAllText(hmiproj));
        return port is null
            ? (null, $"[Error] {Path.GetFileName(hmiproj)} has no HmiCommunicationServerPort — pass endpoint.")
            : ($"http://127.0.0.1:{port}", null);
    }
}
