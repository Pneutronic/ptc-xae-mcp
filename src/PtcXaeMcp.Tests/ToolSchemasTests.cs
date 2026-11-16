using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>Optional parameters are listed without the "null" type and the null default; nothing else changes.</summary>
public sealed class ToolSchemasTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void AnOptionalNullableParameter_LosesTheNullTypeAndTheNullDefault()
    {
        var schema = Parse("""
            {
              "type": "object",
              "properties": {
                "path":  { "description": "p", "type": "string" },
                "name":  { "description": "n", "type": ["string", "null"], "default": null },
                "limit": { "type": ["integer", "null"], "default": null },
                "force": { "type": "boolean", "default": true },
                "must":  { "type": ["string", "null"] }
              },
              "required": ["path", "must"]
            }
            """);

        var slim = ToolSchemas.Slim(schema)!.Value;
        var props = slim.GetProperty("properties");

        Assert.Equal("string", props.GetProperty("name").GetProperty("type").GetString());
        Assert.False(props.GetProperty("name").TryGetProperty("default", out _));
        Assert.Equal("n", props.GetProperty("name").GetProperty("description").GetString());
        Assert.Equal("integer", props.GetProperty("limit").GetProperty("type").GetString());
        Assert.True(props.GetProperty("force").GetProperty("default").GetBoolean());
        // A required parameter keeps the schema the SDK generated.
        Assert.Equal(JsonValueKind.Array, props.GetProperty("must").GetProperty("type").ValueKind);
        Assert.Equal(2, slim.GetProperty("required").GetArrayLength());
    }

    [Fact]
    public void ASchemaWithNothingToSlim_IsLeftAsItIs()
    {
        var schema = Parse("""{ "type": "object", "properties": { "a": { "type": "string" } } }""");
        Assert.Null(ToolSchemas.Slim(schema));
        Assert.Null(ToolSchemas.Slim(Parse("""{ "type": "object" }""")));
    }

    [Fact]
    public void EveryTool_KeepsItsParametersAndRequiredList_AndListsNoNullOnOptionalOnes()
    {
        var tools = typeof(TwinCatMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, createTargetFunc: static _ => throw new InvalidOperationException(), options: null))
            .ToList();
        var before = tools.ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.InputSchema.Clone());

        int changed = ToolSchemas.SlimAll(tools);

        Assert.True(changed > 50, $"only {changed} schemas changed");
        foreach (var tool in tools)
        {
            var old = before[tool.ProtocolTool.Name];
            var now = tool.ProtocolTool.InputSchema;
            Assert.Equal(Names(old, "properties"), Names(now, "properties"));
            Assert.Equal(Required(old), Required(now));
            Assert.Null(ToolSchemas.Slim(now)); // slimming twice changes nothing

            if (!now.TryGetProperty("properties", out var props)) continue;
            foreach (var p in props.EnumerateObject().Where(p => !Required(now).Contains(p.Name)))
            {
                Assert.False(p.Value.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Null,
                    $"{tool.ProtocolTool.Name}.{p.Name} still has default null");
                Assert.False(p.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Array
                             && t.EnumerateArray().Any(e => e.GetString() == "null"),
                    $"{tool.ProtocolTool.Name}.{p.Name} still lists the null type");
            }
        }
    }

    private static List<string> Names(JsonElement schema, string key) =>
        schema.TryGetProperty(key, out var o) ? o.EnumerateObject().Select(p => p.Name).ToList() : [];

    private static List<string> Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToList() : [];
}
