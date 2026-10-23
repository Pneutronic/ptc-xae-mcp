using System.Text;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #149, scope 8: no catch clause in <c>TwinCatComSession*.cs</c> may swallow a busy/blocked
/// XAE. ~460 bare <c>catch { }</c> / <c>catch (Exception)</c> blocks there used to turn
/// RPC_E_SERVERCALL_RETRYLATER into "not found", "no solution" or silently truncated lists.
///
/// A catch clause is accepted when it
/// <list type="bullet">
/// <item>catches a specific non-COM exception type (FormatException, IOException, …), or</item>
/// <item>has an exception filter that consults <c>ComCall</c> (<c>when (ComCall.NotBusy(ex))</c> —
/// the duck-typing fallback still runs for a genuine "member not present"), or</item>
/// <item>rethrows (<c>throw;</c>, or <c>throw new …(…, ex)</c> keeping the cause as inner exception), or</item>
/// <item>carries an explicit <c>// com-allow: &lt;reason&gt;</c> marker on its line. The markers form the
/// allow-list; <see cref="AllowListed"/> pins it so every new entry is a reviewed test change.</item>
/// </list>
/// A plain lexer is enough here (comments and string literals are masked first); no Roslyn dependency.
/// </summary>
public class ComAccessArchitectureTests
{
    /// <summary>The explicit allow-list: file → number of <c>// com-allow:</c> catch clauses.</summary>
    private static readonly Dictionary<string, int> AllowListed = new()
    {
        // Thread entry points: the exception is captured and rethrown/reported on the caller thread;
        // a filter would let busy escape as an unhandled thread exception and kill the process.
        ["TwinCatComSession.Build.cs"] = 1,
        ["TwinCatComSession.EditorNavigation.cs"] = 1,
        // Repository listing: a failed COM read (busy timeout included) becomes a warning and the
        // filesystem fallback, never a silent result.
        ["TwinCatComSession.SbomLibraries.cs"] = 1,
    };

    private static readonly HashSet<string> GenericTypes = new(StringComparer.Ordinal)
    {
        "Exception", "System.Exception",
        "COMException", "System.Runtime.InteropServices.COMException",
        "InvalidOperationException", "System.InvalidOperationException",
        "RuntimeBinderException", "Microsoft.CSharp.RuntimeBinder.RuntimeBinderException",
        "MissingMemberException", "System.MissingMemberException",
        "TargetInvocationException", "System.Reflection.TargetInvocationException",
        "AggregateException",
    };

