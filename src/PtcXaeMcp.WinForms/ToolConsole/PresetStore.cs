using System.Text.Json;
using System.Text.Json.Nodes;
using PtcXaeMcp.WinForms.Localization;

namespace PtcXaeMcp.WinForms.ToolConsole;

/// <summary>A saved favourite: a name, the tool and its arguments (JSON object text).</summary>
internal sealed record ToolPreset(string Name, string Tool, string Arguments);

/// <summary>
/// Favourites in <c>%APPDATA%\ptc-xae-mcp\presets.json</c>. Loading never throws: a missing file
/// yields the shipped "Getting started" presets, a corrupt one is moved aside to
/// <c>presets.json.corrupt-&lt;timestamp&gt;</c> and replaced by the defaults.
/// </summary>
internal sealed class PresetStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public PresetStore(string path) => Path = path;

    public string Path { get; }

    /// <summary>Default location under %APPDATA%.</summary>
    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ptc-xae-mcp", "presets.json");

    /// <summary>Presets shipped on first run.</summary>
    public static IReadOnlyList<ToolPreset> GettingStarted() =>
    [
        new(Loc.Get("toolconsole.preset.status"), "twincat_get_status", "{}"),
        new(Loc.Get("toolconsole.preset.openproject"), "twincat_open_project", "{}"),
        new(Loc.Get("toolconsole.preset.listplc"), "twincat_plc_list", "{}"),
        new(Loc.Get("toolconsole.preset.buildplc"), "twincat_build_plc", "{}"),
        new(Loc.Get("toolconsole.preset.errorlist"), "twincat_get_error_list", "{}"),
    ];

    // Set when the file could not be read for a temporary IO/permission reason: it is left as it
    // is and TrySave refuses to overwrite it until a later Load succeeds.
    private bool _unreadable;

    /// <summary>Loads the presets; see the class remarks for the missing/corrupt cases.</summary>
    public IReadOnlyList<ToolPreset> Load() => LoadWithWarning().Presets;

    /// <summary>
    /// Like <see cref="Load"/>, plus a warning for the status line when the file could not be
    /// read (IO or permission problem): the defaults are then in memory only and the file is
    /// neither renamed nor overwritten.
    /// </summary>
    public (IReadOnlyList<ToolPreset> Presets, string? Warning) LoadWithWarning()
    {
        _unreadable = false;
        if (!File.Exists(Path))
        {
            var defaults = GettingStarted();
            TrySave(defaults);
            return (defaults, null);
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject
                       ?? throw new JsonException("root is not an object");
            if (root["presets"] is not JsonArray arr) throw new JsonException("'presets' array missing");
            var list = new List<ToolPreset>();
            foreach (var item in arr)
            {
                if (item is not JsonObject o) throw new JsonException("preset is not an object");
                string? name = (string?)o["name"], tool = (string?)o["tool"];
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(tool))
                    throw new JsonException("preset without name/tool");
                string args = o["arguments"] switch
                {
                    null => "{}",
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    var n => n.ToJsonString(),
                };
                list.Add(new ToolPreset(name, tool, args));
            }
            return (list, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Possibly temporary (file locked, no permission): do not treat it as corruption.
            _unreadable = true;
            return (GettingStarted(),
                Loc.Format("toolconsole.preset.unreadable", ex.Message));
        }
        catch (Exception ex) when (ex is JsonException or InvalidCastException or InvalidOperationException or FormatException)
        {
            BackupCorrupt();
            var defaults = GettingStarted();
            TrySave(defaults);
            return (defaults, Loc.Get("toolconsole.preset.corrupt"));
        }
    }

    /// <summary>Writes the presets; returns false (never throws) when the file cannot be written.</summary>
    public bool TrySave(IReadOnlyList<ToolPreset> presets)
    {
        if (_unreadable) return false;
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var arr = new JsonArray();
            foreach (var p in presets)
            {
                JsonNode? args;
                try { args = JsonNode.Parse(p.Arguments); }
                catch (JsonException) { args = JsonValue.Create(p.Arguments); } // keep unparsable text verbatim
                arr.Add(new JsonObject { ["name"] = p.Name, ["tool"] = p.Tool, ["arguments"] = args });
            }
            var root = new JsonObject { ["presets"] = arr };
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(Options));
            File.Move(tmp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Adds or replaces (same name) a preset and saves; returns the new list. <paramref name="saved"/> is false when the file could not be written.</summary>
    public IReadOnlyList<ToolPreset> Upsert(IReadOnlyList<ToolPreset> current, ToolPreset preset, out bool saved)
    {
        var list = current.Where(p => !string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(preset);
        saved = TrySave(list);
        return list;
    }

    /// <summary>Removes a preset by name and saves; returns the new list. <paramref name="saved"/> is false when the file could not be written.</summary>
    public IReadOnlyList<ToolPreset> Remove(IReadOnlyList<ToolPreset> current, string name, out bool saved)
    {
        var list = current.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        saved = TrySave(list);
        return list;
    }

    private void BackupCorrupt()
    {
        try
        {
            string backup = $"{Path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(Path, backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not move it aside; TrySave will try to overwrite it. Never crash.
        }
    }
}
