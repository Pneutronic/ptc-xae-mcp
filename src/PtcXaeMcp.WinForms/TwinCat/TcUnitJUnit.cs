using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

// twincat_tcunit_results(junitPath): the same results as a JUnit XML file a CI server reads directly
// (testsuites/testsuite/testcase with failure and skipped). Built from the tool's own JSON result, so the file and the
// answer can never disagree. Suite counts come from TcUnit's totals; a passed case appears as a testcase only when the
// result lists it (includePassed=true).
internal static class TcUnitJUnit
{
    internal static string FromResultJson(string resultJson)
    {
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        var totals = root.TryGetProperty("totals", out var t) ? t : default;

        var suites = new XElement("testsuites",
            new XAttribute("name", "TcUnit"),
            new XAttribute("tests", Int(totals, "tests") ?? 0),
            new XAttribute("failures", Int(totals, "failed") ?? 0),
            new XAttribute("time", Seconds(totals, "duration")));

        if (root.TryGetProperty("suites", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in list.EnumerateArray())
            {
                string suiteName = Str(s, "name") ?? "";
                var suite = new XElement("testsuite",
                    new XAttribute("name", suiteName),
                    new XAttribute("tests", Int(s, "tests") ?? 0),
                    new XAttribute("failures", Int(s, "failed") ?? 0),
                    new XAttribute("time", Seconds(s, "duration")));
                if (s.TryGetProperty("cases", out var cases) && cases.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cases.EnumerateArray())
                    {
                        var testCase = new XElement("testcase", new XAttribute("classname", suiteName), new XAttribute("name", Str(c, "name") ?? ""));
                        if (Bool(c, "skipped") == true) testCase.Add(new XElement("skipped"));
                        else if (Bool(c, "failed") == true)
                        {
                            string message = Str(c, "message") ?? "";
                            testCase.Add(new XElement("failure", new XAttribute("message", message), message));
                        }
                        suite.Add(testCase);
                    }
                }
                suites.Add(suite);
            }
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), suites).ToString(SaveOptions.None);
    }

    /// <summary>The result JSON with "junitFile" added (or the reason it could not be written).</summary>
    internal static string WithJUnitFile(string resultJson, string junitPath)
    {
        var node = JsonNode.Parse(resultJson)!.AsObject();
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(junitPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(junitPath, FromResultJson(resultJson));
            node["junitFile"] = junitPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            node["junitError"] = $"The JUnit file could not be written to '{junitPath}': {ex.Message}";
        }
        return node.ToJsonString();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long i) ? i : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string Seconds(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble().ToString("0.###", CultureInfo.InvariantCulture)
            : "0";
}
