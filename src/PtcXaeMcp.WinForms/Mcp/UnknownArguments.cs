using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PtcXaeMcp.WinForms.Mcp;

// An argument a tool does not declare used to be dropped without a word: twincat_plc_create had no
// 'implementation' parameter, answered "Declaration written" and the body passed with it was gone
// (2026-11-05). Every tool call is checked against the tool's input schema before it runs; an unknown
// name refuses the call with the tool's parameters listed. Names compare case-insensitively, so a call
// the SDK binds today is never refused.
internal static class UnknownArguments
{
    /// <summary>The argument names that are not a property of <paramref name="inputSchema"/>, in call order.</summary>
    public static IReadOnlyList<string> Find(JsonElement inputSchema, IEnumerable<string> argumentNames)
    {
        var known = Parameters(inputSchema).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return argumentNames.Where(a => !known.Contains(a)).ToList();
    }

    /// <summary>The parameter names the schema declares, in schema order.</summary>
    public static IReadOnlyList<string> Parameters(JsonElement inputSchema) =>
        inputSchema.ValueKind == JsonValueKind.Object
        && inputSchema.TryGetProperty("properties", out var props)
        && props.ValueKind == JsonValueKind.Object
            ? props.EnumerateObject().Select(p => p.Name).ToList()
            : [];

    public static string Message(string tool, IReadOnlyList<string> unknown, IReadOnlyList<string> parameters)
    {
        string names = string.Join(", ", unknown.Select(u => $"'{u}'"));
        string valid = parameters.Count == 0 ? "it takes no parameters" : $"its parameters are: {string.Join(", ", parameters)}";
        return $"[Error] {tool} has no parameter {names}; {valid}. Nothing was done — an unknown argument would otherwise be ignored.";
    }

    /// <summary>Call-tool filter: refuses a call with an argument the tool does not declare, before the tool runs.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) =>
        {
            if (context.Params?.Arguments is { Count: > 0 } arguments && ToolOf(context) is { } tool)
            {
                JsonElement schema = tool.ProtocolTool.InputSchema;
                IReadOnlyList<string> unknown = Find(schema, arguments.Keys);
                if (unknown.Count > 0)
                    return ValueTask.FromResult(new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = Message(tool.ProtocolTool.Name, unknown, Parameters(schema)) }],
                    });
            }
            return next(context, cancellationToken);
        };

    private static McpServerTool? ToolOf(RequestContext<CallToolRequestParams> context)
    {
        if (context.MatchedPrimitive is McpServerTool matched) return matched;
        string? name = context.Params?.Name;
        return name is not null && context.Server?.ServerOptions.ToolCollection is { } tools && tools.TryGetPrimitive(name, out var tool)
            ? tool
            : null;
    }
}
