using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat.Schema;

/// <summary>
/// Grammar of <c>ioPoints[].symbol</c>: the full PLC instance path of an <c>AT %I*</c> / <c>AT %Q*</c>
/// variable, exactly as the process image names it. IEC identifiers separated by <c>.</c>; each
/// segment may be followed by one or more index groups <c>[&lt;int&gt;(,&lt;int&gt;)*]</c> with signed
/// integer literals, so <c>a[1]</c>, <c>a[0]</c>, <c>a[-1]</c>, <c>a[2,3]</c>, <c>a[2][3]</c> and
/// <c>a[2].b[1].c</c> all fit. Nothing else: no <c>^</c> (the tree separator), no blanks, no leading
/// <c>.</c>, no constant names or expressions as an index, no leading zeros. Pure: no tree access.
/// The process image lists an array element under its flat name (<c>aCyl[1].bIn</c>), and a
/// multi-dimensional index with no blank (<c>[2,1]</c>), so the symbol is compared and looked up as
/// written, never normalised.
/// </summary>
internal static class PlcSymbolPath
{
    private const string Identifier = "[A-Za-z_][A-Za-z0-9_]*";
    private const string Int = "(?:0|-?[1-9][0-9]*)";
    private const string IndexGroup = $@"\[{Int}(?:,{Int})*\]";
    private const string Segment = $"{Identifier}(?:{IndexGroup})*";

    private static readonly Regex Grammar = new(
        $@"\A{Segment}(?:\.{Segment})*\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>True when <paramref name="symbol"/> is a valid symbol path.</summary>
    internal static bool IsValid(string? symbol) => Check(symbol) is null;

    /// <summary>Null when <paramref name="symbol"/> is valid, else the reason (the message names what to write instead).</summary>
    internal static string? Check(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return "is empty";
        if (Grammar.IsMatch(symbol)) return null;

        if (symbol.Contains('^', StringComparison.Ordinal))
            return "must not contain '^' (the tree separator); write the PLC instance path with '.', e.g. MAIN.fbCyl.bIn";
        if (symbol.Any(char.IsWhiteSpace))
            return InsideBrackets(symbol)
                ? "has a blank inside an index; write [2,1] without blanks (the process image names it that way)"
                : "must not contain blanks";
        if (symbol[0] == '.') return "must not start with '.'";
        if (symbol[^1] == '.') return "must not end with '.'";
        if (symbol.Contains("..", StringComparison.Ordinal) && !symbol.Contains('[', StringComparison.Ordinal))
            return "has an empty segment ('..')";
        if (symbol.Contains("[]", StringComparison.Ordinal))
            return "has an empty index '[]'; write an integer such as [1]";
        if (Regex.IsMatch(symbol, @"\[[^\]]*\.\.", RegexOptions.CultureInvariant))
            return "has an index range '[a..b]'; name one element, e.g. [1]";
        if (Regex.IsMatch(symbol, @"\[-?0[0-9]", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(symbol, @",-?0[0-9]", RegexOptions.CultureInvariant))
            return "has an index with a leading zero; write [1], not [01]";
        if (Regex.IsMatch(symbol, @"\][A-Za-z_]", RegexOptions.CultureInvariant))
            return "needs a '.' after an index ']' (e.g. MAIN.aCyl[1].bIn)";
        if (symbol.Contains('[', StringComparison.Ordinal))
            return "has an index that is not an integer literal ([1], [0], [-1], [2,3]); " +
                   "constants and expressions are not resolved";
        return "is not a PLC instance path (identifiers separated by '.', e.g. MAIN.fbCyl.bIn or MAIN.aCyl[1].bIn)";
    }

    /// <summary>
    /// Tree-path spellings of a valid <paramref name="symbol"/> whose tail is a member of a STRUCT-typed
    /// <c>AT</c> variable. The process image lists the variable flat (<c>MAIN.fbX.stIn</c>) and its
    /// members as child nodes by their own name (<c>MAIN.fbX.stIn^bB</c>, nested <c>…stIn^stSub^bX</c>),
    /// so the symbol <c>MAIN.fbX.stIn.bB</c> is tried as <c>MAIN.fbX.stIn^bB</c>, then
    /// <c>MAIN.fbX^stIn^bB</c>, … (longest flat prefix first). Only used when the flat lookup misses;
    /// each spelling is an exact lookup. Empty for a single-segment symbol.
    /// </summary>
    internal static IReadOnlyList<string> StructMemberCandidates(string symbol)
    {
        // A valid symbol has no '.' inside an index (integer literals only), so a plain split
        // yields exactly its segments.
        string[] segments = symbol.Split('.');
        var result = new List<string>();
        for (int k = segments.Length - 1; k >= 1; k--)
            result.Add(string.Join('.', segments[..k]) + "^" + string.Join('^', segments[k..]));
        return result;
    }

    private static bool InsideBrackets(string symbol)
    {
        int depth = 0;
        foreach (char ch in symbol)
        {
            if (ch == '[') depth++;
            else if (ch == ']' && depth > 0) depth--;
            else if (char.IsWhiteSpace(ch) && depth > 0) return true;
        }
        return false;
    }
}
