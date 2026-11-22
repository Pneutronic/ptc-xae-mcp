using PtcXaeMcp.WinForms.Diagnostics;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms.Dialogs;

/// <summary>One read-only row of the Settings dialog: what it is, its current value and how to change it.</summary>
internal sealed record SettingsRow(string Name, string Value, string HowToChange);

/// <summary>
/// The environment-driven server settings shown read-only in the Settings dialog (issue #273).
/// They are fixed for the lifetime of the process, so changing one means setting the variable and
/// restarting the app. The bearer token value is never shown, only whether one is set.
/// </summary>
internal static class SettingsInfo
{
    /// <param name="updateCheckState">The update-check row's value (<see cref="Updates.UpdateCheck.DescribeState"/>),
    /// or <c>null</c> to leave the row out.</param>
    public static IReadOnlyList<SettingsRow> Rows(
        int port, bool diagnosticsEnabled, Func<string, string?> getEnv, string? updateCheckState = null)
    {
        static string Env(string name) => Loc.Format("settings.how.env", name);
        static string OnOff(bool on) => Loc.Get(on ? "settings.value.on" : "settings.value.off");
        string Raw(string name, string unset) => getEnv(name) is { Length: > 0 } v ? v : unset;
        string none = Loc.Get("settings.value.none");

        bool tokenSet = !string.IsNullOrEmpty(getEnv(LocalhostGuard.TokenEnvVar));
        var rows = new List<SettingsRow>
        {
            new(Loc.Get("settings.row.port"), port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Loc.Get("settings.how.port")),
            new(Loc.Get("settings.row.token"), Loc.Get(tokenSet ? "settings.value.set" : "settings.value.notSet"),
                Env(LocalhostGuard.TokenEnvVar)),
            new(Loc.Get("settings.row.allowedPaths"), Raw(PathPolicy.AllowedPathsEnvVar, Loc.Get("settings.value.allowedPaths.none")),
                Env(PathPolicy.AllowedPathsEnvVar)),
            new(Loc.Get("settings.row.diagnostics"), OnOff(diagnosticsEnabled),
                Loc.Format("settings.how.diagnostics", Env(McpBridge.DiagnosticsEnvVar))),
            new(Loc.Get("settings.row.safetyPrefixes"),
                Raw(SafetySdsParser.PrefixesEnvVar, Loc.Format("settings.value.default", SafetySdsParser.DefaultPrefixes)),
                Env(SafetySdsParser.PrefixesEnvVar)),
            new(Loc.Get("settings.row.keepRuns"), LogRetention.ResolveKeepRuns(getEnv).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Env(LogRetention.KeepRunsEnvVar)),
            new(Loc.Get("settings.row.keepDays"), LogRetention.ResolveKeepDays(getEnv).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Env(LogRetention.KeepDaysEnvVar)),
            new(Loc.Get("settings.row.reportsMirror"), OnOff(getEnv(McpBridge.ReportsMirrorEnvVar) == "1"),
                $"{Env(McpBridge.ReportsMirrorEnvVar)}=1"),
            new(Loc.Get("settings.row.hmiAutoConfirm"), OnOff(getEnv(HmiAutoConfirmPolicy.EnvVar)?.Trim() == "1"),
                $"{Env(HmiAutoConfirmPolicy.EnvVar)}=1"),
            new(Loc.Get("settings.row.toolProfileOverride"), Raw(Mcp.ToolProfiles.EnvVar, Loc.Get("settings.value.toolProfileOverride.none")),
                Loc.Format("settings.how.toolProfileOverride", Env(Mcp.ToolProfiles.EnvVar))),
            new(Loc.Get("settings.row.accessLevelEnv"), Raw(Mcp.AccessPolicy.LevelEnvVar, none),
                Loc.Format("settings.how.accessLevelEnv", Env(Mcp.AccessPolicy.LevelEnvVar))),
            new(Loc.Get("settings.row.blockedEnv"), Raw(Mcp.AccessPolicy.BlockedEnvVar, none), Env(Mcp.AccessPolicy.BlockedEnvVar)),
            new(Loc.Get("settings.row.allowedEnv"), Raw(Mcp.AccessPolicy.AllowedEnvVar, none), Env(Mcp.AccessPolicy.AllowedEnvVar)),
            new(Loc.Get("settings.row.writeAllowEnv"), Raw(Mcp.AccessPolicy.AdsWriteAllowEnvVar, none),
                Loc.Format("settings.how.writeAllowEnv", Env(Mcp.AccessPolicy.AdsWriteAllowEnvVar))),
            new(Loc.Get("settings.row.writeDenyEnv"), Raw(Mcp.AccessPolicy.AdsWriteDenyEnvVar, none), Env(Mcp.AccessPolicy.AdsWriteDenyEnvVar)),
            new(Loc.Get("settings.row.confirmEnv"), Raw(Mcp.AccessPolicy.ConfirmEnvVar, none),
                Loc.Format("settings.how.confirmEnv", Env(Mcp.AccessPolicy.ConfirmEnvVar))),
        };
        HostLanguage.Resolve(getEnv, null, null, null, out string? ignoredLanguage);
        rows.Add(new(Loc.Get("settings.row.language"),
            ignoredLanguage is null
                ? HostLanguage.Current
                : Loc.Format("settings.row.language.ignored", HostLanguage.EnvVar, ignoredLanguage),
            Loc.Format("settings.row.language.how", HostLanguage.EnvVar,
                string.Join(", ", HostLanguage.Shipped.Select(l => l.Code)))));
        if (updateCheckState is not null)
            rows.Add(new(Loc.Get("settings.row.updateCheck"), updateCheckState,
                Loc.Format("settings.how.updateCheck", Env(Updates.UpdateCheck.EnvVar),
                    Updates.UpdateCheck.PolicyKeyPath, Updates.UpdateCheck.PolicyValueName)));
        return rows;
    }
}
