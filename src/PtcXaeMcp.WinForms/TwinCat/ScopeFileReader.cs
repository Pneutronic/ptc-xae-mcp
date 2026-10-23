using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>Exit code of one export-tool run, or null when it was killed after the timeout.</summary>
internal delegate int? ScopeExportRunner(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout);

/// <summary>
/// Reads and converts a scope recording (.svdx) without the XAE, through the official
/// TC3ScopeExportTool.exe (TE130x Scope View / TF3300 Scope Server) run headless with "silent":
/// "svd=&lt;file&gt;" "target=&lt;file&gt;" silent (Infosys te13xx_tc3_scopeview "Automated export").
/// The .svdx format itself is undocumented and is not parsed here. EXPERIMENTAL: the export CSV layout
/// is known from observation only.
/// </summary>
internal sealed class ScopeFileReader
{
    public const string ExperimentalNote =
        "[Experimental] Read through TC3ScopeExportTool.exe; the export layout is known from observation only.";

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Candidate locations of the export tool; tests replace them.</summary>
    internal Func<IReadOnlyList<string>> ToolCandidates { get; init; } = DefaultToolCandidates;

    internal ScopeExportRunner Run { get; init; } = RunProcess;

    internal TimeSpan Timeout { get; init; } = DefaultTimeout;

    public static IReadOnlyList<string> DefaultToolCandidates()
    {
        string root = TwinCatPaths.GetTwinCatDir()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Beckhoff", "TwinCAT");
        return
        [
            Path.Combine(root, "Functions", "TE130x-Scope-View", "TC3ScopeExportTool.exe"),
            Path.Combine(root, "Functions", "TF3300-Scope-Server", "TC3ScopeExportTool.exe"),
        ];
    }

    public string Read(string path, string? channels, double? fromMs, double? toMs, int maxPoints, bool includeValues)
    {
        if (!File.Exists(path)) return $"[Error] Scope file not found: {path}";
        if (fromMs is { } f && toMs is { } t && t < f) return $"[Error] toMs ({t}) is before fromMs ({f}).";

        string dir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp", "scope-read");
        Directory.CreateDirectory(dir);
        string csv = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            if (Export(path, csv) is { } error) return error;

            ScopePointRequest? request = includeValues
                ? new ScopePointRequest(ParseChannels(channels), fromMs, toMs, maxPoints <= 0 ? ScopeExportCsv.DefaultMaxPoints : maxPoints)
                : null;
            ScopeRecording recording;
            try
            {
                using var reader = new StreamReader(csv);
                recording = ScopeExportCsv.Parse(reader, request);
            }
            catch (FormatException ex) { return $"[Error] The export of {path} is not in the expected layout: {ex.Message}"; }

            return $"{ExperimentalNote}\n{Summary(recording)}\n{Json(path, recording, request)}";
        }
        finally
        {
            try { File.Delete(csv); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public string Convert(string path, string targetPath, bool overwrite)
    {
        if (!File.Exists(path)) return $"[Error] Scope file not found: {path}";
        if (string.IsNullOrEmpty(Path.GetExtension(targetPath)))
            return "[Error] targetPath needs an extension (.csv, .tdms, …): the export tool picks the format from it.";
        if (File.Exists(targetPath) && !overwrite)
            return $"[Error] {targetPath} already exists. Pass overwrite=true to replace it.";
        string? targetDir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (targetDir is not null && !Directory.Exists(targetDir))
            return $"[Error] Target directory does not exist: {targetDir}";

        if (File.Exists(targetPath))
        {
            try { File.Delete(targetPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return $"[Error] Could not replace {targetPath}: {ex.Message}"; }
        }
        if (Export(path, targetPath) is { } error) return error;
        long size = new FileInfo(targetPath).Length;
        return $"{ExperimentalNote}\nConverted {path} → {targetPath} ({size.ToString(CultureInfo.InvariantCulture)} bytes).";
    }

    // Null on success (the target exists), otherwise the [Error] text.
    private string? Export(string source, string target)
    {
        var candidates = ToolCandidates();
        string? exe = candidates.FirstOrDefault(File.Exists);
        if (exe is null)
            return "[Error] TC3ScopeExportTool.exe not found (looked in " + string.Join(", ", candidates) +
                   "). It comes with TE130x Scope View or TF3300 Scope Server.";

        int? exit;
        try { exit = Run(exe, [$"svd={source}", $"target={target}", "silent"], Timeout); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return $"[Error] Could not start {exe}: {ex.Message}"; }

        if (exit is null)
            return $"[Error] The export tool did not finish within {Timeout.TotalSeconds:F0}s and was stopped. " +
                   "A window it may have opened was not answered.";
        if (exit != 0)
            return $"[Error] The export tool exited with code {exit.Value.ToString(CultureInfo.InvariantCulture)} for {source}.";
        if (!File.Exists(target))
            return $"[Error] The export tool exited with code 0 but wrote no file at {target}.";
        return null;
    }

    private static int? RunProcess(string exe, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        if (process.WaitForExit(timeout)) return process.ExitCode;
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        return null;
    }

    private static HashSet<string>? ParseChannels(string? channels)
    {
        if (string.IsNullOrWhiteSpace(channels)) return null;
        return channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Summary(ScopeRecording r)
    {
        string span = r.StartUtc is { } s && r.EndUtc is { } e
            ? $", {s.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z – {e.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z"
            : "";
        return $"Scope recording '{r.Name}': {r.Channels.Count} channel{(r.Channels.Count == 1 ? "" : "s")}{span}.";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Json(string path, ScopeRecording r, ScopePointRequest? request)
    {
        var body = new
        {
            file = path,
            name = r.Name,
            startTimeUtc = r.StartUtc?.ToString("o", CultureInfo.InvariantCulture),
            endTimeUtc = r.EndUtc?.ToString("o", CultureInfo.InvariantCulture),
            timeUnit = "ms since the recording start",
            channels = r.Channels.Select(c => new
            {
                name = c.Name,
                symbolName = c.Meta("SymbolName"),
                comment = c.Meta("SymbolComment"),
                dataType = c.Meta("Data-Type"),
                sampleTimeMs = c.Meta("SampleTime[ms]"),
                netId = c.Meta("NetID"),
                port = c.Meta("Port"),
                scaleFactor = c.Meta("ScaleFactor"),
                offset = c.Meta("Offset"),
                unit = c.Meta("Unit"),
                sampleCount = c.SampleCount,
                firstMs = c.FirstMs,
                lastMs = c.LastMs,
                min = c.Min,
                max = c.Max,
                mean = c.Mean,
                decimated = c.Points is null ? (bool?)null : c.Decimated,
                points = c.Points?.Select(p => new[] { p.TimeMs, p.Value }),
            }),
            window = request is null ? null : new { fromMs = request.FromMs, toMs = request.ToMs, maxPoints = request.MaxPoints },
        };
        return JsonSerializer.Serialize(body, JsonOptions);
    }
}
