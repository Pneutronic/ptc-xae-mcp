namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-authoring: twincat_hmi_check_bindings — does every reference in an HMI page point at something
// that exists? Pure over the snapshot, the component index and the symbol sources.

/// <summary>What the server symbols of a binding are checked against.</summary>
/// <param name="ServerSymbols">Mapped symbol names (and server-config symbols); null = unknown (the server
/// could not be asked), then %s% references are reported as unchecked.</param>
/// <param name="ServerSource">Where ServerSymbols came from, for the result.</param>
/// <param name="ServerDomains">Domain names of the server extensions (ADS, TcHmiSrv, …): a reference into a
/// domain (e.g. ADS.Config) is not a mapped symbol and is not checked.</param>
/// <param name="InternalSymbols">Client internal symbols (tchmiconfig.json symbols.internal).</param>
internal sealed record HmiSymbolSources(
    IReadOnlySet<string>? ServerSymbols,
    string ServerSource,
    IReadOnlySet<string> ServerDomains,
    IReadOnlySet<string> InternalSymbols);

/// <param name="Severity">error (fails at runtime), warning (likely wrong or unchecked), info.</param>
/// <param name="Count">How often the same finding occurs in the file; <paramref name="Lines"/> lists every line
/// when more than one.</param>
internal sealed record HmiBindingFinding(string File, int Line, string Severity, string Kind, string Reference, string Message,
    int Count = 1, IReadOnlyList<int>? Lines = null);

internal sealed record HmiBindingReport(
    int FilesChecked,
    int BindingsChecked,
    IReadOnlyDictionary<string, int> BindingsByKind,
    IReadOnlyList<HmiBindingFinding> Findings,
    string ServerSource);

internal static class HmiBindingCheck
{
    internal const string BeckhoffFunctionNamespace = "TcHmi.Functions.Beckhoff.";

    // Server symbols every TE2000 server has without a mapping (TcHmiSrv domain root symbols).
    private static readonly HashSet<string> ServerSystemSymbols = new(StringComparer.Ordinal)
    {
        "ListSymbols", "GetSchema", "GetDefinitions", "Login", "Logout", "ServerTime", "ServerTimeUTC",
        "GetCurrentUser", "Diagnostics", "ListDomains", "ListUserNames", "ListGroups", "ChangePassword",
    };

    private static readonly string[] SeverityOrder = ["error", "warning", "info"];

    public static HmiBindingReport Check(HmiProjectSnapshot snap, HmiComponentIndex index, HmiSymbolSources sources,
        IReadOnlyCollection<string>? onlyPaths = null)
    {
        var findings = new List<HmiBindingFinding>();
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var functions = new HashSet<string>(index.Functions.Select(f => f.FullName), StringComparer.Ordinal);
        var localization = HmiLocalization.Read(snap, index);
        var allIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // id → files
        var docs = new Dictionary<string, HmiMarkupDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in index.Pages)
        {
            var doc = HmiMarkup.Parse(snap.Files[page.Path]);
            docs[page.Path] = doc;
            foreach (var e in doc.Elements.Where(e => !string.IsNullOrEmpty(e.Id)))
                (allIds.TryGetValue(e.Id!, out var l) ? l : allIds[e.Id!] = []).Add(page.Path);
        }

