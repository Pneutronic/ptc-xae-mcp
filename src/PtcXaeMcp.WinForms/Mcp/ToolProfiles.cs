namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>Where the profile of a session comes from, strongest first.</summary>
internal enum ProfileSource { Environment, Gui, ClientRule, Default }

/// <summary>The toolsets a session sees, the name it was given by and why it applies.</summary>
internal sealed record EffectiveProfile(string Name, IReadOnlySet<Toolset> Toolsets, ProfileSource Source, string Reason)
{
    public bool Shows(Toolset toolset) => Toolsets.Contains(toolset);

    public string Describe() => $"{Name} ({Reason})";
}

/// <summary>
/// Which tools a client sees. A profile is a named list of toolsets: <c>all</c>, or <c>core</c> for clients that
/// struggle with many tools. <see cref="EnvVar"/> wins, then the setting in the window, then a rule by the client's
/// self-reported name; an unknown client sees everything. Visibility only — the access policy decides what may run.
/// </summary>
internal static class ToolProfiles
{
    public const string EnvVar = "PTC_XAE_MCP_TOOLSETS";
    public const string All = "all";
    public const string Core = "core";

    /// <summary>The value of the window setting that leaves the choice to the client rules.</summary>
    public const string Automatic = "auto";

    private static readonly IReadOnlySet<Toolset> AllToolsets = Enum.GetValues<Toolset>().ToHashSet();

    private static readonly IReadOnlySet<Toolset> CoreToolsets = new HashSet<Toolset>
    {
        Toolset.Meta, Toolset.Project, Toolset.Build, Toolset.Plc, Toolset.Ads, Toolset.Runtime,
    };

    /// <summary>
    /// Client names are self-reported and differ between versions, so a rule matches a part of the name,
    /// case-insensitively; the first match wins. Clients known to work with every tool come first.
    /// </summary>
    internal static readonly IReadOnlyList<(string NamePart, string Profile)> ClientRules =
    [
        ("claude", All),
        ("cursor", Core),
        ("windsurf", Core),
        ("visual studio code", Core),
        ("vscode", Core),
        ("copilot", Core),
    ];

    public static EffectiveProfile Resolve(string? environmentValue, string? guiValue, string? clientName)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue))
            return Parse(environmentValue, ProfileSource.Environment, $"{EnvVar}={environmentValue.Trim()}");

        if (!string.IsNullOrWhiteSpace(guiValue) && !string.Equals(guiValue.Trim(), Automatic, StringComparison.OrdinalIgnoreCase))
            return Parse(guiValue, ProfileSource.Gui, "set in the window");

        if (!string.IsNullOrWhiteSpace(clientName))
            foreach (var (part, profile) in ClientRules)
                if (clientName.Contains(part, StringComparison.OrdinalIgnoreCase))
                    return Named(profile, ProfileSource.ClientRule, $"client '{clientName}'");

        return Named(All, ProfileSource.Default, string.IsNullOrWhiteSpace(clientName) ? "default" : $"default for client '{clientName}'");
    }

    /// <summary>
    /// A profile name (<c>all</c>, <c>core</c>) or a comma-separated list of toolsets (<c>plc,hmi</c>). Meta is always
    /// added. An unknown name makes the whole value fall back to <c>all</c>, so a typo never hides tools.
    /// </summary>
    internal static EffectiveProfile Parse(string value, ProfileSource source, string reason)
    {
        string trimmed = value.Trim();
        if (string.Equals(trimmed, All, StringComparison.OrdinalIgnoreCase)) return Named(All, source, reason);
        if (string.Equals(trimmed, Core, StringComparison.OrdinalIgnoreCase)) return Named(Core, source, reason);

        var sets = new HashSet<Toolset> { Toolset.Meta };
        foreach (var part in trimmed.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, Core, StringComparison.OrdinalIgnoreCase)) { sets.UnionWith(CoreToolsets); continue; }
            if (!Enum.TryParse<Toolset>(part, ignoreCase: true, out var set) || !Enum.IsDefined(set))
                return Named(All, source, $"{reason}; unknown toolset '{part}', showing all");
            sets.Add(set);
        }
        return new EffectiveProfile(string.Join(",", sets.Order().Select(s => s.ToString().ToLowerInvariant())), sets, source, reason);
    }

    private static EffectiveProfile Named(string name, ProfileSource source, string reason) =>
        new(name, name == Core ? CoreToolsets : AllToolsets, source, reason);
}
