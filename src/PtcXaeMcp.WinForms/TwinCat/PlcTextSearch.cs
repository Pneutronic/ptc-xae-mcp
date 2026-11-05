using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One match in a declaration or implementation text; lines and columns are 1-based.</summary>
internal sealed record PlcTextHit(int Line, int Column, int EndLine);

// twincat_plc_search: matching over the text of one element section. ST is case-insensitive, so the default is too.
// The whole section is matched at once (a regex may span lines); line and column come from the match index, with '\n'
// as the line break and a trailing '\r' belonging to its line, the same counting twincat_plc_read and
// twincat_plc_list_entries use.
internal static class PlcTextSearch
{
    internal const int MaxExcerptLineLength = 200;

    // A pathological pattern must not hold the dispatcher (and with it the XAE) for long.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The matcher for the search parameters, or the reason the pattern is unusable (without "[Error]").</summary>
    internal static (Regex? Matcher, string? Error) Compile(string? pattern, bool regex, bool caseSensitive, bool wholeWord)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return (null, "pattern is required.");
        string body = regex ? pattern : Regex.Escape(pattern);
        // An identifier character on either side means the hit is only part of a longer name.
        if (wholeWord) body = $@"(?<![A-Za-z0-9_])(?:{body})(?![A-Za-z0-9_])";
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        try { return (new Regex(body, options, MatchTimeout), null); }
        catch (ArgumentException ex) { return (null, $"pattern is not a valid regular expression: {ex.Message}"); }
    }

    /// <summary>Every non-empty match in <paramref name="text"/>. Throws <see cref="RegexMatchTimeoutException"/>.</summary>
    internal static IReadOnlyList<PlcTextHit> Find(string text, Regex matcher)
    {
        var hits = new List<PlcTextHit>();
        if (string.IsNullOrEmpty(text)) return hits;
        // '$' in multiline mode stops only before '\n'; a CRLF text would hide every line end from it.
        text = text.Replace("\r\n", "\n");
        int[] lineStarts = LineStarts(text);
        for (Match m = matcher.Match(text); m.Success; m = m.NextMatch())
        {
            if (m.Length == 0) continue;
            int line = LineOf(lineStarts, m.Index);
            int column = m.Index - lineStarts[line - 1] + 1;
            int endLine = LineOf(lineStarts, m.Index + m.Length - 1);
            hits.Add(new PlcTextHit(line, column, endLine));
        }
        return hits;
    }

    /// <summary>
    /// Lines <paramref name="firstLine"/>..<paramref name="lastLine"/> with <paramref name="context"/> lines around them,
    /// each as "&gt; L12: text" (a hit line) or "  L11: text". Long lines are cut.
    /// </summary>
    internal static IReadOnlyList<string> Excerpt(string text, int firstLine, int lastLine, int context)
    {
        string[] lines = SplitLines(text);
        int from = Math.Max(1, firstLine - context);
        int to = Math.Min(lines.Length, lastLine + context);
        var result = new List<string>();
        for (int n = from; n <= to; n++)
        {
            string line = lines[n - 1];
            if (line.Length > MaxExcerptLineLength) line = line[..MaxExcerptLineLength] + "…";
            string mark = n >= firstLine && n <= lastLine ? "> " : "  ";
            result.Add(string.Create(CultureInfo.InvariantCulture, $"{mark}L{n}: {line}"));
        }
        return result;
    }

    internal static string[] SplitLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

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
        return i >= 0 ? i + 1 : ~i;
    }
}

/// <summary>The outcome of <see cref="PlcTextReplace.Apply"/>: the new text and the lines it touched, or the refusal.</summary>
internal sealed record PlcReplaceResult(string? Text, string? Error, int StartLine = 0, int OldEndLine = 0, int NewEndLine = 0);

// twincat_plc_replace_text: replace one exact piece of a section. Exact means case and whitespace as read — a
// replacement that does not hit exactly one place is refused with where the text was nearly found, so the caller can
// correct oldText instead of guessing. Line breaks are the only thing adapted: oldText/newText written with LF match
// and keep a CRLF text (the XAE hands out CRLF).
internal static class PlcTextReplace
{
    internal static PlcReplaceResult Apply(string text, string oldText, string newText, int? occurrence = null)
    {
        if (string.IsNullOrEmpty(oldText)) return new(null, "oldText is required.");

        string oldLf = oldText.Replace("\r\n", "\n");
        string newLf = newText.Replace("\r\n", "\n");
        // Try the text's own line break first; a text with mixed breaks may still hold the other form.
        var candidates = text.Contains("\r\n", StringComparison.Ordinal)
            ? new[] { (oldLf.Replace("\n", "\r\n"), newLf.Replace("\n", "\r\n")), (oldLf, newLf) }
            : new[] { (oldLf, newLf) };

        foreach (var (find, replace) in candidates.Distinct())
        {
            var positions = Occurrences(text, find);
            if (positions.Count == 0) continue;

            int[] lineStarts = PlcTextSearch.LineStarts(text);
            if (occurrence is null && positions.Count > 1)
            {
                string lines = string.Join(", ", positions.Select(p => PlcTextSearch.LineOf(lineStarts, p)));
                return new(null, $"oldText occurs {positions.Count} times (lines {lines}) — include surrounding text to make it " +
                                 $"unique, or pass occurrence (1–{positions.Count}).");
            }
            int n = occurrence ?? 1;
            if (n < 1 || n > positions.Count)
                return new(null, $"occurrence {n} is out of range: oldText occurs {positions.Count} time{(positions.Count == 1 ? "" : "s")}.");

            int at = positions[n - 1];
            int start = PlcTextSearch.LineOf(lineStarts, at);
            return new(
                string.Concat(text.AsSpan(0, at), replace, text.AsSpan(at + find.Length)),
                null,
                start,
                start + LineBreaks(find),
                start + LineBreaks(replace));
        }

        return new(null, NotFound(text, oldLf));
    }

    private static List<int> Occurrences(string text, string find)
    {
        var result = new List<int>();
        for (int i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + find.Length, StringComparison.Ordinal))
            result.Add(i);
        return result;
    }

    // Line breaks inside the piece; a break at its very end ends the last line instead of starting another.
    private static int LineBreaks(string s)
    {
        int count = s.Count(c => c == '\n');
        return s.EndsWith('\n') ? count - 1 : count;
    }

    private static string NotFound(string text, string oldLf)
    {
        const string plain = "oldText does not occur in the text";
        string[] lines = PlcTextSearch.SplitLines(text);

        string squeezedOld = Squeeze(oldLf);
        if (squeezedOld.Length > 0)
        {
            // Compare with all whitespace removed, then map the hit back to a line of the original text.
            var map = new List<int>();
            var squeezedText = new StringBuilder();
            for (int n = 0; n < lines.Length; n++)
                foreach (char c in lines[n])
                    if (!char.IsWhiteSpace(c)) { squeezedText.Append(c); map.Add(n + 1); }
            int at = squeezedText.ToString().IndexOf(squeezedOld, StringComparison.Ordinal);
            if (at >= 0)
                return $"{plain}; it matches when whitespace is ignored, at line {map[at]} — copy it exactly as twincat_plc_read " +
                       "returns it (spaces, tabs, line breaks).";
        }

        string? first = oldLf.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is not null && oldLf.Contains('\n', StringComparison.Ordinal))
        {
            int line = Array.FindIndex(lines, l => l.Contains(first, StringComparison.Ordinal));
            if (line >= 0) return $"{plain}; its first line is at line {line + 1}, a later line differs.";
        }
        return plain + ".";
    }

    private static string Squeeze(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
