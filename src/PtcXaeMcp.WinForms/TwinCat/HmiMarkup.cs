using System.Net;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-authoring: a tolerant reader for the markup of .view / .content / .usercontrol files. These files are
// HTML fragments made of nested <div data-tchmi-type="…"> controls; attribute values may be set by a
// <script data-tchmi-target-attribute="data-tchmi-x" type="application/json"> child. Only <div> elements are
// controls; every other tag is content of a control (e.g. HTML inside a TcHmiHtmlHost) and ignored.

/// <summary>One control (<c>&lt;div&gt;</c>) of an HMI page.</summary>
internal sealed class HmiElement
{
    public string? Id { get; init; }
    public string? Type { get; init; }

    /// <summary>All attributes, HTML-decoded, keyed case-insensitively.</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attribute values set by a <c>&lt;script data-tchmi-target-attribute=…&gt;</c> child (raw JSON).</summary>
    public Dictionary<string, string> ScriptAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>1-based line of the start tag.</summary>
    public int Line { get; init; }

    /// <summary>Index of the '&lt;' of the start tag.</summary>
    public int Start { get; init; }

    /// <summary>Index just after the matching <c>&lt;/div&gt;</c>; -1 when the element is not closed.</summary>
    public int End { get; set; } = -1;

    /// <summary>Index of the '&lt;' of the matching <c>&lt;/div&gt;</c>; -1 when not closed.</summary>
    public int CloseStart { get; set; } = -1;

    public HmiElement? Parent { get; init; }
    public List<HmiElement> Children { get; } = [];

    /// <summary>The attribute value, from the start tag or a target-attribute script.</summary>
    public string? Value(string attribute) =>
        Attributes.TryGetValue(attribute, out string? v) ? v
        : ScriptAttributes.TryGetValue(attribute, out string? s) ? s : null;

    public bool Has(string attribute) => Attributes.ContainsKey(attribute) || ScriptAttributes.ContainsKey(attribute);
}

internal sealed record HmiMarkupDocument(IReadOnlyList<HmiElement> Elements, HmiElement? Root, IReadOnlyList<string> Problems);

/// <summary>A binding expression <c>%k%body%/k%</c> (or a trigger's <c>"fn": "…"</c>) in an HMI file.</summary>
/// <param name="Kind">s (server symbol), i (internal symbol), pp (user-control parameter), ctrl, f (function),
/// l (localized text), ctx, tp (template parameter), tr (themed resource), t (timer), pkg (package).</param>
/// <param name="Target">What the expression points at: symbol name without <c>::</c> sub-path and <c>|</c>
/// options, function name without arguments, control id, parameter or key.</param>
internal sealed record HmiBinding(string Kind, string Expression, string Target, int Line);

internal static partial class HmiMarkup
{
    internal const string UserControlHostType = "TcHmi.Controls.System.TcHmiUserControlHost";
    internal const string TargetUserControlAttribute = "data-tchmi-target-user-control";
    internal const string TargetContentAttribute = "data-tchmi-target-content";

    [GeneratedRegex(@"<(?<close>/)?(?<tag>[A-Za-z][A-Za-z0-9-]*)(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)(?:\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+)))?", RegexOptions.CultureInvariant)]
    private static partial Regex Attr();

