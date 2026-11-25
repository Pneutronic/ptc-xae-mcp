using System.IO;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Normalises <c>.sln</c> paths so two references to the same solution compare
/// equal regardless of casing, quoting, relative segments, 8.3 short names or a
/// trailing separator. Both the deterministic-bind core
/// (<see cref="RotDteResolver"/>) and the session's expected-solution tracking
/// route through here so they always agree on equality
/// (spec risk: path-equality fragility).
/// </summary>
internal static class SolutionPathMatch
{
    /// <summary>Canonical form, or <c>null</c> for a blank input.</summary>
    /// <param name="toLongPath">Expands 8.3 short names of an existing path (default <see cref="Win32LongPath.Resolve"/>).
    /// Needed live: a path under %TEMP% is passed as <c>C:\Users\SOMEUS~1\…</c>, while the XAE reports the solution as
    /// <c>C:\Users\some.user\…</c>; without it the two never matched.</param>
    public static string? Normalize(string? path, Func<string, string>? toLongPath = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path.Trim().Trim('"');
        try { p = Path.GetFullPath(p); } catch { /* keep best-effort literal */ }
        if (p.Contains('~', StringComparison.Ordinal))
        {
            try { p = (toLongPath ?? Win32LongPath.Resolve)(p); } catch { /* keep the short form */ }
        }
        p = p.TrimEnd('\\', '/');
        return p.ToLowerInvariant();
    }

    /// <summary>True only when both normalise to the same non-null path.</summary>
    public static bool Equal(string? a, string? b, Func<string, string>? toLongPath = null)
    {
        string? na = Normalize(a, toLongPath);
        string? nb = Normalize(b, toLongPath);
        return na is not null && na == nb;
    }
}