        var pages = index.Pages
            .Where(p => onlyPaths is null || onlyPaths.Contains(p.Path, StringComparer.OrdinalIgnoreCase))
            .ToList();
        int bindings = 0;
        foreach (var page in pages)
        {
            string text = snap.Files[page.Path];
            var doc = docs[page.Path];
            void Add(int line, string sev, string kind, string reference, string msg) =>
                findings.Add(new HmiBindingFinding(page.Path, line, sev, kind, reference, msg));

            foreach (string p in doc.Problems) Add(0, "error", "markup", "", p);

            // duplicate ids inside one page: the framework refuses the second control at runtime
            foreach (var dup in doc.Elements.Where(e => !string.IsNullOrEmpty(e.Id)).GroupBy(e => e.Id!, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
                foreach (var e in dup.Skip(1))
                    Add(e.Line, "error", "id", dup.Key, $"control id '{dup.Key}' is used {dup.Count()} times in this file (first on line {dup.First().Line})");

            HmiUserControlInfo? ownUc = page.Kind == "usercontrol" ? index.UserControl(page.Path) : null;
            var localIds = new HashSet<string>(doc.Elements.Select(e => e.Id).OfType<string>(), StringComparer.Ordinal);

            foreach (var b in HmiMarkup.Bindings(text))
            {
                bindings++;
                byKind[b.Kind] = byKind.GetValueOrDefault(b.Kind) + 1;
                switch (b.Kind)
                {
                    case "s":
                        CheckServerSymbol(b, sources, Add);
                        break;
                    case "i":
                        if (!sources.InternalSymbols.Contains(b.Target))
                            Add(b.Line, "error", "i", b.Target, $"internal symbol '{b.Target}' is not defined in {HmiComponents.ConfigPath} (symbols.internal)" +
                                Near(b.Target, sources.InternalSymbols));
                        break;
                    case "pp":
                        if (ownUc is null)
                            Add(b.Line, "error", "pp", b.Target, "%pp% (user-control parameter) is only valid inside a .usercontrol file");
                        else if (!ownUc.Parameters.Any(p => p.PropertyName.Equals(b.Target, StringComparison.Ordinal)))
                            Add(b.Line, "error", "pp", b.Target, $"'{b.Target}' is not a parameter of this user control" +
                                Near(b.Target, ownUc.Parameters.Select(p => p.PropertyName)));
                        break;
                    case "ctrl":
                        if (localIds.Contains(b.Target)) break;
                        if (allIds.TryGetValue(b.Target, out var where))
                            Add(b.Line, "warning", "ctrl", b.Target,
                                $"control '{b.Target}' is not in this file but in {string.Join(", ", where.Distinct())} — only resolvable while that page is loaded");
                        else
                            Add(b.Line, "error", "ctrl", b.Target, $"no control with id '{b.Target}' in the project" + Near(b.Target, localIds));
                        break;
                    case "f":
                        if (functions.Contains(b.Target)) break;
                        if (b.Target.StartsWith(BeckhoffFunctionNamespace, StringComparison.Ordinal))
                            Add(b.Line, "info", "f", b.Target, "framework function — not checked offline");
                        else
                            Add(b.Line, "warning", "f", b.Target, "not a function of this project (no matching .function.json) — from a package?" +
                                Near(b.Target, functions));
                        break;
                    case "l":
                        foreach (var lang in localization.Languages)
                            if (!lang.Texts.ContainsKey(b.Target))
                                Add(b.Line, "error", "l", b.Target, $"localized text '{b.Target}' is missing in language '{lang.Locale}'");
                        break;
                }
            }

            foreach (var host in doc.Elements.Where(e => string.Equals(e.Type, HmiMarkup.UserControlHostType, StringComparison.Ordinal)))
                CheckHost(host, index, Add);
            foreach (var region in doc.Elements.Where(e => e.Value(HmiMarkup.TargetContentAttribute) is { Length: > 0 }))
            {
                string target = region.Value(HmiMarkup.TargetContentAttribute)!;
                if (target.Contains('%')) continue;      // bound target, checked as a binding
                string? t = HmiPaths.Normalize(target);
                if (t is null || !index.Pages.Any(p => p.Path.Equals(t, StringComparison.OrdinalIgnoreCase)))
                    Add(region.Line, "error", "content", target, $"region '{region.Id}' targets '{target}', which is not a page of the project");
            }
        }

        // the same unresolved reference on every row of a page is one finding with its lines
        var ordered = findings
            .GroupBy(f => (f.File, f.Severity, f.Kind, f.Reference, f.Message))
            .Select(g => g.Count() == 1 ? g.First()
                : g.First() with { Count = g.Count(), Lines = g.Select(x => x.Line).Distinct().Order().ToList() })
            .OrderBy(f => Array.IndexOf(SeverityOrder, f.Severity))
            .ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Line)
            .ToList();
        return new HmiBindingReport(pages.Count, bindings, byKind, ordered, sources.ServerSource);
    }

