using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// Arguments the server itself reads from a call (in a call-tool filter) and no tool method declares. They are added
/// to the input schema of the tools they apply to, so a client lists them and the unknown-argument check lets them pass.
/// </summary>
internal static class ReservedArguments
{
    internal const string TokenDescription =
        "Only after a [Confirm] answer: the token a human released in the ptc-xae-mcp window. Leave out on the first call.";

    internal const string KeyDescription =
        "Optional UUID. The same key with the same arguments never runs twice: a repeat gets the stored result " +
        "(twincat_receipt_get shows it). Use a new key to run again on purpose.";

    /// <summary>Adds the reserved arguments to the schema of every tool they apply to; returns how many tools changed.</summary>
    public static int AddAll(IEnumerable<McpServerTool>? tools)
    {
        if (tools is null) return 0;
        int changed = 0;
        foreach (var tool in tools)
        {
            if (WithReserved(tool.ProtocolTool.Name, tool.ProtocolTool.InputSchema) is not { } schema) continue;
            tool.ProtocolTool.InputSchema = schema;
            changed++;
        }
        return changed;
    }

    /// <summary>The names added to <paramref name="toolName"/>'s schema, with their descriptions.</summary>
    internal static IReadOnlyList<(string Name, string Description)> For(string toolName)
    {
        var list = new List<(string, string)>();
        var tool = ToolCatalog.Find(toolName);
        if (tool is { Confirm: not ConfirmClass.Never })
            list.Add((HumanConfirmation.TokenArgument, TokenDescription));
        if (tool is { Receipts: true })
            list.Add((RequestReceipts.KeyArgument, KeyDescription));
        return list;
    }

    /// <summary>The schema with the reserved arguments added, or null when the tool takes none or the schema has them already.</summary>
    internal static JsonElement? WithReserved(string toolName, JsonElement schema)
    {
        var reserved = For(toolName);
        if (reserved.Count == 0 || schema.ValueKind != JsonValueKind.Object) return null;
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root) return null;
        if (root["properties"] is not JsonObject properties)
        {
            properties = new JsonObject();
            root["properties"] = properties;
        }

        bool changed = false;
        foreach (var (name, description) in reserved)
        {
            if (properties.ContainsKey(name)) continue;
            properties[name] = new JsonObject { ["type"] = "string", ["description"] = description };
            changed = true;
        }
        if (!changed) return null;

        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }
}
