using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// The pattern language of the symbol lists in the access policy: a glob on the full symbol path, compared
/// case-insensitively. <c>*</c> stands for any run of characters (dots and index brackets included), <c>?</c> for one
/// character; everything else matches itself. <c>MAIN.stTest.*</c> covers the whole struct, <c>GVL_Param.r*</c> every
/// symbol of the GVL that starts with <c>r</c>.
/// </summary>
internal static class SymbolGlob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string pattern, string symbol)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        return Cache.GetOrAdd(pattern.Trim(), Compile).IsMatch(symbol);
    }

    private static Regex Compile(string pattern)
    {
        var text = new System.Text.StringBuilder("^");
        foreach (char c in pattern)
        {
            text.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        text.Append('$');
        return new Regex(text.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}
