using System.Reflection;
using System.Text.Json;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// The access policy as <see cref="McpActivity.AccessGate"/>: finds the tool in <see cref="ToolCatalog"/> and the
/// target system of a runtime action from the traced arguments or the open solution, then asks
/// <see cref="AccessPolicy.Deny"/>. The target is only looked up when the policy has NetId rules.
/// </summary>
internal static class AccessGate
{
    public static Func<string, object?, AccessDecision> Create(
        Func<AccessPolicy> policy, Func<string?> projectTarget, Func<IReadOnlyList<string>> umrInstanceNetIds) =>
        (toolName, arguments) =>
        {
            // A traced name the catalog does not know changes something unless proven otherwise.
            var tool = ToolCatalog.Find(toolName)
                ?? new ToolInfo(toolName, Toolset.Meta, AccessLevel.Engineer, TargetRule.None, AlwaysAllowed: false, ReadOnly: false);
            string? denied = policy().Deny(tool,
                () => Target(tool, arguments, projectTarget),
                () => LocalNetIds.Collect(umrInstanceNetIds),
                out string? warning);
            return denied is null && warning is null ? AccessDecision.Allowed : new AccessDecision(denied, warning);
        };

    /// <summary>The NetId a runtime action reaches: a literal, <see cref="AccessPolicy.LocalKeyword"/>, or null when unknown.</summary>
    internal static string? Target(ToolInfo tool, object? arguments, Func<string?> projectTarget) => tool.Target switch
    {
        TargetRule.AmsNetIdArgument => FromArgument(ArgumentText(arguments, "amsNetId"), projectTarget),
        TargetRule.Project => LocalIfEmpty(projectTarget()),
        TargetRule.Local => AccessPolicy.LocalKeyword,
        _ => null,
    };

    // Empty is the local system (the ADS tools' default), "project" the open solution's target.
    private static string? FromArgument(string? value, Func<string?> projectTarget)
    {
        if (string.IsNullOrWhiteSpace(value)) return AccessPolicy.LocalKeyword;
        if (string.Equals(value.Trim(), AdsTargetNetId.ProjectKeyword, StringComparison.OrdinalIgnoreCase))
            return LocalIfEmpty(projectTarget());
        return value.Trim();
    }

    private static string? LocalIfEmpty(string? projectTargetNetId) => projectTargetNetId switch
    {
        null => null,
        AdsTargetNetId.ProjectTargetIsLocal => AccessPolicy.LocalKeyword,
        var id => id,
    };

    /// <summary>A string argument of the traced argument object (an anonymous object, a dictionary or JSON); null when absent.</summary>
    internal static string? ArgumentText(object? arguments, string name)
    {
        switch (arguments)
        {
            case null:
                return null;
            case IReadOnlyDictionary<string, object?> dict:
                return dict.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
            case JsonElement { ValueKind: JsonValueKind.Object } json:
                foreach (var p in json.EnumerateObject())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                return null;
            default:
                var prop = arguments.GetType().GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                return prop?.GetValue(arguments)?.ToString();
        }
    }
}
