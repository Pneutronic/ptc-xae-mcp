using System.Diagnostics;
using EnvDTE80;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- Reload Device Descriptions ---------------------------------------------
    // Raised by GUID/ID (DeviceDescriptionReloadCommand), never by its localized name. A command
    // the shell reports as unavailable is not forced; a window that blocks the XAE surfaces as
    // XaeBlockedException (named, not answered). Re-reading the ESI files changes nothing in the
    // project, so a busy retry may safely repeat the call.
    public DeviceDescriptionReloadResult ReloadDeviceDescriptions() => ComBusyRetry.Run(() =>
    {
        DTE2 dte = (DTE2)GetActiveTcDte();

        dynamic command;
        try { command = dte.Commands.Item(DeviceDescriptionReloadCommand.Guid, DeviceDescriptionReloadCommand.Id); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            return new DeviceDescriptionReloadResult(false,
                $"This XAE does not know the Reload Device Descriptions command ({ex.Message}).", TimeSpan.Zero);
        }

        bool available = ComCall.ReadProp(() => (bool)command.IsAvailable, false);
        if (!available)
            return new DeviceDescriptionReloadResult(false,
                "The XAE reports the Reload Device Descriptions command as unavailable (it needs an open TwinCAT project).",
                TimeSpan.Zero);

        object? customIn = null;
        object? customOut = null;
        var watch = Stopwatch.StartNew();
        try { dte.Commands.Raise(DeviceDescriptionReloadCommand.Guid, DeviceDescriptionReloadCommand.Id, ref customIn, ref customOut); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            return new DeviceDescriptionReloadResult(false,
                $"The Reload Device Descriptions command failed: {ex.Message}", watch.Elapsed);
        }
        return new DeviceDescriptionReloadResult(true, "Device descriptions reloaded.", watch.Elapsed);
    }, BuildTimeout);
}
