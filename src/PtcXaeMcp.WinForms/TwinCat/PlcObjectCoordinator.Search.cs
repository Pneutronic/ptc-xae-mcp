using System.Globalization;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One hit of twincat_plc_search. Section is "declaration" or "implementation".</summary>
internal sealed record PlcSearchMatch(
    string Path, string Kind, string? Subtype, string Section, int Line, int Column, string Revision, IReadOnlyList<string> Excerpt);

/// <summary>An element (or one of its sections) the search could not look into, and why.</summary>
internal sealed record PlcSearchSkip(string Path, string Reason);

/// <summary>The hits of one page; <see cref="Page"/> is null only for an [Error].</summary>
internal sealed record PlcSearchResult(
    string Summary, IReadOnlyList<PlcSearchMatch> Matches, Mcp.McpPageInfo? Page, IReadOnlyList<PlcSearchSkip> Skipped)
{
    public bool HasMore => Page?.HasMore ?? false;
    public int? NextOffset => Page?.NextOffset;

    internal static PlcSearchResult Error(string message) => new(message, [], null, []);
}

// twincat_plc_search / twincat_plc_replace_text. The Automation Interface has no search: every text element is read
// through the same accessors as twincat_plc_read (so unsaved editor changes are included and a hit's line is the line
// plc_read shows), and a replacement goes through the same verified write path as twincat_plc_write.
internal sealed partial class PlcObjectCoordinator
{
    /// <summary>Hits per page of twincat_plc_search: default 100, at most 1000.</summary>
    internal static readonly Mcp.McpLimit SearchPage = new(100, 1000);
    private const string Declaration = "declaration";
    private const string Implementation = "implementation";

    private static readonly string[] SearchableKinds = [PlcKind.Pou, PlcKind.Interface, PlcKind.Dut, PlcKind.Gvl, PlcKind.Member];

    // ---- search ----------------------------------------------------------------

