using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

// Checks around twincat_plc_rename that the XAE does not make. Live 2026-11-05: a METHOD renamed to the name of
// its own input was accepted without a word and only the next build failed ("A local variable named 'NewStep' is
// already defined"). And with the refactoring option off, the body keeps assigning the return value to the old name.
internal static class RenameChecks
{
    /// <summary>
    /// Where <paramref name="newName"/> is already a variable — of the element itself
    /// (<paramref name="ownDeclaration"/>) or, for a member, of its POU (<paramref name="parentDeclaration"/>) — or
    /// null. Case-insensitive, like IEC identifiers.
    /// </summary>
    public static string? Collision(string newName, string? ownDeclaration, string? parentDeclaration)
    {
        return Find(ownDeclaration, "its own declaration") ?? Find(parentDeclaration, "the declaration of its POU");

        string? Find(string? declaration, string where)
        {
            if (string.IsNullOrWhiteSpace(declaration)) return null;
            var parsed = DeclarationParser.Parse(declaration, DeclarationShape.VarSections);
            string[] lines = declaration.Split('\n');
            foreach (var section in parsed.Sections)
                foreach (var entry in section.Entries)
                    foreach (string name in NamesOf(entry, lines))
                        if (string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
                            return $"'{name}' is already a variable ({section.Header}, line {entry.Line}) in {where}";
            return null;
        }
    }

    // The parser keeps only the first name of "a, b : INT" (and flags the entry); the others come from its line.
    private static IEnumerable<string> NamesOf(DeclEntry entry, string[] lines)
    {
        yield return entry.Name;
        if (entry.Problem is null || entry.Line < 1 || entry.Line > lines.Length) yield break;
        string line = lines[entry.Line - 1];
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) yield break;
        foreach (string name in line[..colon].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            yield return name;
    }

    /// <summary>The lines of <paramref name="implementation"/> that still assign to <paramref name="oldName"/> ("Old := …"), 1-based.</summary>
    public static IReadOnlyList<int> AssignmentsTo(string? implementation, string oldName)
    {
        if (string.IsNullOrEmpty(implementation) || string.IsNullOrWhiteSpace(oldName)) return [];
        var pattern = new Regex($@"(?<![\w.]){Regex.Escape(oldName)}\s*:=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var lines = new List<int>();
        string[] all = implementation.Split('\n');
        for (int i = 0; i < all.Length; i++)
            if (pattern.IsMatch(all[i])) lines.Add(i + 1);
        return lines;
    }
}
