using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// persistent-snapshot: (de)serialisation of the <c>ptc-persistent-snapshot/1</c> file —
/// a flat leaf map keyed by leaf path, with meta. Values round-trip exactly.
/// </summary>
internal static class SnapshotFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Serialize(SnapshotDocument doc)
    {
        var values = new JsonObject();
        foreach (var (path, leaf) in doc.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var o = new JsonObject
            {
                ["type"] = leaf.Type,
                ["value"] = JsonNode.Parse(leaf.Value.GetRawText()),
            };
            if (leaf.Name is not null) o["name"] = leaf.Name;
            if (leaf.Display is not null) o["display"] = leaf.Display;
            values[path] = o;
        }

        var m = doc.Meta;
        var root = new JsonObject
        {
            ["format"] = SnapshotDocument.FormatId,
            ["meta"] = new JsonObject
            {
                ["utc"] = m.Utc,
                ["amsNetId"] = m.AmsNetId,
                ["adsPort"] = m.AdsPort,
                ["solution"] = m.Solution,
                ["plcApp"] = m.PlcApp,
                ["roots"] = new JsonArray(m.Roots.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["leafCount"] = m.LeafCount,
                ["skipped"] = new JsonArray(m.Skipped.Select(s => (JsonNode?)new JsonObject
                {
                    ["path"] = s.Path,
                    ["reason"] = s.Reason,
                }).ToArray()),
            },
            ["values"] = values,
        };
        return root.ToJsonString(Indented);
    }

    public static bool TryParse(string json, out SnapshotDocument? doc, out string error)
    {
        doc = null;
        error = string.Empty;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("format", out var fmt) ||
                fmt.GetString() != SnapshotDocument.FormatId)
            {
                error = $"not a '{SnapshotDocument.FormatId}' file";
                return false;
            }

            var meta = root.GetProperty("meta");
            var roots = meta.TryGetProperty("roots", out var r)
                ? r.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : new List<string>();
            var skipped = meta.TryGetProperty("skipped", out var s)
                ? s.EnumerateArray().Select(e => new SnapshotSkip(
                    e.GetProperty("path").GetString() ?? "", e.GetProperty("reason").GetString() ?? "")).ToList()
                : new List<SnapshotSkip>();

            var values = new Dictionary<string, SnapshotLeaf>(StringComparer.Ordinal);
            foreach (var p in root.GetProperty("values").EnumerateObject())
            {
                var v = p.Value;
                values[p.Name] = new SnapshotLeaf(
                    v.GetProperty("type").GetString() ?? "",
                    v.GetProperty("value").Clone(),
                    v.TryGetProperty("name", out var n) ? n.GetString() : null,
                    v.TryGetProperty("display", out var d) ? d.GetString() : null);
            }

            doc = new SnapshotDocument(
                new SnapshotMeta(
                    meta.GetProperty("utc").GetString() ?? "",
                    meta.GetProperty("amsNetId").GetString() ?? "",
                    meta.GetProperty("adsPort").GetInt32(),
                    meta.TryGetProperty("solution", out var sol) ? sol.GetString() : null,
                    meta.TryGetProperty("plcApp", out var app) ? app.GetString() : null,
                    roots,
                    meta.TryGetProperty("leafCount", out var lc) ? lc.GetInt32() : values.Count,
                    skipped),
                values);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            error = $"snapshot file is malformed: {ex.Message}";
            return false;
        }
    }
}
