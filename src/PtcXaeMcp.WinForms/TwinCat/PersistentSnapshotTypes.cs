using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

// ---- persistent-snapshot ------------------
// Result and value shapes of the persistent-snapshot tools. Everything here is plain data,
// free of any ADS type, so the logic around it is unit-testable without a PLC.

/// <summary>One leaf value: the declared PLC type plus the exact round-trip value.</summary>
/// <param name="Type">Normalised PLC type name (e.g. "LREAL", "STRING", an ENUM's own name).</param>
/// <param name="Value">JSON-native value: number, bool or string (TIME/DT as the raw integer).</param>
/// <param name="Name">ENUM only: the declared member name, if the value matches one.</param>
/// <param name="Display">Time/date types only: the human-readable form (never read back).</param>
internal sealed record SnapshotLeaf(string Type, JsonElement Value, string? Name = null, string? Display = null);

internal sealed record SnapshotSkip(string Path, string Reason);

internal sealed record SnapshotMeta(
    string Utc,
    string AmsNetId,
    int AdsPort,
    string? Solution,
    string? PlcApp,
    IReadOnlyList<string> Roots,
    int LeafCount,
    IReadOnlyList<SnapshotSkip> Skipped);

internal sealed record SnapshotDocument(SnapshotMeta Meta, IReadOnlyDictionary<string, SnapshotLeaf> Values)
{
    public const string FormatId = "ptc-persistent-snapshot/1";
}

// ---- session surface -----------------------------------------------------------

/// <param name="Paths">Symbol paths whose subtrees are flattened into leaves (a scalar path is its own leaf).</param>
/// <param name="AdsPersistentRoots">Also add every symbol the runtime flags IsPersistent (selection source "ads").</param>
internal sealed record SnapshotReadRequest(IReadOnlyList<string> Paths, bool AdsPersistentRoots = false);

internal sealed record SnapshotReadResult(
    bool Success,
    IReadOnlyDictionary<string, SnapshotLeaf> Leaves,
    IReadOnlyList<SnapshotSkip> Skipped,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> AdsRoots,
    string AmsNetId,
    int AdsPort,
    string Message);

internal sealed record SnapshotLeafWrite(string Path, SnapshotLeaf Value);

/// <summary>Outcome of one leaf write. <c>Outcome</c> is "written" or "failed".</summary>
internal sealed record SnapshotWriteItem(string Path, string Outcome, SnapshotLeaf? ReadBack, string Message);

internal sealed record SnapshotWriteResult(
    bool Success,
    IReadOnlyList<SnapshotWriteItem> Items,
    string AmsNetId,
    int AdsPort,
    string Message);

internal sealed record AdsSymbolEntry(
    string Path, string Type, long Size, bool IsPersistent, bool ReadOnly, string? Comment);

internal sealed record AdsSymbolListResult(
    bool Success,
    IReadOnlyList<AdsSymbolEntry> Symbols,
    bool Truncated,
    string AmsNetId,
    int AdsPort,
    string Message);

internal sealed record AdsRunStateResult(bool Success, string State, string AmsNetId, int AdsPort, string Message);
