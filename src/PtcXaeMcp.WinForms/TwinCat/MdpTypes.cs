namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>Outcome class of an IPC inventory read.</summary>
internal static class IpcInventoryStatus
{
    public const string Ok = "ok";
    /// <summary>The ADS router answers but the Device Manager (MDP, AMS port 10000, index group 0xF302) does not.</summary>
    public const string MdpUnavailable = "mdpUnavailable";
    public const string TargetUnreachable = "targetUnreachable";
    /// <summary>The module list is empty: MDP creates its modules dynamically some seconds after boot.</summary>
    public const string MdpNotReady = "mdpNotReady";
}

/// <summary>Result of one raw MDP read. <see cref="AdsError"/> 0 means success.</summary>
internal sealed record AdsRawReadResult(uint AdsError, string AdsErrorName, byte[] Data)
{
    public bool Ok => AdsError == 0;
    public static AdsRawReadResult Success(byte[] data) => new(0, "NoError", data);
    public static AdsRawReadResult Failure(uint code, string name) => new(code, name, []);
    public string ErrorText => $"{AdsErrorName} ({AdsError})";
}

/// <summary>The raw ADS read seam of the MDP and EtherCAT readers; a synthetic implementation drives the unit tests.</summary>
internal interface IAdsRawReader
{
    /// <summary>Reads at most <paramref name="maxLength"/> bytes; the result carries the bytes actually returned.</summary>
    AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength);
}

/// <summary>The MDP address scheme: AMS port 10000, index group 0xF302, index offset = tableIndex &lt;&lt; 16 | subIndex.</summary>
internal static class MdpAddress
{
    public const int AmsPort = 10000;
    public const uint IndexGroup = 0xF302;
    public const int ModuleListTable = 0xF020;
    public const int ModuleNameTable = 0xF060;
    public const int DeviceInfoTable = 0xF9F0;

    public static uint IndexOffset(int tableIndex, int subIndex) => ((uint)(tableIndex & 0xFFFF) << 16) | (uint)(subIndex & 0xFFFF);

    /// <summary>Table index of a config-area table: 0x8000 | moduleId &lt;&lt; 4 | tableId.</summary>
    public static int ConfigTable(int moduleId, int tableId) => 0x8000 | ((moduleId & 0xFF) << 4) | (tableId & 0xF);
}

/// <summary>The per-call ADS timeout of the tools: default 5000 ms, 100..30000 ms.</summary>
internal static class AdsTimeout
{
    public const int DefaultMs = 5000;
    public const int MinMs = 100;
    public const int MaxMs = 30000;

    /// <summary>The timeout to use, or an [Error] text when the requested one is out of range.</summary>
    public static string? Resolve(int? requested, out int timeoutMs)
    {
        timeoutMs = requested ?? DefaultMs;
        return timeoutMs is >= MinMs and <= MaxMs
            ? null
            : $"[Error] timeoutMs {timeoutMs} is out of range ({MinMs}-{MaxMs} ms, default {DefaultMs}).";
    }
}

/// <summary>Repeats a read once when the ADS client times out (ClientSyncTimeOut, 1861), before the error is recorded.</summary>
internal sealed class RetryOnTimeoutReader(IAdsRawReader inner) : IAdsRawReader, IDisposable
{
    public const uint ClientSyncTimeout = 1861;

    public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength)
    {
        var result = inner.Read(indexGroup, indexOffset, maxLength);
        return !result.Ok && result.AdsError == ClientSyncTimeout ? inner.Read(indexGroup, indexOffset, maxLength) : result;
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}

internal sealed record MdpEntry(int SubIndex, string? Name, string? DataType, object? Value, string? Raw, string? AdsError, uint IndexOffset);

internal sealed record MdpTable(int TableIndex, string? Name, IReadOnlyList<MdpEntry> Entries);

internal sealed record MdpModule(
    int ModuleId, int ModuleType, string TypeName, string? Name,
    IReadOnlyList<MdpTable> Tables, IReadOnlyDictionary<string, object?>? Decoded);

internal sealed record MdpDevice(string? Btn, string? Bic);

internal sealed record IpcInventoryResult(
    string Status, string NetId, int Port, DateTimeOffset ReadAt, string? Message,
    uint? AdsErrorCode, string? AdsErrorName,
    MdpDevice? Device, IReadOnlyList<MdpModule> Modules, IReadOnlyList<string> Warnings)
{
    public bool Success => Status == IpcInventoryStatus.Ok;
}

/// <summary>Hard limits of one inventory call.</summary>
internal sealed record MdpReadLimits(int MaxLen = 255, int BufferSize = 1024, int MaxReads = 4000, TimeSpan? MaxDuration = null)
{
    public TimeSpan Duration => MaxDuration ?? TimeSpan.FromSeconds(120);
}
