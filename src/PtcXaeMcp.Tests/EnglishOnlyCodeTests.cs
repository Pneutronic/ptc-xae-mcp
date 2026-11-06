using System.Text.RegularExpressions;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #157 item 2: code, logs, exceptions, tool results and GUI text must be
/// English. German survives only where it is not prose Claude/us wrote, but text
/// that has to match something the XAE itself produces — a dialog title/button,
/// a VS command-bar id, a tree/variable name TwinCAT itself labels in German, or
/// (one file) a single example byte value in a doc comment. Everywhere else, a
/// German umlaut/ß in <c>src/PtcXaeMcp.WinForms/**/*.cs</c> is a regression.
/// <para>
/// This scans every <c>.cs</c> file under <c>src/PtcXaeMcp.WinForms</c> for
/// <c>[äöüÄÖÜß]</c> outside line/block comments, except the files named in
/// <see cref="AllowListedFiles"/> below — each with its own justification, so a
/// new allow-list entry cannot be added silently. <see cref="Allow_ListedFiles_StillExist"/>
/// guards the list itself against drift (a renamed/deleted file leaving a stale entry).
/// </para>
/// <para>
/// Comment-stripping here is a best-effort text transform (line/block comments
/// removed by regex, not a real C# tokenizer) — good enough for this repo's
/// actual comment style, mirroring the same trade-off <c>McpToolResultContractTests</c>
/// makes when it parses tool bodies out of source text instead of a full parser.
/// </para>
/// </summary>
public sealed class EnglishOnlyCodeTests
{
    private static readonly Regex GermanCharRegex = new("[äöüÄÖÜß]", RegexOptions.Compiled);

    private static readonly Regex BlockCommentRegex =
        new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex LineCommentRegex =
        new(@"//.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Files allowed to contain German, each with why. Paths are relative to
    /// <c>src/PtcXaeMcp.WinForms</c>, forward-slash separated.
    /// </summary>
    private static readonly Dictionary<string, string> AllowListedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TwinCat/Win32DialogAutomation.cs"] =
            "matches XAE's own modal dialog titles/buttons (German UI) to answer them.",
        ["TwinCat/GoToDialogAutomation.cs"] =
            "matches the VS 'Go To Line' dialog's own (German-locale) caption/controls.",
        ["TwinCat/XaeDialogs.cs"] =
            "matches XAE's own dialog titles/button text for detection/dismissal.",
        ["TwinCat/XaeDialogClassifier.cs"] =
            "classifies XAE's own German dialog texts and button captions ('löschen', 'außerhalb', 'überschreiben').",
        ["TwinCat/ErrorListClearCommand.cs"] =
            "the VS command-bar command id string ('OtherContextMenus.ErrorList.Löschen') is XAE's own name for the command, not translatable prose.",
        ["TwinCat/BuildTypes.cs"] =
            "holds XAE's own login-modal text ('Applikation … seit dem letzten Download geändert') used for dialog matching.",
        ["TwinCat/SegmentTreeWalker.cs"] =
            "matches TwinCAT's own German-labeled tree node names (e.g. 'Geräte').",
        ["TwinCat/Schema/LinkCandidates.cs"] =
            "matches TwinCAT's own German-labeled task/variable node names (e.g. 'Eingänge'/'Ausgänge').",
        ["NativeTwinCatRunner.cs"] =
            "matches TwinCAT's own German-labeled I/O group names (e.g. 'Eingänge'/'Ausgänge') read back from the project.",
        ["TwinCat/PlcStringCodec.cs"] =
            "a doc-comment example of a single German umlaut's byte encoding (0xF6 for 'ö'), not UI/log prose.",
        ["Mcp/TwinCatMcpTools.Build.cs"] =
            "a tool [Description] quotes XAE's own login-modal text verbatim so the parameter's effect is unambiguous.",
        ["Mcp/TwinCatMcpTools.IbnLive.cs"] =
            "a tool [Description] quotes XAE's own dialog/step text verbatim for the same reason.",
    };

    [Fact]
    public void NoGermanCharacters_OutsideComments_InNonAllowListedWinFormsSource()
    {
        var root = FindWinFormsSourceDirectory();
        var failures = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (AllowListedFiles.ContainsKey(relative))
                continue;

            var text = File.ReadAllText(file);
            var withoutComments = LineCommentRegex.Replace(BlockCommentRegex.Replace(text, ""), "");

            var matches = GermanCharRegex.Matches(withoutComments);
            if (matches.Count > 0)
            {
                // Show a little context for the first offending match so a failure
                // is actionable without opening the file.
                int idx = matches[0].Index;
                int start = Math.Max(0, idx - 20);
                int len = Math.Min(40, withoutComments.Length - start);
                failures.Add($"{relative}: {matches.Count} German character(s), e.g. near " +
                             $"\"{withoutComments.Substring(start, len).ReplaceLineEndings(" ")}\"");
            }
        }

        Assert.True(failures.Count == 0,
            "Non-English (German) text found outside comments in non-allow-listed WinForms source:\n" +
            string.Join("\n", failures) +
            "\n\nEither translate it, or add it to EnglishOnlyCodeTests.AllowListedFiles with a reason.");
    }

    [Fact]
    public void AllowListedFiles_StillExist()
    {
        var root = FindWinFormsSourceDirectory();
        var missing = AllowListedFiles.Keys
            .Where(relative => !File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        Assert.True(missing.Count == 0,
            "Allow-list entries reference files that no longer exist: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to find
    /// <c>src/PtcXaeMcp.WinForms</c>, so the test works regardless of repo root
    /// name or worktree location (same approach as McpToolResultContractTests).
    /// </summary>
    private static string FindWinFormsSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PtcXaeMcp.WinForms");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Program.cs")))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/PtcXaeMcp.WinForms by walking up from " +
            AppContext.BaseDirectory + " — repo layout changed?");
    }
}
