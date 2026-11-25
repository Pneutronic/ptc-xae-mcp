using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Pure selection + path-normalisation logic for the Q2 deterministic bind
/// (issue #57). The COM ROT walk itself (<see cref="RotDteEnumerator"/>) is
/// live-only and not covered here.
/// </summary>
public class RotDteResolverTests
{
    private static RotDteCandidate Cand(string? path) => new(path, new object());

    // ── SolutionPathMatch ──────────────────────────────────────────────────

    [Fact]
    public void Normalize_Blank_ReturnsNull()
    {
        Assert.Null(SolutionPathMatch.Normalize(null));
        Assert.Null(SolutionPathMatch.Normalize("   "));
    }

    [Fact]
    public void Normalize_ShortName_IsExpanded_SoTheLongPathTheXaeReportsMatches()
    {
        static string Expand(string p) => p.Replace(@"\SOMEUS~1\", @"\some.user\", StringComparison.OrdinalIgnoreCase);
        Assert.True(SolutionPathMatch.Equal(@"C:\Users\SOMEUS~1\AppData\Local\Temp\x\X.sln",
            @"C:\Users\some.user\AppData\Local\Temp\x\X.sln", Expand));
        Assert.False(SolutionPathMatch.Equal(@"C:\Users\SOMEUS~1\AppData\Local\Temp\x\X.sln",
            @"C:\Users\other\AppData\Local\Temp\x\X.sln", Expand));
        // A path without '~' is never sent to the expander.
        Assert.Equal(@"c:\repos\a.sln", SolutionPathMatch.Normalize(@"C:\Repos\A.sln", _ => throw new InvalidOperationException()));
        // An expander that fails keeps the short form instead of throwing.
        Assert.Equal(@"c:\users\someus~1\a.sln",
            SolutionPathMatch.Normalize(@"C:\Users\SOMEUS~1\A.sln", _ => throw new IOException("gone")));
    }

    [Fact]
    public void Normalize_CaseAndTrailingSlashAndQuotes_Canonicalised()
    {
        var a = SolutionPathMatch.Normalize(@"C:\Repos\SampleProject\SampleProject.sln");
        var b = SolutionPathMatch.Normalize("\"c:\\repos\\sampleproject\\SampleProject.sln\\\"");
        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Normalize_RelativeSegments_Collapsed()
    {
        var a = SolutionPathMatch.Normalize(@"C:\Repos\X\..\SampleProject\SampleProject.sln");
        var b = SolutionPathMatch.Normalize(@"C:\Repos\SampleProject\SampleProject.sln");
        Assert.Equal(b, a);
    }

    [Fact]
    public void Equal_BothBlank_False()
    {
        Assert.False(SolutionPathMatch.Equal(null, ""));
    }

    // ── RotDteResolver ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoExpectedPath_ReturnsNoExpected_NeverGuesses()
    {
        var r = RotDteResolver.Resolve(
            new[] { Cand(@"C:\a\a.sln"), Cand(@"C:\b\b.sln") }, expectedSolutionPath: null);
        Assert.Equal(RotDteOutcome.NoExpectedPath, r.Outcome);
        Assert.Null(r.Dte);
    }

    [Fact]
    public void Resolve_UniqueMatch_BindsToThatDte()
    {
        var target = new object();
        var candidates = new[]
        {
            new RotDteCandidate(@"C:\repos\Other\Other.sln", new object()),
            new RotDteCandidate(@"C:\repos\SampleProject\SampleProject.sln", target),
        };
        var r = RotDteResolver.Resolve(candidates, @"c:\repos\sampleproject\SampleProject.sln");
        Assert.Equal(RotDteOutcome.Unique, r.Outcome);
        Assert.Same(target, r.Dte);
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNone()
    {
        var r = RotDteResolver.Resolve(
            new[] { Cand(@"C:\a\a.sln"), Cand(@"C:\b\b.sln") },
            @"C:\repos\SampleProject\SampleProject.sln");
        Assert.Equal(RotDteOutcome.None, r.Outcome);
    }

    [Fact]
    public void Resolve_TwoInstancesSameSolution_Ambiguous()
    {
        var candidates = new[]
        {
            Cand(@"C:\repos\SampleProject\SampleProject.sln"),
            Cand(@"C:\repos\SampleProject\SampleProject.sln"),
        };
        var r = RotDteResolver.Resolve(candidates, @"C:\repos\SampleProject\SampleProject.sln");
        Assert.Equal(RotDteOutcome.Ambiguous, r.Outcome);
        Assert.Equal(2, r.MatchCount);
        Assert.Null(r.Dte);
    }

    [Fact]
    public void Resolve_CandidateWithNoSolution_DoesNotMatch()
    {
        var target = new object();
        var candidates = new[]
        {
            new RotDteCandidate(null, new object()),
            new RotDteCandidate("", new object()),
            new RotDteCandidate(@"C:\repos\SampleProject\SampleProject.sln", target),
        };
        var r = RotDteResolver.Resolve(candidates, @"C:\repos\SampleProject\SampleProject.sln");
        Assert.Equal(RotDteOutcome.Unique, r.Outcome);
        Assert.Same(target, r.Dte);
    }
}