    // The symbol tags of the framework (TcHmi SymbolTag, framework 14.6: s i l pp tp f ctrl ctx tr t pkg).
    [GeneratedRegex(@"%(?<k>s|i|l|pp|tp|f|ctrl|ctx|tr|t|pkg)%(?<body>.*?)%/\k<k>%", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Expression();

    [GeneratedRegex(@"""fn""\s*:\s*""(?<fn>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex TriggerFunction();

    // ---- elements --------------------------------------------------------------

    public static HmiMarkupDocument Parse(string text)
    {
        var lines = LineStarts(text);
        var elements = new List<HmiElement>();
        var problems = new List<string>();
        var stack = new Stack<HmiElement>();
        HmiElement? root = null;

        int pos = 0;
        while (pos < text.Length)
        {
            int comment = text.IndexOf("<!--", pos, StringComparison.Ordinal);
            Match m = Tag().Match(text, pos);
            if (!m.Success) break;
            if (comment >= 0 && comment < m.Index)
            {
                int endComment = text.IndexOf("-->", comment + 4, StringComparison.Ordinal);
                pos = endComment < 0 ? text.Length : endComment + 3;
                continue;
            }
            pos = m.Index + m.Length;
            string tag = m.Groups["tag"].Value;
            bool closing = m.Groups["close"].Success;
            string attrs = m.Groups["attrs"].Value;

            if (tag.Equals("script", StringComparison.OrdinalIgnoreCase) && !closing)
            {
                int close = text.IndexOf("</script>", pos, StringComparison.OrdinalIgnoreCase);
                int contentEnd = close < 0 ? text.Length : close;
                var sa = Attributes(attrs);
                if (sa.TryGetValue("data-tchmi-target-attribute", out string? target) && stack.Count > 0)
                    stack.Peek().ScriptAttributes[target] = text[pos..contentEnd].Trim();
                if (close < 0) problems.Add($"line {LineOf(lines, m.Index)}: <script> without </script>");
                pos = close < 0 ? text.Length : close + "</script>".Length;
                continue;
            }
            if (!tag.Equals("div", StringComparison.OrdinalIgnoreCase)) continue;

            if (closing)
            {
                if (stack.Count == 0) { problems.Add($"line {LineOf(lines, m.Index)}: </div> without an open <div>"); continue; }
                HmiElement done = stack.Pop();
                done.CloseStart = m.Index;
                done.End = pos;
                continue;
            }

            var a = Attributes(attrs);
            var el = new HmiElement
            {
                Id = a.GetValueOrDefault("id"),
                Type = a.GetValueOrDefault("data-tchmi-type"),
                Line = LineOf(lines, m.Index),
                Start = m.Index,
                Parent = stack.Count > 0 ? stack.Peek() : null,
            };
            foreach (var (k, v) in a) el.Attributes[k] = v;
            elements.Add(el);
            el.Parent?.Children.Add(el);
            if (el.Parent is null)
            {
                if (root is null) root = el;
                else problems.Add($"line {el.Line}: a second top-level element ('{el.Id}') — an HMI page has one root control");
            }
            if (attrs.TrimEnd().EndsWith('/')) { el.End = pos; el.CloseStart = m.Index; }
            else stack.Push(el);
        }
        foreach (var open in stack) problems.Add($"line {open.Line}: <div id=\"{open.Id}\"> is never closed");
        return new HmiMarkupDocument(elements, root, problems);
    }

    private static Dictionary<string, string> Attributes(string attrs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Attr().Matches(attrs))
        {
            string name = a.Groups["name"].Value;
            if (!d.ContainsKey(name)) d[name] = WebUtility.HtmlDecode(a.Groups["v"].Success ? a.Groups["v"].Value : "");
        }
        return d;
    }

    // ---- bindings --------------------------------------------------------------

    /// <summary>All binding expressions of a file, nested ones included (<c>%f%Fn(%s%A%/s%)%/f%</c> yields both),
    /// plus the function names of trigger actions (<c>"fn": "…"</c>). HTML entities are decoded first.</summary>
    public static IReadOnlyList<HmiBinding> Bindings(string text)
    {
        var lines = LineStarts(text);
        var list = new List<HmiBinding>();
        Collect(text, 0, text.Length, lines, list);
        foreach (Match m in TriggerFunction().Matches(text))
        {
            string fn = WebUtility.HtmlDecode(m.Groups["fn"].Value).Trim();
            list.Add(new HmiBinding("f", m.Value, fn, LineOf(lines, m.Index)));
        }
        return list.OrderBy(b => b.Line).ThenBy(b => b.Kind, StringComparer.Ordinal).ToList();
    }

    private static void Collect(string text, int start, int end, int[] lines, List<HmiBinding> list)
    {
        int pos = start;
        while (pos < end)
        {
            Match m = Expression().Match(text, pos, end - pos);
            if (!m.Success) break;
            string kind = m.Groups["k"].Value;
            string body = WebUtility.HtmlDecode(m.Groups["body"].Value);
            list.Add(new HmiBinding(kind, WebUtility.HtmlDecode(m.Value), TargetOf(kind, body), LineOf(lines, m.Index)));
            Group g = m.Groups["body"];
            Collect(text, g.Index, g.Index + g.Length, lines, list);
            pos = m.Index + m.Length;
        }
    }

    /// <summary>The name an expression body points at (see <see cref="HmiBinding.Target"/>).</summary>
    internal static string TargetOf(string kind, string body)
    {
        string b = body.Trim();
        switch (kind)
        {
            case "f":
                int paren = b.IndexOf('(');
                return (paren < 0 ? b : b[..paren]).Trim();
            case "s" or "i":
                return Cut(b, "|", "::", "[").Trim();
            case "pp" or "ctrl" or "l" or "tp" or "ctx":
                return Cut(b, "|", "::").Trim();
            default:
                return b;
        }
    }

    private static string Cut(string s, params string[] separators)
    {
        int cut = s.Length;
        foreach (string sep in separators)
        {
            int i = s.IndexOf(sep, StringComparison.Ordinal);
            if (i >= 0 && i < cut) cut = i;
        }
        return s[..cut];
    }

    // ---- lines -----------------------------------------------------------------

    internal static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return [.. starts];
    }

    internal static int LineOf(int[] lineStarts, int index)
    {
        int i = Array.BinarySearch(lineStarts, index);
        return (i >= 0 ? i : ~i - 1) + 1;
    }

    /// <summary>The leading whitespace of the line that contains <paramref name="index"/>.</summary>
    internal static string IndentAt(string text, int index)
    {
        int lineStart = index <= 0 ? 0 : text.LastIndexOf('\n', Math.Min(index, text.Length) - 1) + 1;
        int i = lineStart;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return text[lineStart..i];
    }
}
