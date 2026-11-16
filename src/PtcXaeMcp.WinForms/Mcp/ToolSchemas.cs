using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// Leaner input schemas for tools/list. The SDK describes an optional parameter of a nullable type as
/// <c>"type": ["string", "null"]</c> with <c>"default": null</c>. A parameter that <c>required</c> does not name is
/// already optional, so neither says anything a client needs, and across all tools they cost thousands of tokens
/// in every tools/list. Only optional top-level properties change; required ones, other defaults and nested schemas
/// stay as generated. Arguments bind as before: an omitted parameter is still null.
/// </summary>
internal static class ToolSchemas
{
    /// <summary>Slims the schema of every tool once, before the server starts. Returns how many changed.</summary>
    public static int SlimAll(IEnumerable<McpServerTool>? tools)
    {
        if (tools is null) return 0;
        int changed = 0;
        foreach (var tool in tools)
        {
            if (Slim(tool.ProtocolTool.InputSchema) is not { } slim) continue;
            tool.ProtocolTool.InputSchema = slim;
            changed++;
        }
        return changed;
    }

    /// <summary>The slimmed schema, or null when nothing in <paramref name="schema"/> changes.</summary>
    internal static JsonElement? Slim(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root || root["properties"] is not JsonObject properties)
            return null;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (root["required"] is JsonArray names)
            foreach (var n in names)
                if (n is JsonValue v && v.TryGetValue<string>(out var s))
                    required.Add(s);

        bool changed = false;
        foreach (var (name, node) in properties)
        {
            if (required.Contains(name) || node is not JsonObject property) continue;

            if (property["type"] is JsonArray types && NonNullType(types) is { } single)
            {
                property["type"] = single;
                changed = true;
            }
            if (property.TryGetPropertyValue("default", out var defaultValue) && defaultValue is null)
            {
                property.Remove("default");
                changed = true;
            }
        }
        if (!changed) return null;

        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>The one type besides "null" in a two-entry type array, else null (kept as generated).</summary>
    private static string? NonNullType(JsonArray types)
    {
        if (types.Count != 2) return null;
        var strings = types.Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToList();
        if (strings.Any(s => s is null) || strings.Count(s => s == "null") != 1) return null;
        return strings.Single(s => s != "null");
    }
}
