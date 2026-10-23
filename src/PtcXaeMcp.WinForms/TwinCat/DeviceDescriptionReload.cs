using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// The XAE menu command "TwinCAT > EtherCAT Devices > Reload Device Descriptions". Its name is
/// localized ("TwinCAT.Gerätebeschreibungenneuladen" on a German XAE); its identity — command-set
/// GUID + ID — is not, so the command is only ever addressed by that identity (read from
/// dte.Commands on TcXaeShell 4026.22).
/// </summary>
internal static class DeviceDescriptionReloadCommand
{
    public const string Guid = "{40EE08E0-8FB4-46E9-BAAB-100E60019B7B}";
    public const int Id = 4520;

    public const string MenuPath = "TwinCAT > EtherCAT Devices > Reload Device Descriptions";
}

/// <param name="Success">True when the command was issued and returned without an error.</param>
/// <param name="Message">What happened, or why the command could not run.</param>
/// <param name="Elapsed">Time the command call took.</param>
internal sealed record DeviceDescriptionReloadResult(bool Success, string Message, TimeSpan Elapsed);

/// <summary>
/// Makes the XAE re-read the ESI files under <c>Config\Io\EtherCAT</c>. Without it a vendor ESI put there
/// afterwards is unknown to the XAE until it is restarted, and adding that box fails with
/// "Invalid item sub type".
/// </summary>
internal sealed class DeviceDescriptionsCoordinator(ITwinCatSession session)
{
    public string Reload()
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        DeviceDescriptionReloadResult result;
        try { result = session.ReloadDeviceDescriptions(); }
        catch (XaeBlockedException ex)
        {
            // A dialog is never answered here: it is named so the user can deal with it.
            string dialog = string.IsNullOrWhiteSpace(ex.Dialog) ? "" : $" Blocking dialog: '{ex.Dialog}'.";
            return $"[Error] The XAE did not accept the reload command: {ex.Message}{dialog} Nothing was answered. " +
                   $"Close the dialog and retry, or run it by hand: {DeviceDescriptionReloadCommand.MenuPath}.";
        }
        catch (InvalidOperationException ex) { return $"[Error] {ex.Message}"; }

        return result.Success
            ? $"Device descriptions reloaded in {result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s."
            : $"[Error] {result.Message} Run it by hand: {DeviceDescriptionReloadCommand.MenuPath}.";
    }
}
