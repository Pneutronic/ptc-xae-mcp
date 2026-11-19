using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- IO mapping surface -------------------------------------------------

    public Exception? LinkVariablesThrows { get; set; }
    public Exception? UnlinkVariablesThrows { get; set; }
    public string MappingInfoXml { get; set; } = "<VarLinks/>";
    public Exception? GetMappingInfoThrows { get; set; }
    public Exception? SetDeviceDisabledThrows { get; set; }

    public void LinkVariables(string v1, string v2, int offs1 = 0, int offs2 = 0, int size = 0)
    {
        CallLog.Add($"LinkVariables({v1}, {v2})");
        if (LinkVariablesThrows is not null) throw LinkVariablesThrows;
    }

    public void UnlinkVariables(string v1, string v2 = "")
    {
        CallLog.Add($"UnlinkVariables({v1}, {v2})");
        if (UnlinkVariablesThrows is not null) throw UnlinkVariablesThrows;
    }

    public string GetMappingInfo()
    {
        CallLog.Add("GetMappingInfo()");
        if (GetMappingInfoThrows is not null) throw GetMappingInfoThrows;
        return MappingInfoXml;
    }

    public void SetDeviceDisabled(string treePath, bool disabled)
    {
        CallLog.Add($"SetDeviceDisabled({treePath}, {disabled})");
        if (SetDeviceDisabledThrows is not null) throw SetDeviceDisabledThrows;
        OnSetDeviceDisabled?.Invoke(treePath, disabled);
    }

    /// <summary>Called by SetDeviceDisabled after the log entry (e.g. to update <see cref="TreeItemXml"/>).</summary>
    public Action<string, bool>? OnSetDeviceDisabled { get; set; }
}
