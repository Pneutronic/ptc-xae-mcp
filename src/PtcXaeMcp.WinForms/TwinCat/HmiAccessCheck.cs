using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-tf2000: twincat_hmi_check_access — can the user groups of the project read (and, where a page writes,
// write) every server symbol the pages bind? In the engineering server everything runs as a local
// administrator, so missing rights only show on the TF2000 runtime: empty fields and writes that do nothing.
//
// Rights model (Server\TcHmiSrv\TcHmiSrv.Config.default.json, which the server writes on every change):
//   USERGROUPS.<group> = { ENABLED, SYMBOLACCESS (default for every symbol), SYMBOLS { <symbol>: access } }
//   access: 0 none, 1 read, 2 write, 3 read+write. An explicit entry wins over SYMBOLACCESS, also downwards.
//   USERGROUPUSERS.<auth domain>.<user>.USERGROUPUSERS_GROUPS = [ groups ].
// Verified live 2026-11-19 (server 24.0.7, forced-auth endpoint, logged in as a user of a test group with
// SYMBOLACCESS 1 and entries Diagnostics 3 / ListUsers 0): GetSymbolAccess answered 3, 0, 1, 1 exactly as
// modelled; as __SystemGuest likewise (DefaultUserGroup 1, others 0). Entries on a sub-symbol path
// ("A::b") are looked up most-specific first — not yet verified live.

internal sealed record HmiAccessGroup(string Name, bool Enabled, int SymbolAccess, IReadOnlyDictionary<string, int> Symbols,
    IReadOnlyList<string> Users);

internal sealed record HmiAccessUser(string Name, bool Enabled, bool PasswordSet, IReadOnlyList<string> Groups);

internal sealed record HmiAccessModel(IReadOnlyList<HmiAccessGroup> Groups, IReadOnlyList<HmiAccessUser> Users,
    int? RequireAuthDefault, int? RequireAuthRemote, IReadOnlyList<string> Problems)
{
    /// <summary>What the published (remote) server will use: the remote override, else the project default.</summary>
    public int? RequireAuthOnTarget => RequireAuthRemote ?? RequireAuthDefault;

    public HmiAccessGroup? Group(string name) =>
        Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The access of a group to a symbol path: the most specific explicit entry ("A::b::c", "A::b",
    /// "A"), else the group's SYMBOLACCESS.</summary>
    public static (int Access, string Source) Effective(HmiAccessGroup g, string symbolPath)
    {
        string p = symbolPath;
        while (true)
        {
            if (g.Symbols.TryGetValue(p, out int a)) return (a, $"entry '{p}'");
            int cut = p.LastIndexOf("::", StringComparison.Ordinal);
            if (cut <= 0) break;
            p = p[..cut];
        }
        return (g.SymbolAccess, "SYMBOLACCESS default");
    }
}

/// <summary>One place a page reads or writes a server symbol.</summary>
internal sealed record HmiSymbolUse(string File, int Line, string? ControlId, string Attribute, string Symbol, bool Writes, string Why);

internal sealed record HmiAccessFinding(string Severity, string Group, string Symbol, string Problem, int Access, string AccessSource,
    string File, IReadOnlyList<int> Lines, int Count, IReadOnlyList<string> Controls, string Message);

internal sealed record HmiAccessReport(IReadOnlyList<string> GroupsChecked, int UsesChecked, int FilesChecked,
    IReadOnlyList<HmiAccessFinding> Findings, IReadOnlyList<string> Notes);

internal static class HmiAccessCheck
{
    internal const string AdministratorsGroup = "__SystemAdministrators";
    internal const string GuestsGroup = "__SystemGuests";
    internal const string ServerConfigRemoteRelativePath = @"Server\TcHmiSrv\TcHmiSrv.Config.remote.json";
    internal const string UserManagementConfigRelativePath = @"Server\TcHmiUserManagement\TcHmiUserManagement.Config.default.json";

    // Attributes whose symbol the control writes: the state symbol of Checkbox/Button/ToggleButton/…, the
    // DataSymbol of the Datagrid ("changes in the datagrid will be written back", Controls 14.8.3).
    private static readonly HashSet<string> WritingAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "data-tchmi-state-symbol", "data-tchmi-data-symbol",
    };

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    // ---- model -------------------------------------------------------------------

    public static HmiAccessModel LoadModel(string projectDirectory)
    {
        var problems = new List<string>();
        JsonNode? srv = ReadJson(Path.Combine(projectDirectory, HmiInternalSymbols.ServerConfigRelativePath), problems);
        JsonNode? remote = ReadJson(Path.Combine(projectDirectory, ServerConfigRemoteRelativePath), problems, optional: true);
        JsonNode? um = ReadJson(Path.Combine(projectDirectory, UserManagementConfigRelativePath), problems, optional: true);
        return BuildModel(srv, remote, um, problems);
    }

    /// <summary>Symbols that hold the rights on a running server (verified live 2026-11-19 on a TF2000 24.0.7 service as
    /// __SystemAdministrator; USERS carries password hashes, of which only "set or not" is used).</summary>
    internal static readonly string[] LiveRightsSymbols =
    [
        "TcHmiSrv.Config::USERGROUPS", "TcHmiSrv.Config::USERGROUPUSERS", "TcHmiSrv.Config::REQUIREAUTH",
        "TcHmiUserManagement.Config::USERS",
    ];

    /// <summary>The rights model of a running server, read through its WebSocket API.</summary>
    public static async Task<(HmiAccessModel? Model, string? Error)> ReadLiveModelAsync(string endpoint, string? user, string? password,
        bool trustServerCertificate, int timeoutMs, CancellationToken ct)
    {
        var commands = new JsonArray();
        foreach (string s in LiveRightsSymbols) commands.Add(new JsonObject { ["symbol"] = s, ["commandOptions"] = new JsonArray("SendErrorMessage") });
        var (answer, error) = await HmiServerApi.QueryAsync(endpoint, user, password, trustServerCertificate, commands, timeoutMs, ct)
            .ConfigureAwait(false);
        return answer is null ? (null, error) : LiveModel(answer);
    }

    internal static (HmiAccessModel? Model, string? Error) LiveModel(JsonObject answer)
    {
        var denied = new List<string>();
        foreach (JsonNode? c in answer["commands"] as JsonArray ?? [])
            if (HmiServerApi.ErrorOf(c) is { } e) denied.Add($"{c?["symbol"]} ({e})");
        if (denied.Count > 0)
            return (null, "the server does not let this user read its rights: " + string.Join(", ", denied) +
                          ". Log in as a user who may read TcHmiSrv.Config (e.g. an administrator) with user and passwordVariable.");
        var srv = new JsonObject
        {
            ["USERGROUPS"] = HmiServerApi.ReadValue(answer, LiveRightsSymbols[0])?.DeepClone(),
            ["USERGROUPUSERS"] = HmiServerApi.ReadValue(answer, LiveRightsSymbols[1])?.DeepClone(),
            ["REQUIREAUTH"] = HmiServerApi.ReadValue(answer, LiveRightsSymbols[2])?.DeepClone(),
        };
        var um = new JsonObject { ["USERS"] = HmiServerApi.ReadValue(answer, LiveRightsSymbols[3])?.DeepClone() };
        return (BuildModel(srv, null, um), null);
    }

    internal static HmiAccessModel BuildModel(JsonNode? srv, JsonNode? remote, JsonNode? userManagement, List<string>? problems = null)
    {
        problems ??= [];
        var membership = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // group → users
        var userGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // user → groups
        if (srv?["USERGROUPUSERS"] is JsonObject domains)
        {
            foreach (var (_, usersNode) in domains)
            {
                if (usersNode is not JsonObject domainUsers) continue;
                foreach (var (user, cfg) in domainUsers)
                {
                    var groups = (cfg?["USERGROUPUSERS_GROUPS"] as JsonArray)?.Select(g => g?.GetValue<string>()).OfType<string>().ToList() ?? [];
                    userGroups[user] = groups;
                    foreach (string g in groups)
                    {
                        if (!membership.TryGetValue(g, out var list)) membership[g] = list = [];
                        list.Add(user);
                    }
                }
            }
        }

        var groupList = new List<HmiAccessGroup>();
        if (srv?["USERGROUPS"] is JsonObject groupsNode)
        {
            foreach (var (name, g) in groupsNode)
            {
                var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
                if (g?["SYMBOLS"] is JsonObject sy)
                    foreach (var (s, a) in sy)
                        if (TryInt(a, out int v)) symbols[s] = v;
                groupList.Add(new HmiAccessGroup(name, Bool(g?["ENABLED"], true), TryInt(g?["SYMBOLACCESS"], out int def) ? def : 0,
                    symbols, membership.TryGetValue(name, out var u) ? u : []));
            }
        }
        else if (srv is not null) problems.Add("the server configuration has no USERGROUPS.");

        var users = new List<HmiAccessUser>();
        if (userManagement?["USERS"] is JsonObject umUsers)
        {
            foreach (var (name, u) in umUsers)
                users.Add(new HmiAccessUser(name, Bool(u?["ENABLED"], true), !string.IsNullOrEmpty(Str(u?["PASSWORD"])),
                    userGroups.TryGetValue(name, out var gs) ? gs : []));
        }

        return new HmiAccessModel(groupList, users,
            TryInt(srv?["REQUIREAUTH"], out int ra) ? ra : null,
            TryInt(remote?["REQUIREAUTH"], out int rr) ? rr : null, problems);
    }

    // ---- uses ----------------------------------------------------------------------

    /// <summary>Every server symbol (<c>%s%</c>) a page binds, with whether the page writes it.</summary>
    public static IReadOnlyList<HmiSymbolUse> Uses(string file, string text)
    {
        var doc = HmiMarkup.Parse(text);
        var list = new List<HmiSymbolUse>();
        foreach (var el in doc.Elements)
        {
            foreach (var (attr, value) in el.Attributes)
            {
                if (attr.Equals("id", StringComparison.OrdinalIgnoreCase) || attr.Equals("data-tchmi-type", StringComparison.OrdinalIgnoreCase)) continue;
                AddFromText(list, file, el, attr, value, WritingAttributes.Contains(attr) ? "writing attribute" : null);
            }
            foreach (var (attr, json) in el.ScriptAttributes)
            {
                JsonNode? node = null;
                try { node = JsonNode.Parse(json, documentOptions: Lenient); }
                catch (JsonException) { /* not JSON — scan the raw text */ }
                if (node is null) { AddFromText(list, file, el, attr, json, WritingAttributes.Contains(attr) ? "writing attribute" : null); continue; }
                Walk(node, null, null, (text2, writes) => AddFromText(list, file, el, attr, text2, writes));
            }
        }
        return list;
    }

    // A trigger action { "objectType": "WriteToSymbol", "symbolExpression": "%s%X%/s%", "valueSource": … } writes X;
    // every other expression inside the JSON is read.
    private static void Walk(JsonNode node, JsonObject? parent, string? property, Action<string, string?> onText)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o) if (v is not null) Walk(v, o, k, onText);
                break;
            case JsonArray a:
                foreach (var v in a) if (v is not null) Walk(v, null, null, onText);
                break;
            case JsonValue v when v.TryGetValue(out string? s):
                bool write = parent is not null && property == "symbolExpression" &&
                             string.Equals(Str(parent["objectType"]), "WriteToSymbol", StringComparison.Ordinal);
                onText(s, write ? "WriteToSymbol action" : null);
                break;
        }
    }

    private static void AddFromText(List<HmiSymbolUse> list, string file, HmiElement el, string attr, string text, string? writeReason)
    {
        if (!text.Contains("%s%", StringComparison.Ordinal)) return;
        foreach (var b in HmiMarkup.Bindings(text))
        {
            if (b.Kind != "s") continue;
            string body = b.Expression.Length > 7 ? b.Expression[3..^4] : "";
            string path = SymbolPath(body);
            if (path.Length == 0 || path.Contains('%', StringComparison.Ordinal)) continue;   // dynamic name — not checkable
            bool twoWay = body.Contains("BindingMode=TwoWay", StringComparison.OrdinalIgnoreCase);
            string? why = writeReason ?? (twoWay ? "TwoWay binding" : null);
            list.Add(new HmiSymbolUse(file, el.Line, el.Id, attr, path, why is not null, why ?? "binding"));
        }
    }

    /// <summary>The symbol path of a %s% body: options (<c>|…</c>) and array indexes cut, sub-symbols (<c>::</c>) kept.</summary>
    internal static string SymbolPath(string body)
    {
        string b = body.Trim();
        int bar = b.IndexOf('|', StringComparison.Ordinal);
        if (bar >= 0) b = b[..bar];
        int bracket = b.IndexOf('[', StringComparison.Ordinal);
        if (bracket >= 0) b = b[..bracket];
        return b.Trim().TrimEnd(':');
    }

    // ---- check ---------------------------------------------------------------------

    /// <summary>The groups checked when none are named: enabled groups that have users, except the
    /// administrators (full access) and the guests (not logged in).</summary>
    public static IReadOnlyList<HmiAccessGroup> DefaultGroups(HmiAccessModel model) =>
        model.Groups.Where(g => g.Enabled && g.Users.Count > 0 &&
                                !g.Name.Equals(AdministratorsGroup, StringComparison.OrdinalIgnoreCase) &&
                                !g.Name.Equals(GuestsGroup, StringComparison.OrdinalIgnoreCase)).ToList();

    public static HmiAccessReport Check(IReadOnlyDictionary<string, string> pages, HmiAccessModel model, IReadOnlyList<HmiAccessGroup> groups)
    {
        var uses = new List<HmiSymbolUse>();
        foreach (var (file, text) in pages.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) uses.AddRange(Uses(file, text));

        var findings = new List<HmiAccessFinding>();
        foreach (var g in groups)
        {
            var raw = new List<(HmiSymbolUse Use, string Problem, int Access, string Source)>();
            foreach (var u in uses)
            {
                var (access, source) = HmiAccessModel.Effective(g, u.Symbol);
                if ((access & 1) == 0) raw.Add((u, "read", access, source));
                else if (u.Writes && (access & 2) == 0) raw.Add((u, "write", access, source));
            }
            foreach (var grp in raw.GroupBy(r => (r.Use.File, r.Use.Symbol, r.Problem)))
            {
                var first = grp.First();
                var lines = grp.Select(r => r.Use.Line).Distinct().Order().ToList();
                var controls = grp.Select(r => r.Use.ControlId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                string what = first.Problem == "read"
                    ? $"group '{g.Name}' cannot read {first.Use.Symbol} (access {first.Access} from {first.Source}) — the control stays empty on the runtime"
                    : $"group '{g.Name}' cannot write {first.Use.Symbol} (access {first.Access} from {first.Source}), but {Reasons(grp.Select(r => r.Use))} writes it — the write is refused on the runtime";
                findings.Add(new HmiAccessFinding("error", g.Name, first.Use.Symbol, first.Problem, first.Access, first.Source,
                    first.Use.File, lines, grp.Count(), controls, what));
            }
        }

        var notes = new List<string>();
        int? auth = model.RequireAuthOnTarget;
        if (auth == 0)
            notes.Add("REQUIREAUTH is 0 for the published server (TcHmiSrv.Config" + (model.RequireAuthRemote is null ? ".default" : ".remote") +
                      ".json): nobody logs in on the runtime, so these group rights decide nothing about who operates the HMI.");
        else if (auth is not null)
            notes.Add($"REQUIREAUTH {auth.Value.ToString(CultureInfo.InvariantCulture)} for the published server — users log in, their groups' rights apply.");
        var empty = model.Groups.Where(g => g.Enabled && g.Users.Count == 0 && !g.Name.StartsWith("__", StringComparison.Ordinal)).Select(g => g.Name).ToList();
        if (empty.Count > 0) notes.Add($"group(s) without users in the project: {string.Join(", ", empty)}.");
        var noPassword = model.Users.Where(u => u.Enabled && !u.PasswordSet && !u.Name.StartsWith("__", StringComparison.Ordinal)).Select(u => u.Name).ToList();
        if (noPassword.Count > 0) notes.Add($"enabled user(s) without a password in the project: {string.Join(", ", noPassword)}.");
        notes.AddRange(model.Problems);

        return new HmiAccessReport(groups.Select(g => g.Name).ToList(), uses.Count, pages.Count,
            findings.OrderBy(f => f.Group, StringComparer.Ordinal).ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Lines[0]).ToList(),
            notes);
    }

    private static string Reasons(IEnumerable<HmiSymbolUse> uses) =>
        string.Join(" and ", uses.Select(u => u.Why == "writing attribute" ? $"{u.Attribute}" : u.Why).Distinct(StringComparer.Ordinal));

    // ---- helpers -------------------------------------------------------------------

    private static JsonNode? ReadJson(string path, List<string> problems, bool optional = false)
    {
        if (!File.Exists(path))
        {
            if (!optional) problems.Add($"{Path.GetFileName(path)} not found — the server has not started for this project yet.");
            return null;
        }
        try
        {
            string text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text, documentOptions: Lenient);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(path)} could not be read: {ex.Message}");
            return null;
        }
    }

    private static bool TryInt(JsonNode? n, out int v)
    {
        // The server writes numbers as doubles ("ACCESS": 3.0 is as valid as 3).
        v = 0;
        if (n is not JsonValue jv || !jv.TryGetValue(out double d) || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue) return false;
        v = (int)d;
        return true;
    }

    private static bool Bool(JsonNode? n, bool fallback) => n is JsonValue jv && jv.TryGetValue(out bool b) ? b : fallback;

    private static string? Str(JsonNode? n) => n is JsonValue jv && jv.TryGetValue(out string? s) ? s : null;
}
