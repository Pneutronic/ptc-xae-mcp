using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    public IpcInventoryResult FakeIpcInventoryResult { get; set; } = new(
        IpcInventoryStatus.Ok, "10.0.0.1.1.1", MdpAddress.AmsPort, DateTimeOffset.UnixEpoch, null, null, null,
        new MdpDevice(null, null), [], []);
    public Exception? ReadIpcInventoryThrows { get; set; }

    public IpcInventoryResult ReadIpcInventory(string? amsNetId, IReadOnlyList<int>? moduleTypes, bool includeRaw, int timeoutMs)
    {
        CallLog.Add($"ReadIpcInventory({amsNetId},{(moduleTypes is null ? "all" : string.Join("|", moduleTypes))},{includeRaw},{timeoutMs})");
        if (ReadIpcInventoryThrows is not null) throw ReadIpcInventoryThrows;
        return FakeIpcInventoryResult;
    }

    public EtherCatIdentitiesResult FakeEtherCatIdentitiesResult { get; set; } = new(DateTimeOffset.UnixEpoch, []);
    public Exception? ReadEtherCatIdentitiesThrows { get; set; }

    public EtherCatIdentitiesResult ReadEtherCatIdentities(string? masterTreePath, string? amsNetId, int? adsPort, bool includeCrc, bool includeCoe, bool includeDeviceDetails, int timeoutMs)
    {
        CallLog.Add($"ReadEtherCatIdentities({masterTreePath},{amsNetId},{adsPort},{includeCrc},{includeCoe},{includeDeviceDetails},{timeoutMs})");
        if (ReadEtherCatIdentitiesThrows is not null) throw ReadEtherCatIdentitiesThrows;
        return FakeEtherCatIdentitiesResult;
    }

    public List<LibraryRepositoryEntry> FakeLibraryRepositories { get; } = [];
    public List<PlcLibraryContext> FakePlcLibraryContexts { get; } = [];
    public Exception? ListLibraryRepositoryEntriesThrows { get; set; }
    public Exception? ReadPlcLibraryContextsThrows { get; set; }

    public List<string> FakeLibraryRepositoryWarnings { get; } = [];

    public LibraryRepositoryListing ListLibraryRepositoryEntries()
    {
        CallLog.Add("ListLibraryRepositoryEntries()");
        if (ListLibraryRepositoryEntriesThrows is not null) throw ListLibraryRepositoryEntriesThrows;
        return new LibraryRepositoryListing(FakeLibraryRepositories, FakeLibraryRepositoryWarnings);
    }

    public IReadOnlyList<PlcLibraryContext> ReadPlcLibraryContexts(string? plcProjectPath)
    {
        CallLog.Add($"ReadPlcLibraryContexts({plcProjectPath})");
        if (ReadPlcLibraryContextsThrows is not null) throw ReadPlcLibraryContextsThrows;
        return FakePlcLibraryContexts;
    }
}