    private static void CheckServerSymbol(HmiBinding b, HmiSymbolSources sources, Action<int, string, string, string, string> add)
    {
        string name = b.Target;
        if (name.Length == 0) { add(b.Line, "error", "s", name, "empty server symbol"); return; }
        if (name.Contains('%')) { add(b.Line, "info", "s", name, "dynamic symbol name — not checked"); return; }
        if (ServerSystemSymbols.Contains(name)) return;
        int dot = name.IndexOf('.');
        if (dot > 0 && sources.ServerDomains.Contains(name[..dot]))
        {
            add(b.Line, "info", "s", name, $"symbol of the server domain '{name[..dot]}' — not checked");
            return;
        }
        if (sources.ServerSymbols is null)
        {
            add(b.Line, "warning", "s", name, "not checked: the mapped symbols could not be read (" + sources.ServerSource + ")");
            return;
        }
        if (sources.ServerSymbols.Contains(name)) return;
        string? caseOnly = sources.ServerSymbols.FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        add(b.Line, "error", "s", name, caseOnly is not null
            ? $"server symbol '{name}' is mapped as '{caseOnly}' — symbol names are case-sensitive"
            : $"server symbol '{name}' is not mapped" + Near(name, sources.ServerSymbols));
    }

    private static void CheckHost(HmiElement host, HmiComponentIndex index, Action<int, string, string, string, string> add)
    {
        string? target = host.Value(HmiMarkup.TargetUserControlAttribute);
        if (string.IsNullOrWhiteSpace(target))
        {
            add(host.Line, "error", "usercontrol", host.Id ?? "", $"user-control host '{host.Id}' has no {HmiMarkup.TargetUserControlAttribute}");
            return;
        }
        string? path = HmiPaths.Normalize(target);
        HmiUserControlInfo? uc = path is null ? null : index.UserControl(path);
        if (uc is null)
        {
            add(host.Line, "error", "usercontrol", target, $"host '{host.Id}' targets '{target}', which is not a user control of the project" +
                Near(target, index.UserControls.Select(u => u.Path)));
            return;
        }
        if (uc.Problem is not null)
        {
            add(host.Line, "warning", "usercontrol", target, $"parameters of '{target}' unknown: {uc.Problem}");
            return;
        }
        foreach (var p in uc.Parameters.Where(p => p.RequiredOnCompile && p.DefaultJson is null && !host.Has(p.Attribute)))
            add(host.Line, "error", "usercontrol", p.PropertyName, $"host '{host.Id}' does not set the required parameter '{p.PropertyName}' ({p.Attribute})");
        foreach (var p in uc.Parameters.Where(p => !p.Bindable))
            if (host.Value(p.Attribute) is { } v && v.Contains("%s%", StringComparison.Ordinal))
                add(host.Line, "warning", "usercontrol", p.PropertyName, $"parameter '{p.PropertyName}' is not bindable but gets a symbol binding");
    }

    /// <summary>" — did you mean 'X'?" for the closest candidate (small edit distance), else "".</summary>
    internal static string Near(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string c in candidates)
        {
            int d = Distance(name, c);
            if (d < bestDistance) { bestDistance = d; best = c; }
        }
        // Live 2026-11-18: with length/4 'McpScaffold.Axis' suggested 'McpScaffold.On' (4 edits) — noise. A typo is
        // one or two edits, a bit more in long names.
        int limit = Math.Max(2, name.Length / 8);
        return best is not null && bestDistance <= limit ? $" — did you mean '{best}'?" : "";
    }

    internal static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 8) return int.MaxValue;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
