using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>The state of a target over ADS, shown with an <c>outcome_unknown</c> receipt so a person (or an agent) can tell what became of the call.</summary>
internal static class ReceiptLiveState
{
    public static string Read(string? netId)
    {
        if (string.IsNullOrWhiteSpace(netId)) return "unknown (the target of the call could not be told)";
        var target = ActivationTarget.Resolve(netId, LocalAmsNetIdResolver.Shared.Resolve());
        if (target is null) return $"'{netId}' is not an AMS NetId, so no ADS state was read";

        using var system = TargetStateReader.Open(target.NetId);
        using var plc = TargetStateReader.Open(target.NetId, SystemModeSwitch.FirstPlcPort);
        return $"{target.Describe()}: TwinCAT system {system.Read()}, PLC runtime on port {SystemModeSwitch.FirstPlcPort} {plc.Read()}";
    }
}