    [Fact]
    public void NoCatchInTwinCatComSessionSwallowsABusyXae()
    {
        var offenders = new List<string>();
        foreach (var file in SessionFiles())
            foreach (var c in CatchClause.Parse(File.ReadAllText(file)))
                if (Verdict(c) is null)
                    offenders.Add($"{Path.GetFileName(file)}:{c.Line}: {c.LineText.Trim()}");

        Assert.True(offenders.Count == 0,
            "Catch clauses that can swallow RPC_E_SERVERCALL_RETRYLATER / RPC_E_CALL_REJECTED " +
            "(add 'when (ComCall.NotBusy(ex))', use ComCall.ReadProp/Try, or justify with '// com-allow: <reason>'):\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void AllowListIsExplicit()
    {
        var actual = new Dictionary<string, int>();
        foreach (var file in SessionFiles())
        {
            int n = CatchClause.Parse(File.ReadAllText(file)).Count(c => Verdict(c) == "allow");
            if (n > 0) actual[Path.GetFileName(file)] = n;
        }
        Assert.Equal(AllowListed.OrderBy(kv => kv.Key), actual.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void ScannerSeesTheCatchClauses()
    {
        // Guard against a scanner that silently finds nothing (and so passes vacuously).
        int total = SessionFiles().Sum(f => CatchClause.Parse(File.ReadAllText(f)).Count);
        Assert.True(total > 300, $"only {total} catch clauses found — scanner or path broken");
    }

    [Theory]
    [InlineData("try { x(); } catch { }", false)]
    [InlineData("try { x(); } catch { return null; }", false)]
    [InlineData("try { x(); } catch (Exception) { }", false)]
    [InlineData("try { x(); } catch (COMException ex) { log(ex); }", false)]
    [InlineData("try { x(); } catch (Exception ex) when (ex is not null) { }", false)]
    [InlineData("try { x(); } catch (Exception ex) when (ComCall.NotBusy(ex)) { }", true)]
    [InlineData("try { x(); } catch (Exception ex) { throw new InvalidOperationException(\"m\", ex); }", true)]
    [InlineData("try { x(); } catch (Exception ex) { throw new InvalidOperationException($\"m {ex.Message}\"); }", false)]
    [InlineData("try { x(); } catch { throw; }", true)]
    [InlineData("try { x(); } catch (FormatException) { }", true)]
    [InlineData("try { x(); } catch { } // com-allow: reason", true)]
    [InlineData("var s = \"catch { }\"; // catch { }", null)]
    public void Verdict_Rules(string code, bool? accepted)
    {
        var clauses = CatchClause.Parse("class C { void M() { " + code + " } }");
        if (accepted is null) { Assert.Empty(clauses); return; }
        var c = Assert.Single(clauses);
        Assert.Equal(accepted, Verdict(c) is not null);
    }

    private static string? Verdict(CatchClause c)
    {
        if (c.Type is not null && !GenericTypes.Contains(c.Type)) return "specific";
        if (c.Filter is not null && (c.Filter.Contains("ComCall.") || c.Filter.Contains("IsBusy"))) return "filtered";
        if (c.LineText.Contains("// com-allow:")) return "allow";
        if (Regex.IsMatch(c.Body, @"\bthrow\s*;")) return "rethrow";
        if (c.Variable is not null &&
            Regex.IsMatch(c.Body, @"\bthrow\s+new\b[^;]*[(,]\s*" + Regex.Escape(c.Variable) + @"\s*\)\s*;"))
            return "wrap";
        return null;
    }

    private static IEnumerable<string> SessionFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "PtcXaeMcp.WinForms", "TwinCat")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var files = Directory.GetFiles(Path.Combine(dir!.FullName, "src", "PtcXaeMcp.WinForms", "TwinCat"), "TwinCatComSession*.cs");
        Assert.NotEmpty(files);
        return files.OrderBy(f => f, StringComparer.Ordinal);
    }

    private sealed record CatchClause(int Line, string LineText, string? Type, string? Variable, string? Filter, string Body)
    {
        public static List<CatchClause> Parse(string src)
        {
            string m = Mask(src);
            string[] lines = src.Split('\n');
            var result = new List<CatchClause>();
            foreach (Match mt in Regex.Matches(m, @"\bcatch\b"))
            {
                int i = SkipWs(m, mt.Index + mt.Length);
                string? type = null, variable = null, filter = null;
                if (m[i] == '(')
                {
                    int j = Match(m, i, '(', ')');
                    var parts = src.Substring(i + 1, j - i - 1).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    type = parts[0];
                    variable = parts.Length > 1 ? parts[1] : null;
                    i = SkipWs(m, j + 1);
                }
                if (string.CompareOrdinal(m, i, "when", 0, 4) == 0)
                {
                    int k = SkipWs(m, i + 4);
                    int j = Match(m, k, '(', ')');
                    filter = src.Substring(k, j - k + 1);
                    i = SkipWs(m, j + 1);
                }
                if (m[i] != '{') continue; // not a catch clause (e.g. identifier)
                int end = Match(m, i, '{', '}');
                int line = src.AsSpan(0, mt.Index).Count('\n') + 1;
                // Body from the masked text: string contents (e.g. "';'-separated") must not end a match.
                result.Add(new CatchClause(line, lines[line - 1], type, variable, filter, m.Substring(i + 1, end - i - 1)));
            }
            return result;
        }

        private static int SkipWs(string s, int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; return i; }

        private static int Match(string s, int i, char open, char close)
        {
            int depth = 0;
            for (int j = i; j < s.Length; j++)
            {
                if (s[j] == open) depth++;
                else if (s[j] == close && --depth == 0) return j;
            }
            throw new InvalidOperationException($"unbalanced '{open}' at {i}");
        }

        // Replaces comments and the contents of string/char literals with spaces (same length),
        // so braces or the word "catch" inside them are not mistaken for code.
        private static string Mask(string src)
        {
            var o = new StringBuilder(src);
            int n = src.Length, i = 0;
            void Blank(int from, int to) { for (int k = from; k < to && k < n; k++) if (o[k] != '\n') o[k] = ' '; }
            while (i < n)
            {
                if (i + 1 < n && src[i] == '/' && src[i + 1] == '/')
                {
                    int j = src.IndexOf('\n', i); if (j < 0) j = n;
                    Blank(i, j); i = j;
                }
                else if (i + 1 < n && src[i] == '/' && src[i + 1] == '*')
                {
                    int j = src.IndexOf("*/", i + 2, StringComparison.Ordinal); j = j < 0 ? n : j + 2;
                    Blank(i, j); i = j;
                }
                else if (src[i] == '"' || ((src[i] == '$' || src[i] == '@') && i + 1 < n && (src[i + 1] == '"' || src[i + 1] == '$' || src[i + 1] == '@')))
                {
                    int j = i; bool verbatim = false, interp = false;
                    while (src[j] == '$' || src[j] == '@') { if (src[j] == '@') verbatim = true; else interp = true; j++; }
                    j++; // opening quote
                    int holes = 0;
                    while (j < n)
                    {
                        char ch = src[j];
                        if (interp && ch == '{') { if (j + 1 < n && src[j + 1] == '{') { j += 2; continue; } holes++; }
                        else if (interp && ch == '}' && holes > 0) holes--;
                        else if (ch == '\\' && !verbatim) { j += 2; continue; }
                        else if (ch == '"' && holes > 0)
                        {
                            int k = j + 1; while (k < n && src[k] != '"') { if (src[k] == '\\') k++; k++; }
                            j = k + 1; continue;
                        }
                        else if (ch == '"')
                        {
                            if (verbatim && j + 1 < n && src[j + 1] == '"') { j += 2; continue; }
                            j++; break;
                        }
                        j++;
                    }
                    Blank(i + 1, j - 1); i = j;
                }
                else if (src[i] == '\'')
                {
                    int j = i + 1; j += src[j] == '\\' ? 2 : 1;
                    while (j < n && src[j] != '\'') j++;
                    Blank(i + 1, j); i = j + 1;
                }
                else i++;
            }
            return o.ToString();
        }
    }
}
