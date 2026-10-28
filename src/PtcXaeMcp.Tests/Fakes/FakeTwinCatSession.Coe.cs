using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- coe-registers -------------------------------------------------------

    public Dictionary<string, CoeDictionary> CoeDictionaryByPath { get; } = new();
    public Dictionary<string, CoeStartupList> CoeStartupByPath { get; } = new();
    public Exception? ReadCoeDictionaryThrows { get; set; }
    public Exception? ReadCoeStartupThrows { get; set; }

    public CoeDictionary ReadCoeDictionary(string treePath, string? indexFilter)
    {
        CallLog.Add($"ReadCoeDictionary({treePath}, filter={indexFilter ?? "<null>"})");
        if (ReadCoeDictionaryThrows is not null) throw ReadCoeDictionaryThrows;
        return CoeDictionaryByPath.TryGetValue(treePath, out var d)
            ? d
            : throw new InvalidOperationException($"Tree path not found: {treePath}");
    }

    public EtherCatStartupResult FakeEtherCatStartupResult { get; set; } = new([], 0);
    public Exception? ReadEtherCatStartupThrows { get; set; }

    public EtherCatStartupResult ReadEtherCatStartup(string? treePath, bool includeFixed, string? protocol)
    {
        CallLog.Add($"ReadEtherCatStartup({treePath},{includeFixed},{protocol})");
        if (ReadEtherCatStartupThrows is not null) throw ReadEtherCatStartupThrows;
        return FakeEtherCatStartupResult;
    }

    public SoeParameterReadResult FakeSoeParameterResult { get; set; } =
        new("", "10.0.0.1.2.1", 1002, SoeParameterMode.Read, []);
    public Exception? ReadSoeParametersThrows { get; set; }

    public SoeParameterReadResult ReadSoeParameters(string treePath, IReadOnlyList<int>? idns, int? driveNo, IReadOnlyList<int> elements, int timeoutMs)
    {
        CallLog.Add($"ReadSoeParameters({treePath},{SoeParameterText.Idns(idns)},{(driveNo is null ? "-" : driveNo.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))},{SoeParameterText.Elements(elements)},{timeoutMs})");
        if (ReadSoeParametersThrows is not null) throw ReadSoeParametersThrows;
        return FakeSoeParameterResult;
    }

    public CoeStartupList ReadCoeStartup(string treePath)
    {
        CallLog.Add($"ReadCoeStartup({treePath})");
        if (ReadCoeStartupThrows is not null) throw ReadCoeStartupThrows;
        return CoeStartupByPath.TryGetValue(treePath, out var s)
            ? s
            : throw new InvalidOperationException($"Tree path not found: {treePath}");
    }
}
