using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One <c>&lt;Device&gt;</c> entry of an ESI file (issue #85).</summary>
/// <param name="Type">Inner text of <c>&lt;Device&gt;&lt;Type&gt;</c> (the order code, e.g. <c>EL1008</c>).</param>
/// <param name="VInfo">
/// The revision-exact vInfo for <c>twincat_add_ethercat_box</c>. For Beckhoff
/// (vendor id 2) built by the <c>&lt;Type&gt;-&lt;LoWord&gt;-&lt;HiWord&gt;</c>
/// convention (e.g. <c>EL1008-0000-0017</c>); for other vendors the bare Type text.
/// </param>
internal sealed record EsiCatalogDevice(
    string Type,
    string VInfo,
    string? Name,
    uint? ProductCode,
    uint? RevisionNo,
    uint? VendorId,
    string? VendorName,
    string SourceFile);

/// <summary>Result of scanning one ESI directory (cached per process).</summary>
internal sealed record EsiCatalogScan(
    string Directory,
    IReadOnlyList<EsiCatalogDevice> Devices,
    int FilesScanned,
    int FilesSkipped,
    int NonEsiFiles,
    IReadOnlyList<string> Warnings,
    bool FromCache);

/// <summary>
/// Offline EtherCAT box catalog (issue #85, option 1): streams the ESI files
/// under <c>&lt;TwinCAT&gt;\3.1\Config\Io\EtherCAT</c> (including subfolders —
/// TwinCAT loads those too, e.g. vendor folders) with <see cref="XmlReader"/>
/// and extracts per <c>&lt;Descriptions&gt;&lt;Devices&gt;&lt;Device&gt;</c> the
/// Type text, ProductCode/RevisionNo, Name (LcId 1033 preferred), vendor and
/// source file. Pure file I/O — no XAE, no COM, no open solution. Malformed
/// files are skipped and counted, never fatal. The full scan is cached per
/// process, keyed by directory + newest file timestamp + file count.
/// </summary>
internal static class EsiCatalogScanner
{
    /// <summary>Devices per page of the tool: default 100, at most 1000 so the result stays small.</summary>
    public static readonly Mcp.McpLimit Page = new(100, 1000);

    /// <summary>At most this many per-file warnings are kept/reported.</summary>
    public const int MaxWarnings = 20;

    private const uint BeckhoffVendorId = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    private static readonly Regex FullRevisionSuffix = new(@"-\d{4}-\d{4}$", RegexOptions.CultureInvariant);
    private static readonly Regex VariantSuffix = new(@"-\d{4}$", RegexOptions.CultureInvariant);

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (string Stamp, EsiCatalogScan Scan)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    static EsiCatalogScanner()
    {
        // Some third-party ESI files declare windows-125x encodings, which
        // .NET (Core) only knows once the code-pages provider is registered.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// The ESI directory: <c>&lt;InstallDir&gt;\Config\Io\EtherCAT</c> via
    /// <see cref="TwinCatPaths.GetInstallDir"/> (registry → %TwinCAT3Dir% →
    /// default), falling back to <see cref="EsiLocator.FindEsiDirectory"/>.
    /// Null when none exists.
    /// </summary>
    public static string? ResolveEsiDirectory()
    {
        string? installDir = TwinCatPaths.GetInstallDir();
        if (installDir is not null)
        {
            string candidate = Path.Combine(installDir, "Config", "Io", "EtherCAT");
            if (Directory.Exists(candidate)) return candidate;
        }
        return EsiLocator.FindEsiDirectory();
    }

    /// <summary>
    /// Builds the tool result: a one-line summary plus indented JSON, or an
    /// <c>[Error] …</c> line. <paramref name="esiDirectory"/> null → <see cref="ResolveEsiDirectory"/>.
    /// </summary>
    public static string ListCatalog(
        string? filter,
        int? limit = null,
        int offset = 0,
        bool latestRevisionOnly = true,
        string? esiDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (Mcp.McpPaging.Validate(limit, offset) is { } invalid) return invalid;

        string? dir = esiDirectory ?? ResolveEsiDirectory();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return "[Error] ESI directory not found" +
                   (string.IsNullOrWhiteSpace(dir) ? " (no TwinCAT 3.1 installation resolved)." : $": '{dir}'.");

        EsiCatalogScan scan = Scan(dir, cancellationToken);
        string trimmedFilter = (filter ?? string.Empty).Trim();
        IReadOnlyList<EsiCatalogDevice> matches = Query(scan.Devices, trimmedFilter, latestRevisionOnly);
        var page = Mcp.McpPaging.Apply(matches, limit, offset, Page);

        var payload = new
        {
            esiDirectory = scan.Directory,
            filter = trimmedFilter,
            latestRevisionOnly,
            filesScanned = scan.FilesScanned,
            filesSkipped = scan.FilesSkipped,
            nonEsiFiles = scan.NonEsiFiles,
            devicesIndexed = scan.Devices.Count,
            cached = scan.FromCache,
            warnings = scan.Warnings,
            devices = page.Items.Select(d => new
            {
                vInfo = d.VInfo,
                type = d.Type,
                name = d.Name,
                productCode = FormatHex(d.ProductCode),
                revisionNo = FormatHex(d.RevisionNo),
                vendorId = d.VendorId,
                vendorName = d.VendorName,
                sourceFile = Path.GetRelativePath(scan.Directory, d.SourceFile),
            }),
        };

        var summary = new StringBuilder();
        summary.Append(CultureInfo.InvariantCulture, $"Found {matches.Count} EtherCAT box type(s)");
        if (trimmedFilter.Length > 0)
            summary.Append(CultureInfo.InvariantCulture, $" matching '{trimmedFilter}'");
        summary.Append(latestRevisionOnly ? " (latest revision only)" : " (all revisions)");
        if (page.Returned == 0 && matches.Count > 0)
            summary.Append(CultureInfo.InvariantCulture, $", none from offset {page.Offset}");
        else if (page.Returned < matches.Count)
            summary.Append(CultureInfo.InvariantCulture, $", returning {page.Offset + 1}–{page.Offset + page.Returned}");
        if (page.HasMore)
            summary.Append(CultureInfo.InvariantCulture, $" (next_offset {page.NextOffset})");
        summary.Append(CultureInfo.InvariantCulture,
            $"; scanned {scan.FilesScanned} ESI file(s), skipped {scan.FilesSkipped} malformed");
        summary.Append(scan.FromCache ? " (cached)." : ".");
        if (trimmedFilter.Length == 0)
            summary.Append(" Pass a filter (e.g. 'EL1008' or 'EK11') to narrow the list.");

        return summary + Environment.NewLine + page.Serialize(payload, JsonOpts, "devices");
    }

    /// <summary>
    /// Filters (case-insensitive substring on Type or vInfo), optionally keeps
    /// only the highest revision per (vendor, product code, revision low word)
    /// — the low word is the Beckhoff variant (e.g. EL3112 vs EL3112-0011) —
    /// and sorts by Type, then revision descending.
    /// </summary>
    public static IReadOnlyList<EsiCatalogDevice> Query(
        IReadOnlyList<EsiCatalogDevice> devices, string? filter, bool latestRevisionOnly)
    {
        IEnumerable<EsiCatalogDevice> q = devices;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            string f = filter.Trim();
            q = q.Where(d => d.Type.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || d.VInfo.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        if (latestRevisionOnly)
        {
            q = q.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
                 .Select(g => g.OrderBy(IsPlaceholderRevision)
                               .ThenByDescending(d => d.RevisionNo ?? 0)
                               .First());
        }

        return q.OrderBy(d => d.Type, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(d => d.RevisionNo ?? 0)
                .ToList();
    }

    /// <summary>
    /// Beckhoff's generic "Beckhoff EtherCAT Terminals.xml" describes many
    /// devices with revision high words ≥ 9990 (e.g. EK1100 #x270B0000 →
    /// EK1100-0000-9995). Those are fallback descriptions, not the newest
    /// product revision, so <c>latestRevisionOnly</c> only picks one when the
    /// group has no real revision. They still appear with
    /// <c>latestRevisionOnly=false</c>.
    /// </summary>
    internal static bool IsPlaceholderRevision(EsiCatalogDevice d) =>
        d.VendorId == BeckhoffVendorId && d.RevisionNo is uint rev && (rev >> 16) >= PlaceholderRevisionHighWord;

    private const uint PlaceholderRevisionHighWord = 9990;

    private static string GroupKey(EsiCatalogDevice d)
    {
        if (d.ProductCode is not uint pc)
            return "type:" + d.Type; // no ProductCode → only the Type text identifies it
        uint lo = (d.RevisionNo ?? 0) & 0xFFFF;
        return string.Create(CultureInfo.InvariantCulture, $"{d.VendorId}:{pc:x8}:{lo:x4}");
    }

    /// <summary>
    /// Scans <paramref name="directory"/> recursively, or returns the cached
    /// result when no *.xml file was added/removed/modified since.
    /// </summary>
    public static EsiCatalogScan Scan(string directory, CancellationToken cancellationToken = default)
    {
        string fullDir = Path.GetFullPath(directory);
        string[] files = EnumerateXmlFiles(fullDir);
        string stamp = ComputeStamp(files);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(fullDir, out var hit) && hit.Stamp == stamp)
                return hit.Scan with { FromCache = true };
        }

        var perFile = new ConcurrentDictionary<string, FileResult>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(
            files,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            },
            file => perFile[file] = ScanFileSafe(file));

        var devices = new List<EsiCatalogDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        int scanned = 0, skipped = 0, nonEsi = 0;

        // Deterministic order (file path) so dedup keeps the same entry every run.
        foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            FileResult r = perFile[file];
            switch (r.Kind)
            {
                case FileKind.Skipped:
                    skipped++;
                    if (warnings.Count < MaxWarnings)
                        warnings.Add($"{Path.GetRelativePath(fullDir, file)}: {r.Error}");
                    continue;
                case FileKind.NotEsi:
                    nonEsi++;
                    continue;
            }

            scanned++;
            foreach (EsiCatalogDevice d in r.Devices)
            {
                string key = string.Create(CultureInfo.InvariantCulture,
                    $"{d.VendorId}|{d.ProductCode}|{d.RevisionNo}|{d.Type}");
                if (seen.Add(key))
                    devices.Add(d);
            }
        }

        var result = new EsiCatalogScan(fullDir, devices, scanned, skipped, nonEsi, warnings, FromCache: false);
        lock (CacheLock)
        {
            Cache[fullDir] = (stamp, result);
        }
        return result;
    }

    /// <summary>Drops every cached scan (tests).</summary>
    internal static void ClearCache()
    {
        lock (CacheLock) { Cache.Clear(); }
    }

    private static string[] EnumerateXmlFiles(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*.xml", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            });
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string ComputeStamp(string[] files)
    {
        long newest = 0;
        foreach (string f in files)
        {
            try
            {
                long t = File.GetLastWriteTimeUtc(f).Ticks;
                if (t > newest) newest = t;
            }
            catch (IOException) { /* vanished between enumerate and stat — the count still changes */ }
            catch (UnauthorizedAccessException) { }
        }
        return string.Create(CultureInfo.InvariantCulture, $"{files.Length}:{newest}");
    }

    private enum FileKind { Esi, NotEsi, Skipped }

    private sealed record FileResult(FileKind Kind, IReadOnlyList<EsiCatalogDevice> Devices, string? Error);

    private static FileResult ScanFileSafe(string file)
    {
        try
        {
            IReadOnlyList<EsiCatalogDevice>? devices = ScanFile(file);
            return devices is null
                ? new FileResult(FileKind.NotEsi, [], null)
                : new FileResult(FileKind.Esi, devices, null);
        }
        catch (XmlException ex) { return new FileResult(FileKind.Skipped, [], "malformed XML: " + ex.Message); }
        catch (IOException ex) { return new FileResult(FileKind.Skipped, [], "unreadable: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { return new FileResult(FileKind.Skipped, [], "access denied: " + ex.Message); }
        catch (ArgumentException ex) { return new FileResult(FileKind.Skipped, [], "unsupported encoding/content: " + ex.Message); }
        catch (InvalidOperationException ex) { return new FileResult(FileKind.Skipped, [], "unreadable: " + ex.Message); }
    }

    /// <summary>
    /// Streams one ESI file. Returns null when the root is not
    /// <c>&lt;EtherCATInfo&gt;</c> (e.g. a module-description file); throws
    /// <see cref="XmlException"/> on malformed XML. Stops reading once the
    /// <c>&lt;Devices&gt;</c> section is done (the rest — Modules, images — is
    /// irrelevant and often huge).
    /// </summary>
    public static IReadOnlyList<EsiCatalogDevice>? ScanFile(string file)
    {
        using XmlReader reader = XmlReader.Create(
            new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan),
            ReaderSettings);

        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "EtherCATInfo")
            return null;

        var devices = new List<EsiCatalogDevice>();
        uint? vendorId = null;
        string? vendorName = null;
        int rootDepth = reader.Depth;
        if (reader.IsEmptyElement) return devices;
        reader.Read();

        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            int rel = reader.Depth - rootDepth;
            if (rel == 1 && reader.LocalName == "Vendor")
            {
                (vendorId, vendorName) = ReadVendor(reader);
                continue;
            }
            if (rel == 1 && reader.LocalName == "Descriptions")
            {
                reader.Read(); // descend into Descriptions
                continue;
            }
            if (rel == 2 && reader.LocalName == "Devices")
            {
                ReadDevices(reader, file, vendorId, vendorName, devices);
                break; // nothing after <Devices> matters
            }

            reader.Skip(); // any other element at any level: skip its subtree
        }

        return devices;
    }

    private static (uint? Id, string? Name) ReadVendor(XmlReader reader)
    {
        uint? id = null;
        string? name = null;
        int depth = reader.Depth;
        if (reader.IsEmptyElement) { reader.Read(); return (id, name); }
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                switch (reader.LocalName)
                {
                    case "Id":
                        id = ParseEsiNumber(reader.ReadElementContentAsString());
                        continue;
                    case "Name" when name is null || reader.GetAttribute("LcId") == "1033":
                        string text = reader.ReadElementContentAsString().Trim();
                        if (text.Length > 0) name = text;
                        continue;
                    default:
                        reader.Skip();
                        continue;
                }
            }
            reader.Read();
        }
        reader.Read(); // past </Vendor>
        return (id, name);
    }

    private static void ReadDevices(
        XmlReader reader, string file, uint? vendorId, string? vendorName, List<EsiCatalogDevice> into)
    {
        int depth = reader.Depth;
        if (reader.IsEmptyElement) return;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                if (reader.LocalName == "Device")
                {
                    EsiCatalogDevice? d = ReadDevice(reader, file, vendorId, vendorName);
                    if (d is not null) into.Add(d);
                }
                else
                {
                    reader.Skip();
                }
                continue;
            }
            reader.Read();
        }
    }

    private static EsiCatalogDevice? ReadDevice(XmlReader reader, string file, uint? vendorId, string? vendorName)
    {
        int depth = reader.Depth;
        if (reader.IsEmptyElement) { reader.Read(); return null; }

        string? type = null;
        uint? productCode = null, revisionNo = null;
        string? name1033 = null, firstName = null;

        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                switch (reader.LocalName)
                {
                    case "Type" when type is null:
                        productCode = ParseEsiNumber(reader.GetAttribute("ProductCode"));
                        revisionNo = ParseEsiNumber(reader.GetAttribute("RevisionNo"));
                        type = reader.ReadElementContentAsString().Trim();
                        continue;
                    case "Name":
                        string? lcId = reader.GetAttribute("LcId");
                        string text = reader.ReadElementContentAsString().Trim();
                        if (text.Length > 0)
                        {
                            firstName ??= text;
                            if (lcId == "1033" && name1033 is null) name1033 = text;
                        }
                        continue;
                    default:
                        reader.Skip(); // Info/Profile/Dictionary/PDOs — the bulk of the file
                        continue;
                }
            }
            reader.Read();
        }
        reader.Read(); // past </Device>

        if (string.IsNullOrEmpty(type)) return null;
        return new EsiCatalogDevice(
            type,
            BuildVInfo(type, revisionNo, vendorId),
            name1033 ?? firstName,
            productCode,
            revisionNo,
            vendorId,
            vendorName,
            file);
    }

    /// <summary>
    /// Revision-exact vInfo per the Beckhoff convention
    /// <c>&lt;Type&gt;-&lt;RevisionNo low word, 4 digits&gt;-&lt;high word, 4 digits&gt;</c>
    /// (e.g. EL1008 + #x00110000 → <c>EL1008-0000-0017</c>; EL3112-0011 +
    /// #x0010000B → <c>EL3112-0011-0016</c>; a Type already in that shape,
    /// e.g. <c>AX5206-0000-0216</c>, is kept). Non-Beckhoff vendors and entries
    /// without a RevisionNo get the bare Type text.
    /// </summary>
    internal static string BuildVInfo(string type, uint? revisionNo, uint? vendorId)
    {
        if (vendorId != BeckhoffVendorId || revisionNo is not uint rev)
            return type;
        if (FullRevisionSuffix.IsMatch(type))
            return type;

        uint lo = rev & 0xFFFF;
        uint hi = rev >> 16;
        return VariantSuffix.IsMatch(type)
            ? string.Create(CultureInfo.InvariantCulture, $"{type}-{hi:D4}")
            : string.Create(CultureInfo.InvariantCulture, $"{type}-{lo:D4}-{hi:D4}");
    }

    /// <summary>ESI HexDecValue: <c>#x0c1d3052</c> / <c>0x…</c> hex, else decimal. Null when unparsable.</summary>
    internal static uint? ParseEsiNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.StartsWith("#x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex)
                ? hex : null;
        }
        if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec))
            return dec;
        // Negative decimals occur in a few third-party files — keep the bit pattern.
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int neg)
            ? unchecked((uint)neg) : null;
    }

    private static string? FormatHex(uint? value) =>
        value is uint v ? string.Create(CultureInfo.InvariantCulture, $"#x{v:X8}") : null;
}
