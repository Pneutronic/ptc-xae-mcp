namespace PtcXaeMcp.WinForms.TwinCat;

// A minimal, deliberately narrow static check for one specific failure mode:
// write_pou_body accepted a POU/INTERFACE declaration with a METHOD/PROPERTY/
// ACTION header embedded as plain text. XAE writes that through without
// complaint (it is technically well-formed ST as far as the tree-item write
// path is concerned) and the POU/INTERFACE itself still compiles — the error
// (C0149, "not identical to object name" family) only surfaces later in a
// CONSUMING project that tries to implement the interface or call the type.
// METHOD/PROPERTY/ACTION are structural members with their own tree node
// (added via twincat_plc_create, kind=MEMBER) — never text inside the parent's own
// declaration. This is not a general ST linter; it does not parse the
// language, it only refuses the one shape that is unambiguously wrong here.
internal static class PlcDeclarationLint
{
    private static readonly string[] MemberHeaderKeywords = { "METHOD", "PROPERTY", "ACTION" };

    public static string? CheckNoEmbeddedMemberHeader(string? declaration)
    {
        if (string.IsNullOrEmpty(declaration)) return null;

        foreach (var rawLine in declaration.Split('\n'))
        {
            string line = rawLine.TrimStart();
            foreach (var kw in MemberHeaderKeywords)
            {
                if (!line.StartsWith(kw, StringComparison.OrdinalIgnoreCase)) continue;
                bool wordBoundary = line.Length == kw.Length || char.IsWhiteSpace(line[kw.Length]);
                if (!wordBoundary) continue;

                // A member header is "METHOD <name>…"; a variable declaration is
                // "Method : INT;". Both clear the word boundary above, so also
                // require an identifier to follow — otherwise a variable that
                // happens to be named Method/Property/Action is refused, which
                // a live test actually hit.
                if (!NextTokenStartsIdentifier(line, kw.Length)) continue;

                return $"[Error] declaration contains an embedded '{kw}' header line " +
                    $"('{line.TrimEnd()}'). {kw} is a structural member with its own tree node — " +
                    "add it via twincat_plc_create (kind=MEMBER), never as text inside the POU/INTERFACE's " +
                    "own declaration. Writing it here is accepted silently and only fails later, " +
                    "in a project that consumes this type (C0149-family compiler error).";
            }
        }
        return null;
    }

    // True when the first non-whitespace character after the keyword could start
    // an IEC identifier (letter or underscore) — i.e. "METHOD DoThing", not
    // "Method : INT;" or "Action := TRUE;".
    private static bool NextTokenStartsIdentifier(string line, int afterKeyword)
    {
        int i = afterKeyword;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i >= line.Length) return false;
        return char.IsLetter(line[i]) || line[i] == '_';
    }
}
