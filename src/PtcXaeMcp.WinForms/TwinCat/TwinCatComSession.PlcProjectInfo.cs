namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // The PLC project item (ItemType 600) carries the project-wide settings, among them <IECProjectDef><ProjectInfo>.
    public string ReadPlcProjectXml(string? plcProjectName) =>
        ComBusyRetry.Run(() => (string)PlcProjectItem(plcProjectName).ProduceXml(false), OpenTimeout);

    public void ConsumePlcProjectXml(string? plcProjectName, string xml) =>
        ComBusyRetry.Run(() => { PlcProjectItem(plcProjectName).ConsumeXml(xml); }, OpenTimeout);

    private static dynamic PlcProjectItem(string? plcProjectName)
    {
        dynamic dte = GetActiveTcDte();
        return FindPlcSourceProjectNode(dte, ResolvePlcInstanceName(plcProjectName))
            ?? throw new InvalidOperationException("No PLC source-project node found under TIPC — open a solution with a PLC project first.");
    }
}
