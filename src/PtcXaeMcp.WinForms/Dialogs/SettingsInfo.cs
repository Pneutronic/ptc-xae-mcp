using PtcXaeMcp.WinForms.Diagnostics;
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
        static string Env(string name) => "Environment variable " + name;
        static string OnOff(bool on) => on ? "on" : "off";
        string Raw(string name, string unset) => getEnv(name) is { Length: > 0 } v ? v : unset;

        bool tokenSet = !string.IsNullOrEmpty(getEnv(LocalhostGuard.TokenEnvVar));
        var rows = new List<SettingsRow>
        {
            new("Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "Fixed (http://localhost only)"),
            new("Access token", tokenSet ? "set" : "not set", Env(LocalhostGuard.TokenEnvVar)),
            new("Allowed paths", Raw(PathPolicy.AllowedPathsEnvVar, "(none: open solution, %TEMP%, log folder, TwinCAT install)"),
                Env(PathPolicy.AllowedPathsEnvVar)),
            new("Diagnostics tools", OnOff(diagnosticsEnabled),
                $"Start with --diagnostics or {Env(McpBridge.DiagnosticsEnvVar)}=1"),
            new("Safety signal prefixes", Raw(SafetySdsParser.PrefixesEnvVar, SafetySdsParser.DefaultPrefixes + " (default)"),
                Env(SafetySdsParser.PrefixesEnvVar)),
            new("Keep logs: runs", LogRetention.ResolveKeepRuns(getEnv).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Env(LogRetention.KeepRunsEnvVar)),
            new("Keep logs: days", LogRetention.ResolveKeepDays(getEnv).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Env(LogRetention.KeepDaysEnvVar)),
            new("Mirror reports into the repo", OnOff(getEnv(McpBridge.ReportsMirrorEnvVar) == "1"),
                $"{Env(McpBridge.ReportsMirrorEnvVar)}=1"),
            new("Auto-confirm HMI writes (TEMP fixtures)", OnOff(getEnv(HmiAutoConfirmPolicy.EnvVar)?.Trim() == "1"),
                $"{Env(HmiAutoConfirmPolicy.EnvVar)}=1"),
        };
        if (updateCheckState is not null)
            rows.Add(new("Update check at start", updateCheckState,
                $"Checkbox above; {Env(Updates.UpdateCheck.EnvVar)}=0 or the machine policy HKLM\\{Updates.UpdateCheck.PolicyKeyPath}\\{Updates.UpdateCheck.PolicyValueName}=1 switch it off"));
        return rows;
    }
}