    public PlcSearchResult Search(string pattern, bool regex = false, bool caseSensitive = false, bool wholeWord = false,
        string? scope = null, string? pathPrefix = null, string? kind = null, int contextLines = 1, int? limit = null, int offset = 0)
    {
        var (matcher, patternError) = PlcTextSearch.Compile(pattern, regex, caseSensitive, wholeWord);
        if (patternError is not null) return PlcSearchResult.Error("[Error] " + patternError);

        string normScope = string.IsNullOrWhiteSpace(scope) ? "both" : scope.Trim().ToLowerInvariant();
        if (normScope is not ("both" or Declaration or Implementation))
            return PlcSearchResult.Error($"[Error] Unknown scope '{scope}'. Expected declaration, implementation or both.");

        string? normKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!PlcKindResolver.TryParseKind(kind, out string k, out string? kindError)) return PlcSearchResult.Error(kindError!);
            if (!SearchableKinds.Contains(k))
                return PlcSearchResult.Error($"[Error] kind {k} has no text to search. Expected POU, INTERFACE, DUT, GVL or MEMBER.");
            normKind = k;
        }
        if (Mcp.McpPaging.Validate(limit, offset) is { } invalid) return PlcSearchResult.Error(invalid);
        if (contextLines < 0 || contextLines > 10) return PlcSearchResult.Error("[Error] contextLines must be between 0 and 10.");

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return PlcSearchResult.Error(error);

        string? prefix = string.IsNullOrWhiteSpace(pathPrefix) ? null : pathPrefix.Trim().TrimEnd('^');
        bool InScope(string path) => prefix is null
            || path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "^", StringComparison.OrdinalIgnoreCase);

        var matches = new List<PlcSearchMatch>();
        var skipped = new List<PlcSearchSkip>();
        int searched = 0;
        try
        {
            foreach (var (path, entry) in TextElements(prefix, InScope, skipped))
            {
                if (normKind is not null && entry.Kind != normKind) continue;

                PlcElementContent content;
                try { content = ReadContent(path, entry.Kind!, entry); }
                catch (Exception ex) when (!ComCall.IsBusy(ex)) { skipped.Add(new(path, $"unreadable: {ex.Message}")); continue; }
                searched++;

                if (normScope != Implementation && content.HasDeclaration)
                    Collect(path, entry, Declaration, content.Declaration, content.Revision);
                if (normScope != Declaration && content.HasImplementation)
                {
                    if (PlcImplLanguage.IsGraphical(content.Language))
                        skipped.Add(new(path, $"implementation in {content.Language} (XML) not searched"));
                    else
                        Collect(path, entry, Implementation, content.Implementation, content.Revision);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return PlcSearchResult.Error("[Error] The pattern took too long to match. Simplify the regular expression " +
                                         "(nested repetitions such as (a+)+ backtrack) or search literally (regex=false).");
        }
        catch (Exception ex) { return PlcSearchResult.Error(McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message)); }

        void Collect(string path, PlcCatalogEntry entry, string section, string text, string revision)
        {
            foreach (var hit in PlcTextSearch.Find(text, matcher!))
                matches.Add(new PlcSearchMatch(path, entry.Kind!, entry.Subtype, section, hit.Line, hit.Column, revision,
                    PlcTextSearch.Excerpt(text, hit.Line, hit.EndLine, contextLines)));
        }

        string skippedNote = skipped.Count == 0 ? "" : $", {skipped.Count} skipped";
        string searchedText = $"{searched} element{(searched == 1 ? "" : "s")} searched";
        var page = Mcp.McpPaging.Apply(matches, limit, offset, SearchPage);
        if (matches.Count == 0)
            return new($"No match for '{pattern}' in {searchedText}{(skipped.Count == 0 ? "" : $" ({skipped.Count} skipped)")}.",
                [], page, skipped);

        int elements = matches.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string head = $"Found {matches.Count} match{(matches.Count == 1 ? "" : "es")} in {elements} element{(elements == 1 ? "" : "s")} " +
                      $"({searchedText}{skippedNote}).";
        string shown = page.Returned == 0
            ? $" offset {offset} is past the last match."
            : string.Create(CultureInfo.InvariantCulture, $" Showing {page.Offset + 1}–{page.Offset + page.Returned}.");
        return new(head + shown, page.Items, page, skipped);
    }

    // Every element with text, in tree order, a POU's/INTERFACE's members right after it. Folders and the elements
    // whose content XAE does not hand out are not text elements and are passed over silently.
    private IEnumerable<(string Path, PlcCatalogEntry Entry)> TextElements(string? prefix, Func<string, bool> inScope, List<PlcSearchSkip> skipped)
    {
        foreach (var top in session.ListPlcElements())
        {
            var entry = CatalogOf(top.Path, top.ItemType, skipped);
            if (entry is null || !(entry.HasDeclaration || entry.HasImplementation)) continue;
            if (inScope(top.Path)) yield return (top.Path, entry);

            // Members live below their POU/INTERFACE; list them only when some of them can be in scope.
            if (entry.Kind is not (PlcKind.Pou or PlcKind.Interface)) continue;
            bool membersInScope = inScope(top.Path) || prefix!.StartsWith(top.Path + "^", StringComparison.OrdinalIgnoreCase);
            if (!membersInScope) continue;
            IReadOnlyList<PouMemberEntry> members;
            try { members = session.ListPouElements(top.Path); }
            catch (Exception ex) when (!ComCall.IsBusy(ex)) { skipped.Add(new(top.Path, $"members unreadable: {ex.Message}")); continue; }
            foreach (var member in members)
            {
                if (member.Kind == PlcKind.Folder || !inScope(member.Path)) continue;
                var memberEntry = CatalogOf(member.Path, member.ItemType, skipped);
                if (memberEntry is not null && (memberEntry.HasDeclaration || memberEntry.HasImplementation))
                    yield return (member.Path, memberEntry);
            }
        }
    }

    private PlcCatalogEntry? CatalogOf(string path, int? itemType, List<PlcSearchSkip> skipped)
    {
        if (itemType is null)
        {
            try { itemType = session.ProbePlcNode(path).ItemType; }
            catch (Exception ex) when (!ComCall.IsBusy(ex)) { skipped.Add(new(path, $"unreadable: {ex.Message}")); return null; }
        }
        if (itemType is not int it) { skipped.Add(new(path, "element type unreadable")); return null; }
        var entry = PlcElementCatalog.ForItemType(it);
        return entry is { Kind: not null } && Unsupported(entry.Kind, "read") is null ? entry : null;
    }

    // ---- replace_text ----------------------------------------------------------

    /// <param name="occurrence">1-based; null requires <paramref name="oldText"/> to occur exactly once.</param>
    public string ReplaceText(string path, string kind, string section, string oldText, string? newText,
        int? occurrence = null, string? expectedRevision = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return "[Error] path is required.";
        string normSection = (section ?? "").Trim().ToLowerInvariant();
        if (normSection is not (Declaration or Implementation)) return "[Error] section must be declaration or implementation.";
        if (string.IsNullOrEmpty(oldText)) return "[Error] oldText is required.";
        if (newText is null) return "[Error] newText is required (an empty string deletes oldText).";
        string? err = ParseKindAndSubtype(kind, null, "replace_text", out string k, out _);
        if (err is not null) return err;

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        // Everything up to the write runs BEFORE the logout: kind, section, revision and the replacement itself.
        var (probeError, target) = ProbeKind(path, k);
        if (probeError is not null) return probeError;
        var entry = target!.Value.Entry;
        if (entry.Content == PlcContent.Structure)
            return $"[Error] '{path}' is a FOLDER: it has no declaration or implementation.";
        bool isDecl = normSection == Declaration;
        if (isDecl && !entry.HasDeclaration)
            return $"[Error] '{path}' is {Describe(entry)}: it has no declaration section, only an implementation.";
        if (!isDecl && !entry.HasImplementation)
            return $"[Error] '{path}' is {Describe(entry)}: it has no implementation section.";

        PlcElementContent content;
        try { content = ReadContent(path, k, entry); }
        catch (Exception ex) { return McpErrors.EnsureError(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message); }
        var stale = RevisionRefusal(path, expectedRevision, content.Revision);
        if (stale is not null) return stale;
        if (!isDecl && PlcImplLanguage.IsGraphical(content.Language))
            return $"[Error] '{path}' is implemented in {content.Language}: its implementation is XAE's XML, not text to patch. " +
                   "Change it with twincat_plc_write. Nothing was changed.";

        string before = isDecl ? content.Declaration : content.Implementation;
        var replaced = PlcTextReplace.Apply(before, oldText, newText, occurrence);
        if (replaced.Error is not null) return $"[Error] {replaced.Error.TrimEnd('.')} (in the {normSection} of '{path}'). Nothing was changed.";
        string after = replaced.Text!;

        if (isDecl && k != PlcKind.Member)
        {
            var lintError = PlcDeclarationLint.CheckNoEmbeddedMemberHeader(after);
            if (lintError is not null) return lintError;
        }

        error = SessionGuards.EnsureOfflineForWrite(session);
        if (error is not null) return error;

        string? decl = isDecl ? after : null;
        string? impl = isDecl ? null : after;
        try
        {
            if (k == PlcKind.Member) session.WritePouMember(path, decl, impl);
            else session.WritePouBody(path, decl, impl);
        }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        PlcElementContent written;
        try { written = ReadContent(path, k, entry); }
        catch (Exception ex) { return McpErrors.EnsureError($"The write ran, but the read-back failed: {(ComCall.IsBusy(ex) ? ComCall.Describe(ex) : ex.Message)}"); }
        // The XAE may hand the text back with other line breaks than it was given; only the content counts.
        static string Lf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Lf(isDecl ? written.Declaration : written.Implementation) != Lf(after))
            return $"[Error] The write ran, but the read-back of the {normSection} of '{path}' differs from the replaced text. " +
                   "Read it with twincat_plc_read and check.";

        string lines = replaced.NewEndLine > replaced.StartLine
            ? string.Create(CultureInfo.InvariantCulture, $"lines {replaced.StartLine}–{replaced.NewEndLine}")
            : string.Create(CultureInfo.InvariantCulture, $"line {replaced.StartLine}");
        var excerpt = PlcTextSearch.Excerpt(after, replaced.StartLine, Math.Max(replaced.StartLine, replaced.NewEndLine), context: 2);
        return $"Replaced text in the {normSection} of {k} '{path}', {lines} (verified by read-back). Revision: {written.Revision}\n" +
               string.Join("\n", excerpt);
    }

    // The refusal for a write whose expected revision is not the element's current one; null when it matches or none was given.
    private static string? RevisionRefusal(string path, string? expectedRevision, string currentRevision)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision) || PlcRevision.Matches(expectedRevision, currentRevision)) return null;
        return $"[Error] '{path}' changed since it was read (expected revision {expectedRevision.Trim()}, now {currentRevision}) — " +
               "someone else wrote it in between. Nothing was changed. Read it again with twincat_plc_read, apply your edit " +
               "to that text and write with the new revision.";
    }
}
