namespace PtcXaeMcp.WinForms.TwinCat;

internal enum EntryOp { Add, Set, Remove }

/// <param name="Op">add | set | remove.</param>
/// <param name="Name">The entry the operation addresses (for add: the new entry).</param>
/// <param name="Section">Section header ("VAR_INPUT", "VAR_GLOBAL PERSISTENT"); required when it is ambiguous.</param>
/// <param name="Type">Data type (not for enum values).</param>
/// <param name="Value">Enum value (only for enum values; "" removes an explicit value).</param>
/// <param name="InitialValue">Initial value (not for enum values; "" removes it).</param>
/// <param name="Address">AT address such as "%I*" (not for enum values; "" removes it).</param>
/// <param name="Comment">Trailing comment text ("" removes it).</param>
/// <param name="AfterName">add: insert after this entry instead of at the end of the section.</param>
/// <param name="NewName">set: rename the entry.</param>
/// <param name="Attributes">Attribute pragmas of the entry; null leaves them unchanged, an empty list removes them all.</param>
internal sealed record EntryEdit(
    EntryOp Op, string Name, string? Section = null, string? Type = null, string? Value = null,
    string? InitialValue = null, string? Address = null, string? Comment = null, string? AfterName = null,
    string? NewName = null, IReadOnlyList<string>? Attributes = null);

/// <summary>The edited text, or an "[Error] ..." when the edit was refused (the text is then untouched).</summary>
internal sealed record EntryEditResult(string? Text, string? Error, string Summary);

/// <summary>An entry as listed to the caller.</summary>
internal sealed record DeclEntryInfo(
    string Section, string Name, string? Type, string? Initial, string? Address, string? Comment,
    IReadOnlyList<string> Attributes, int Line, string? Problem);

// Pure text function: structured add / set / remove of the entries of a declaration.
// Every operation computes a list of span replacements against the ORIGINAL text and applies them back to front,
// so everything it does not mention stays byte-identical. Anything ambiguous is refused (R9): the read-back of the
// write is the last safeguard, not the first.
internal static class DeclarationEntryEditor
{
    public static (IReadOnlyList<DeclEntryInfo>? Entries, string? Error) List(
        string text, DeclarationShape shape, string? section)
    {
        var parsed = DeclarationParser.Parse(text, shape);
        if (parsed.Error is not null) return (null, parsed.Error);

        string? wanted = section is null ? null : NormalizeSection(section);
        var sections = parsed.Sections;
        if (wanted is not null)
        {
            sections = sections.Where(s => s.Header == wanted).ToList();
            if (sections.Count == 0)
                return (null, $"[Error] No section '{wanted}'. {Available(parsed)}");
        }
        var list = sections.SelectMany(s => s.Entries)
            .Select(e => new DeclEntryInfo(e.Section, e.Name, e.Type, e.Initial, e.Address, e.Comment, e.Attributes, e.Line, e.Problem))
            .ToList();
        return (list, null);
    }

