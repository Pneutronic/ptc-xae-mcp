using System.Text.Json;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>What one static-analysis run produced: whether SARIF was written, why not, and the node that ran it.</summary>
internal sealed record StaticAnalysisRun(bool SarifExported, string? SarifNote, string Node);

/// <summary>One finding; Level is error, warning or note. File is relative to the solution directory when it lies below it.</summary>
internal sealed record StaticAnalysisFinding(string RuleId, string Level, string Message, string? File, int? Line, int? Column);

// The static-analysis results, from SARIF 2.1.0 (ITcPlcIECProject7.ExportToSarif, TwinCAT >= 3.1.4026.20) or, when the
// XAE cannot export it, from the Error List entries whose text starts with an SA rule number.
internal static class SarifReader
{
    private static readonly Regex SaEntry = new(@"^\s*(?<rule>SA\d{4})\s*:?\s*(?<text>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    internal static IReadOnlyList<StaticAnalysisFinding> Parse(string json, string? solutionDirectory)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException($"The SARIF file cannot be read: {ex.Message}", ex); }

        using (doc)
        {
            var findings = new List<StaticAnalysisFinding>();
            if (!doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return findings;
            foreach (var run in runs.EnumerateArray())
            {
                if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;
                foreach (var r in results.EnumerateArray())
                {
                    string rule = Str(r, "ruleId") ?? "";
                    // SARIF: a result without a level is a warning (§3.27.10).
                    string level = NormaliseLevel(Str(r, "level") ?? "warning");
                    string message = r.TryGetProperty("message", out var m) ? Str(m, "text") ?? "" : "";
                    string? file = null;
                    int? line = null, column = null;
                    if (r.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array && locs.GetArrayLength() > 0
                        && locs[0].TryGetProperty("physicalLocation", out var phys))
                    {
                        if (phys.TryGetProperty("artifactLocation", out var art)) file = Relative(Str(art, "uri"), solutionDirectory);
                        if (phys.TryGetProperty("region", out var region))
                        {
                            line = Int(region, "startLine");
                            column = Int(region, "startColumn");
                        }
                    }
                    findings.Add(new StaticAnalysisFinding(rule, level, message, file, line, column));
                }
            }
            return findings;
        }
    }

    internal static IReadOnlyList<StaticAnalysisFinding> FromErrorList(IEnumerable<BuildError> entries, string? solutionDirectory) =>
        entries.Select(e => (Entry: e, Match: SaEntry.Match(e.Description)))
            .Where(x => x.Match.Success)
            .Select(x => new StaticAnalysisFinding(x.Match.Groups["rule"].Value, NormaliseLevel(x.Entry.Level),
                x.Match.Groups["text"].Value.Trim(), Relative(x.Entry.File, solutionDirectory), x.Entry.Line, null))
            .ToList();

    // SARIF levels are error/warning/note/none; the Error List says Error/Warning/Message.
    internal static string NormaliseLevel(string level) =>
        level.Trim().ToLowerInvariant() switch
        {
            var l when l.Contains("error", StringComparison.Ordinal) => "error",
            var l when l.Contains("warn", StringComparison.Ordinal) => "warning",
            _ => "note",
        };

    private static string? Relative(string? location, string? solutionDirectory)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        string path = location;
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
        if (solutionDirectory is not null && Path.IsPathFullyQualified(path))
        {
            string rel = Path.GetRelativePath(solutionDirectory, path);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathFullyQualified(rel)) return rel;
        }
        return path;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
}
