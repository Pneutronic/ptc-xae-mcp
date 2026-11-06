using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// MCP prompts: proven step-by-step workflows a user picks in the client (in Claude Code as a slash command).
/// The texts are the embedded <c>Mcp\Prompts\*.md</c> files; <c>{{name}}</c> is replaced by the argument. A prompt is
/// listed only when the client's tool profile shows the toolset it is mainly about.
/// </summary>
[McpServerPromptType]
internal sealed class McpPrompts
{
    /// <summary>Every prompt with the toolset it needs to be useful.</summary>
    internal static readonly IReadOnlyDictionary<string, Toolset> Needs = new Dictionary<string, Toolset>(StringComparer.Ordinal)
    {
        ["plc-change"] = Toolset.Plc,
        ["commission-ethercat"] = Toolset.Io,
        ["diagnose-runtime"] = Toolset.Ads,
        ["run-tcunit"] = Toolset.Plc,
        ["scope-measure"] = Toolset.Scope,
        ["doctor"] = Toolset.Meta,
    };

    [McpServerPrompt(Name = "plc-change", Title = "Change PLC code")]
    [Description("Find, read, change, build and check PLC code in small verified steps.")]
    public static string PlcChange([Description("What to change, in your own words.")] string task) =>
        Render("plc-change", ("task", task));

    [McpServerPrompt(Name = "commission-ethercat", Title = "Commission EtherCAT I/O")]
    [Description("Compare the configured EtherCAT topology with the hardware, add and link boxes, build, activate on request.")]
    public static string CommissionEtherCat(
        [Description("Tree path or name of the EtherCAT master, e.g. 'Device 1 (EtherCAT)'.")] string master,
        [Description("AMS NetId of the target system.")] string target) =>
        Render("commission-ethercat", ("master", master), ("target", target));

    [McpServerPrompt(Name = "diagnose-runtime", Title = "Diagnose a runtime")]
    [Description("Read status, alarms, errors, core dumps and fieldbus of a target to name the likely cause. Changes nothing.")]
    public static string DiagnoseRuntime([Description("AMS NetId of the target system, or 'local'.")] string target) =>
        Render("diagnose-runtime", ("target", target));

    [McpServerPrompt(Name = "run-tcunit", Title = "Run TcUnit tests")]
    [Description("Build, run the TcUnit tests on the test runtime and map failures to their elements.")]
    public static string RunTcUnit() => Render("run-tcunit");

    [McpServerPrompt(Name = "scope-measure", Title = "Measure with the Scope")]
    [Description("Record PLC signals with the TwinCAT Scope and evaluate them.")]
    public static string ScopeMeasure(
        [Description("PLC symbols to record, separated by commas, e.g. 'MAIN.bStart, MAIN.nState'.")] string symbols,
        [Description("Record time in seconds.")] string seconds) =>
        Render("scope-measure", ("symbols", symbols), ("seconds", seconds));

    [McpServerPrompt(Name = "doctor", Title = "Check the setup")]
    [Description("Check whether ptc-xae-mcp and TwinCAT on this machine are ready, and explain what is not.")]
    public static string Doctor() => Render("doctor");

    /// <summary>The prompt text with its placeholders filled; an argument left empty shows as "(not given)".</summary>
    internal static string Render(string name, params (string Key, string Value)[] arguments)
    {
        string text = Text(name);
        foreach (var (key, value) in arguments)
            text = text.Replace("{{" + key + "}}", string.IsNullOrWhiteSpace(value) ? "(not given)" : value.Trim(), StringComparison.Ordinal);
        return text;
    }

    internal static string Text(string name)
    {
        using var stream = typeof(McpPrompts).Assembly.GetManifestResourceStream($"PtcXaeMcp.WinForms.Prompts.{name}.md")
            ?? throw new InvalidOperationException($"The prompt text '{name}' is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>prompts/list filter: a prompt about a toolset the client's profile hides is left out.</summary>
    public static McpRequestHandler<ListPromptsRequestParams, ListPromptsResult> ListFilter(
        ToolSurface surface, McpRequestHandler<ListPromptsRequestParams, ListPromptsResult> next) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            var profile = surface.ProfileFor(context.Server?.ClientInfo?.Name);
            result.Prompts = result.Prompts
                .Where(p => !Needs.TryGetValue(p.Name, out var set) || profile.Shows(set))
                .ToList();
            return result;
        };
}