    public static string NormalizeSection(string section) =>
        string.Join(' ', section.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string Available(ParsedDeclaration parsed) =>
        parsed.Sections.Count == 0
            ? "The declaration has no section."
            : "Sections: " + string.Join(", ", parsed.Sections.Select(s => s.Header).Distinct()) + ".";

    private readonly record struct Change(int Start, int End, string Replacement);

    public static EntryEditResult Apply(string text, DeclarationShape shape, EntryEdit edit)
    {
        var parsed = DeclarationParser.Parse(text, shape);
        if (parsed.Error is not null) return Fail(parsed.Error);
        if (string.IsNullOrWhiteSpace(edit.Name)) return Fail("[Error] name is required.");

        bool isEnum = shape == DeclarationShape.EnumBody;
        string? paramError = CheckParameters(edit, isEnum);
        if (paramError is not null) return Fail(paramError);

        return edit.Op switch
        {
            EntryOp.Add => Add(parsed, shape, edit, isEnum),
            EntryOp.Set => SetEntry(parsed, edit, isEnum),
            _ => Remove(parsed, edit, isEnum),
        };
    }

    private static EntryEditResult Fail(string error) => new(null, error, error);

    // ---- parameter validation -------------------------------------------------

    private static string? CheckParameters(EntryEdit e, bool isEnum)
    {
        string verb = e.Op.ToString().ToLowerInvariant();
        if (e.Op == EntryOp.Remove)
        {
            if (e.Type is not null || e.Value is not null || e.InitialValue is not null || e.Address is not null
                || e.Comment is not null || e.AfterName is not null || e.NewName is not null || e.Attributes is not null)
                return "[Error] op=remove takes only name (and section); the other parameters do not apply.";
            return null;
        }
        if (isEnum)
        {
            if (e.Type is not null) return "[Error] type does not apply to an enum value.";
            if (e.InitialValue is not null) return "[Error] initialValue does not apply to an enum value — use value.";
            if (e.Address is not null) return "[Error] address does not apply to an enum value.";
        }
        else if (e.Value is not null)
            return "[Error] value applies to enum values only — use initialValue for a variable or field.";

        if (e.Op == EntryOp.Add && e.NewName is not null) return "[Error] newName applies to op=set only.";
        if (e.Op == EntryOp.Set && e.AfterName is not null) return "[Error] afterName applies to op=add only.";

        if (e.Op == EntryOp.Add)
        {
            var nameError = IecIdentifier.Validate(e.Name, "entry name");
            if (nameError is not null) return nameError;
            if (!isEnum && string.IsNullOrWhiteSpace(e.Type)) return "[Error] type is required for op=add.";
        }
        if (e.NewName is not null)
        {
            var nameError = IecIdentifier.Validate(e.NewName, "newName");
            if (nameError is not null) return nameError;
        }
        if (e.Op == EntryOp.Set && e.Type is null && e.Value is null && e.InitialValue is null && e.Address is null
            && e.Comment is null && e.NewName is null && e.Attributes is null)
            return $"[Error] op={verb} changes nothing: pass at least one of type, initialValue, value, address, comment, newName, attributes.";

        foreach (var (label, fragment) in new[] { ("type", e.Type), ("value", e.Value), ("initialValue", e.InitialValue), ("address", e.Address) })
        {
            if (string.IsNullOrEmpty(fragment)) continue;
            var fragError = CheckFragment(label, fragment);
            if (fragError is not null) return fragError;
        }
        if (e.Comment is not null && (e.Comment.Contains('\n', StringComparison.Ordinal) || e.Comment.Contains('\r', StringComparison.Ordinal)))
            return "[Error] comment must be a single line.";
        if (e.Attributes is not null)
            foreach (string a in e.Attributes)
                if (NormalizeAttribute(a) is null)
                    return $"[Error] Invalid attribute '{a}'. Expected a name (hide), \"attribute 'hide'\" or a full pragma {{attribute 'hide'}}.";
        return null;
    }

    // A fragment (type, value) is spliced into the entry: it must not carry a ';' / ',' at top level, a line break, or
    // unbalanced brackets, or it would corrupt the entry boundaries.
    private static string? CheckFragment(string label, string fragment)
    {
        var (tokens, error) = DeclarationParser.Tokenize(fragment);
        if (tokens is null) return $"[Error] {label} '{fragment}' is not valid here: {error!["[Error] ".Length..]}";
        int depth = 0;
        foreach (var t in tokens)
        {
            if (t.Kind == DeclTokenKind.Newline) return $"[Error] {label} must be a single line.";
            if (t.Kind != DeclTokenKind.Symbol || t.End - t.Start != 1) continue;
            char ch = fragment[t.Start];
            if (ch is '(' or '[') depth++;
            else if (ch is ')' or ']') depth--;
            else if (depth == 0 && ch == ';') return $"[Error] {label} '{fragment}' contains a top-level ';'.";
            if (depth < 0) return $"[Error] {label} '{fragment}' has an unbalanced bracket.";
        }
        return depth == 0 ? null : $"[Error] {label} '{fragment}' has an unbalanced bracket.";
    }

    /// <summary>The pragma text for an attribute given as a name, as "attribute 'name'" or as a full {pragma}; null when invalid.</summary>
    internal static string? NormalizeAttribute(string input)
    {
        string a = input.Trim();
        if (a.Length == 0) return null;
        string pragma;
        if (a.StartsWith('{'))
        {
            if (!a.EndsWith('}')) return null;
            pragma = a;
        }
        else if (a.StartsWith("attribute", StringComparison.OrdinalIgnoreCase) && a.Length > 9 && char.IsWhiteSpace(a[9]))
            pragma = "{" + a + "}";
        else if (a.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            pragma = "{attribute '" + a + "'}";
        else
            return null;

        if (!pragma.AsSpan(1).TrimStart().StartsWith("attribute", StringComparison.OrdinalIgnoreCase)) return null;
        var (tokens, error) = DeclarationParser.Tokenize(pragma);
        if (tokens is null || error is not null) return null;
        return tokens.Count == 1 && tokens[0].Kind == DeclTokenKind.Pragma ? pragma : null;
    }

    // ---- locating -------------------------------------------------------------

    private static (DeclSection? Section, DeclEntry? Entry, string? Error) Locate(
        ParsedDeclaration parsed, string name, string? sectionRaw)
    {
        string? wanted = sectionRaw is null ? null : NormalizeSection(sectionRaw);
        var candidates = wanted is null ? parsed.Sections : parsed.Sections.Where(s => s.Header == wanted).ToList();
        if (wanted is not null && candidates.Count == 0)
            return (null, null, $"[Error] No section '{wanted}'. {Available(parsed)}");

        var hits = candidates
            .SelectMany(s => s.Entries.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).Select(e => (s, e)))
            .ToList();
        if (hits.Count == 0) return (null, null, $"[Error] No entry '{name}'{(wanted is null ? "" : $" in section '{wanted}'")}.");
        if (hits.Count > 1)
            return (null, null, $"[Error] Entry '{name}' occurs {hits.Count} times ({string.Join(", ", hits.Select(h => h.s.Header))}); pass section.");
        return (hits[0].s, hits[0].e, null);
    }

    private static string? RefuseIfUnsafe(DeclEntry e) =>
        e.Problem is null ? null : $"[Error] Entry '{e.Name}' (line {e.Line}) {e.Problem}; it cannot be edited here. Rewrite that part with twincat_plc_write.";

    // ---- add ------------------------------------------------------------------

    private static EntryEditResult Add(ParsedDeclaration parsed, DeclarationShape shape, EntryEdit edit, bool isEnum)
    {
        if (parsed.Sections.SelectMany(s => s.Entries).Any(e => string.Equals(e.Name, edit.Name, StringComparison.OrdinalIgnoreCase)))
            return Fail($"[Error] An entry named '{edit.Name}' already exists.");

        string nl = parsed.Newline;
        string? wanted = edit.Section is null ? null : NormalizeSection(edit.Section);
        var changes = new List<Change>();

        DeclSection? section;
        if (shape != DeclarationShape.VarSections)
        {
            section = parsed.Sections[0];
            if (wanted is not null && wanted != section.Header)
                return Fail($"[Error] This declaration has one section, '{section.Header}', not '{wanted}'.");
        }
        else if (wanted is null)
        {
            if (parsed.Sections.Count == 0)
                return Fail("[Error] The declaration has no section; pass section (e.g. VAR_INPUT) to create one.");
            if (parsed.Sections.Count > 1)
                return Fail($"[Error] Several sections exist; pass section. {Available(parsed)}");
            section = parsed.Sections[0];
        }
        else
        {
            var matches = parsed.Sections.Where(s => s.Header == wanted).ToList();
            if (matches.Count > 1) return Fail($"[Error] Section '{wanted}' occurs {matches.Count} times; the target is ambiguous.");
            section = matches.Count == 1 ? matches[0] : null;
        }

        // Where the new entry's indentation comes from: a sibling, else any entry, else four spaces.
        string indent = section?.Entries.Select(e => e.Indent).FirstOrDefault(i => i.Length > 0)
            ?? parsed.Sections.SelectMany(s => s.Entries).Select(e => e.Indent).FirstOrDefault(i => i.Length > 0)
            ?? "    ";

        if (section is null)
        {
            if (!DeclarationParser.IsValidSectionHeader(wanted!))
                return Fail($"[Error] '{wanted}' is not a section. Expected VAR, VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR_GLOBAL, VAR_TEMP, " +
                            "VAR_STAT or VAR_INST, optionally followed by CONSTANT, RETAIN or PERSISTENT.");
            if (edit.AfterName is not null) return Fail("[Error] afterName does not apply: the new section is empty.");
            // New section after the last one (or at the end of the text).
            string body = BuildEntryText(edit, indent, nl, isEnum, withComma: false);
            string block = wanted + nl + body + "END_VAR" + nl;
            // XAE returns a declaration without a trailing line break: the new section must not be glued to END_VAR.
            int at = parsed.Sections.Count > 0 ? parsed.Sections[^1].CloseLineEnd : parsed.Text.Length;
            string prefix = at > 0 && parsed.Text[at - 1] != '\n' && parsed.Text[at - 1] != '\r' ? nl : "";
            changes.Add(new Change(at, at, prefix + block));
            return Done(parsed, changes, $"Added entry '{edit.Name}' in new section '{wanted}'.",
                after => ExpectEntry(after, edit.Name, wanted, parsed.Sections.Sum(s => s.Entries.Count) + 1));
        }

        {
            // Position: after AfterName's line, or before the closing line.
            DeclEntry? after = null;
            if (edit.AfterName is not null)
            {
                after = section.Entries.FirstOrDefault(e => string.Equals(e.Name, edit.AfterName, StringComparison.OrdinalIgnoreCase));
                if (after is null) return Fail($"[Error] afterName '{edit.AfterName}' is not an entry of section '{section.Header}'.");
                var unsafeError = RefuseIfUnsafe(after);
                if (unsafeError is not null) return Fail(unsafeError);
            }
            var last = section.Entries.Count > 0 ? section.Entries[^1] : null;
            bool becomesLast = after is null || ReferenceEquals(after, last);

            int insertAt;
            string lead = "";
            if (after is not null) insertAt = after.LineEnd;
            else if (section.CloseSharesLine) { insertAt = section.CloseTokenStart; lead = nl; }
            else insertAt = section.CloseLineStart;

            if (isEnum && becomesLast && last is not null)
            {
                var lastUnsafe = RefuseIfUnsafe(last);
                if (lastUnsafe is not null) return Fail(lastUnsafe);
                // The value that was last gets a separating comma, right after its value (before any comment).
                if (last.TerminatorPos < 0) changes.Add(new Change(last.ContentEnd, last.ContentEnd, ","));
            }
            string entryText = BuildEntryText(edit, indent, nl, isEnum, withComma: isEnum && !becomesLast);
            changes.Add(new Change(insertAt, insertAt, lead + entryText));
            string header = section.Header;
            return Done(parsed, changes, $"Added entry '{edit.Name}' to section '{header}'.",
                after => ExpectEntry(after, edit.Name, header, parsed.Sections.Sum(s => s.Entries.Count) + 1));
        }
    }

    private static string BuildEntryText(EntryEdit e, string indent, string nl, bool isEnum, bool withComma)
    {
        var sb = new System.Text.StringBuilder();
        if (e.Attributes is not null)
            foreach (string a in e.Attributes) sb.Append(indent).Append(NormalizeAttribute(a)).Append(nl);
        sb.Append(indent).Append(e.Name);
        if (isEnum)
        {
            if (!string.IsNullOrEmpty(e.Value)) sb.Append(" := ").Append(e.Value);
            if (withComma) sb.Append(',');
        }
        else
        {
            if (!string.IsNullOrEmpty(e.Address)) sb.Append(" AT ").Append(e.Address);
            sb.Append(" : ").Append(e.Type);
            if (!string.IsNullOrEmpty(e.InitialValue)) sb.Append(" := ").Append(e.InitialValue);
            sb.Append(';');
        }
        if (e.Comment is not null && CommentText(e.Comment) is { Length: > 0 } comment) sb.Append(" // ").Append(comment);
        sb.Append(nl);
        return sb.ToString();
    }

    /// <summary>
    /// The text of a comment as the caller may pass it: with or without its markers. "// x", "(* x *)" and "/* x */"
    /// all mean "x" (list_entries reports the comment without markers, so the caller often echoes them back); the
    /// editor sets the markers itself. Without this a "// x" became "// // x".
    /// </summary>
    internal static string CommentText(string comment)
    {
        string c = comment.Trim();
        while (true)
        {
            if (c.StartsWith("//", StringComparison.Ordinal)) c = c[2..].Trim();
            else if (c.Length >= 4 && ((c.StartsWith("(*", StringComparison.Ordinal) && c.EndsWith("*)", StringComparison.Ordinal))
                                       || (c.StartsWith("/*", StringComparison.Ordinal) && c.EndsWith("*/", StringComparison.Ordinal))))
                c = c[2..^2].Trim();
            else return c;
        }
    }

    // ---- remove ---------------------------------------------------------------

    private static EntryEditResult Remove(ParsedDeclaration parsed, EntryEdit edit, bool isEnum)
    {
        var (section, entry, error) = Locate(parsed, edit.Name, edit.Section);
        if (error is not null) return Fail(error);
        var unsafeError = RefuseIfUnsafe(entry!);
        if (unsafeError is not null) return Fail(unsafeError);

        var changes = new List<Change> { new(entry!.LineStart, entry.LineEnd, "") };
        if (isEnum && entry.TerminatorPos < 0 && section!.Entries.Count > 1)
        {
            // The removed value was the last one: the one before it must not keep a trailing comma.
            var prev = section.Entries[^2];
            if (prev.TerminatorPos >= 0) changes.Add(new Change(prev.TerminatorPos, prev.TerminatorPos + 1, ""));
        }
        return Done(parsed, changes, $"Removed entry '{entry.Name}' from section '{section!.Header}'.",
            after => after.Sections.SelectMany(s => s.Entries).Any(e => string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
                ? $"entry '{entry.Name}' is still there"
                : after.Sections.Sum(s => s.Entries.Count) == parsed.Sections.Sum(s => s.Entries.Count) - 1
                    ? null : "the number of entries is not one less");
    }

    // ---- set ------------------------------------------------------------------

    private static EntryEditResult SetEntry(ParsedDeclaration parsed, EntryEdit edit, bool isEnum)
    {
        var (section, entry, error) = Locate(parsed, edit.Name, edit.Section);
        if (error is not null) return Fail(error);
        var unsafeError = RefuseIfUnsafe(entry!);
        if (unsafeError is not null) return Fail(unsafeError);
        string text = parsed.Text;
        string nl = parsed.Newline;
        var changes = new List<Change>();

        if (edit.NewName is not null && !string.Equals(edit.NewName, entry!.Name, StringComparison.Ordinal))
        {
            bool taken = parsed.Sections.SelectMany(s => s.Entries)
                .Any(e => !ReferenceEquals(e, entry) && string.Equals(e.Name, edit.NewName, StringComparison.OrdinalIgnoreCase));
            if (taken) return Fail($"[Error] An entry named '{edit.NewName}' already exists.");
            changes.Add(new Change(entry.NameStart, entry.NameEnd, edit.NewName));
        }

        if (edit.Type is not null)
        {
            if (string.IsNullOrWhiteSpace(edit.Type)) return Fail("[Error] type must not be empty.");
            changes.Add(new Change(entry!.TypeSpan!.Value.Start, entry.TypeSpan.Value.End, edit.Type.Trim()));
        }

        string? initEdit = isEnum ? edit.Value : edit.InitialValue;
        if (initEdit is not null)
        {
            // The text the initial value hangs off: the type (variable, field) or the name (enum value).
            int anchor = isEnum ? entry!.NameEnd : entry!.TypeSpan!.Value.End;
            if (initEdit.Length == 0)
            {
                if (entry.InitSpan is { } init) changes.Add(new Change(anchor, init.End, ""));
            }
            else if (entry.InitSpan is { } init) changes.Add(new Change(init.Start, init.End, initEdit.Trim()));
            else changes.Add(new Change(anchor, anchor, " := " + initEdit.Trim()));
        }

        if (edit.Address is not null)
        {
            if (edit.Address.Length == 0)
            {
                if (entry!.AddressSpan is { } a) changes.Add(new Change(PrecedingContentEnd(text, a.Start), a.End, ""));
            }
            else if (entry!.AddressSpan is { } a) changes.Add(new Change(a.Start, a.End, "AT " + edit.Address.Trim()));
            else changes.Add(new Change(entry.NameEnd, entry.NameEnd, " AT " + edit.Address.Trim()));
        }

        if (edit.Comment is not null)
        {
            string comment = CommentText(edit.Comment);
            if (comment.Length == 0)
            {
                if (entry!.CommentSpan is { } c) changes.Add(new Change(PrecedingContentEnd(text, c.Start), c.End, ""));
            }
            else if (entry!.CommentSpan is { } c)
            {
                bool block = text.AsSpan(c.Start, 2).SequenceEqual("(*") ;
                bool slashStar = text.AsSpan(c.Start, 2).SequenceEqual("/*");
                string replacement = block ? "(* " + comment + " *)"
                    : slashStar ? "/* " + comment + " */"
                    : "// " + comment;
                changes.Add(new Change(c.Start, c.End, replacement));
            }
            else
            {
                int at = entry!.TerminatorPos >= 0 ? entry.TerminatorPos + 1 : entry.ContentEnd;
                changes.Add(new Change(at, at, " // " + comment));
            }
        }

        if (edit.Attributes is not null)
        {
            foreach (var (s, e) in entry!.AttributeRemovals) changes.Add(new Change(s, e, ""));
            if (edit.Attributes.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (string a in edit.Attributes) sb.Append(entry.Indent).Append(NormalizeAttribute(a)).Append(nl);
                // Directly above the entry's first line, after the removals of the old attribute lines.
                int at = entry.LineStart;
                int firstRemoval = entry.AttributeRemovals.Count > 0 ? entry.AttributeRemovals.Min(r => r.Start) : at;
                changes.Add(new Change(Math.Min(at, firstRemoval), Math.Min(at, firstRemoval), sb.ToString()));
            }
        }

        string finalName = edit.NewName ?? entry!.Name;
        string sectionHeader = section!.Header;
        return Done(parsed, changes, $"Changed entry '{edit.Name}' in section '{sectionHeader}'.",
            after => ExpectEntry(after, finalName, sectionHeader, parsed.Sections.Sum(s => s.Entries.Count)));
    }

    // End of the last non-whitespace character before pos (so a deleted span takes its leading blanks with it).
    private static int PrecedingContentEnd(string text, int pos)
    {
        int p = pos;
        while (p > 0 && (text[p - 1] == ' ' || text[p - 1] == '\t')) p--;
        return p;
    }

    // ---- apply ----------------------------------------------------------------

    // The edited text must parse into the structure the edit promised: the entry is in the expected section and the
    // entry count is what it should be. A read-back of the write only proves the TEXT arrived; this proves it still
    // means what was asked (an inserted line glued to END_VAR would be written and read back faithfully).
    private static string? ExpectEntry(ParsedDeclaration after, string name, string sectionHeader, int expectedCount)
    {
        int count = after.Sections.Sum(s => s.Entries.Count);
        if (count != expectedCount) return $"expected {expectedCount} entries, found {count}";
        bool present = after.Sections.Where(s => s.Header == sectionHeader)
            .SelectMany(s => s.Entries).Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) && e.Problem is null);
        return present ? null : $"entry '{name}' is not in section '{sectionHeader}'";
    }

    private static EntryEditResult Done(ParsedDeclaration parsed, List<Change> changes, string summary,
        Func<ParsedDeclaration, string?> expect)
    {
        // Back to front, so earlier offsets stay valid. Two changes at the same offset keep their order of creation
        // (an insertion must not end up inside a replacement that starts at the same place).
        var ordered = changes
            .Select((c, index) => (c, index))
            .OrderByDescending(x => x.c.Start).ThenByDescending(x => x.c.End).ThenByDescending(x => x.index)
            .Select(x => x.c)
            .ToList();

        for (int i = 1; i < ordered.Count; i++)
            if (ordered[i].End > ordered[i - 1].Start)
                return Fail("[Error] The requested changes overlap in the source text; make them one at a time.");

        var sb = new System.Text.StringBuilder(parsed.Text);
        foreach (var c in ordered) sb.Remove(c.Start, c.End - c.Start).Insert(c.Start, c.Replacement);
        string result = sb.ToString();

        // The edited text must still parse into what the edit promised; otherwise nothing is written.
        var reparsed = DeclarationParser.Parse(result, parsed.Shape);
        if (reparsed.Error is not null) return Fail(reparsed.Error);
        string? mismatch = expect(reparsed);
        if (mismatch is not null)
            return Fail($"[Error] The edit would not produce the intended declaration ({mismatch}); nothing was written. " +
                        "Please report this together with the declaration text.");
        return new EntryEditResult(result, null, summary);
    }
}
