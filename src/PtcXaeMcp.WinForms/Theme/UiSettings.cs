using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.Theme;

/// <summary>Saved window geometry. <see cref="X"/>..<see cref="Height"/> are the NORMAL bounds
/// (the restore bounds when the window is maximized).</summary>
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>
/// The GUI settings file <c>%APPDATA%\ptc-xae-mcp\ui.json</c> (issue #273):
/// <c>{ "theme": "dark", "rememberWindowPosition": true, "checkForUpdates": false,
/// "window": { "x":…, "y":…, "width":…, "height":…, "maximized":false } }</c>.
/// Backward compatible: an old file with only <c>theme</c> loads with the defaults for the rest;
/// a missing, empty or corrupt file yields the defaults. Unknown keys are ignored.
/// </summary>
internal sealed class UiSettings
{
    public bool DarkTheme { get; set; }

    public bool RememberWindowPosition { get; set; } = true;

    public WindowPlacement? Window { get; set; }

    /// <summary>Opt-in: ask github.com for the latest release at start (<c>checkForUpdates</c>, default off).</summary>
    public bool CheckForUpdates { get; set; }

    public static UiSettings Parse(string? json)
    {
        var s = new UiSettings();
        if (string.IsNullOrWhiteSpace(json)) return s;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return s;

            if (root.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String)
                s.DarkTheme = string.Equals(t.GetString(), "dark", StringComparison.OrdinalIgnoreCase);

            if (root.TryGetProperty("rememberWindowPosition", out var r)
                && r.ValueKind is JsonValueKind.True or JsonValueKind.False)
                s.RememberWindowPosition = r.GetBoolean();

            if (root.TryGetProperty("checkForUpdates", out var u)
                && u.ValueKind is JsonValueKind.True or JsonValueKind.False)
                s.CheckForUpdates = u.GetBoolean();

            if (root.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object
                && TryInt(w, "x", out int x) && TryInt(w, "y", out int y)
                && TryInt(w, "width", out int width) && TryInt(w, "height", out int height)
                && width > 0 && height > 0)
            {
                bool max = w.TryGetProperty("maximized", out var m) && m.ValueKind == JsonValueKind.True;
                s.Window = new WindowPlacement(x, y, width, height, max);
            }
        }
        catch (JsonException)
        {
            return new UiSettings();
        }
        return s;
    }

    private static bool TryInt(JsonElement o, string name, out int value)
    {
        value = 0;
        return o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
    }

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["theme"] = DarkTheme ? "dark" : "light",
            ["rememberWindowPosition"] = RememberWindowPosition,
            ["checkForUpdates"] = CheckForUpdates,
        };
        if (Window is { } w)
            root["window"] = new JsonObject
            {
                ["x"] = w.X, ["y"] = w.Y, ["width"] = w.Width, ["height"] = w.Height, ["maximized"] = w.Maximized,
            };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static UiSettings Load(string path)
    {
        // Best effort, like before #273: an unreadable settings file must never stop the app.
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new UiSettings(); }
        catch (Exception) { return new UiSettings(); }
    }

    /// <summary>Best effort: a settings file that cannot be written must never break the app.</summary>
    public void Save(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson());
        }
        catch (Exception) { }
    }

    /// <summary>Minimum overlap with a screen for a saved position to count as visible.</summary>
    public static readonly Size MinVisible = new(100, 50);

    /// <summary>
    /// Maps a saved window rectangle onto the currently connected screens so a window saved on a
    /// monitor that is gone (or a changed resolution) never opens off-screen. The screen with the
    /// largest overlap wins; with less than <see cref="MinVisible"/> overlap everywhere it falls
    /// back to <paramref name="workingAreas"/>[0] (the primary screen). The size is limited to the
    /// chosen area and the position shifted into it.
    /// </summary>
    /// <returns>The clamped bounds, or <c>null</c> when no screen is given.</returns>
    public static Rectangle? ClampToScreens(Rectangle saved, IReadOnlyList<Rectangle> workingAreas, Size minimumSize)
    {
        if (workingAreas.Count == 0) return null;

        Rectangle best = workingAreas[0];
        long bestArea = -1;
        foreach (var area in workingAreas)
        {
            var overlap = Rectangle.Intersect(saved, area);
            long a = overlap.IsEmpty ? 0 : (long)overlap.Width * overlap.Height;
            if (a > bestArea) { bestArea = a; best = area; }
        }
        var visible = Rectangle.Intersect(saved, best);
        if (visible.Width < MinVisible.Width || visible.Height < MinVisible.Height)
            best = workingAreas[0];

        int width = Math.Min(Math.Max(saved.Width, minimumSize.Width), best.Width);
        int height = Math.Min(Math.Max(saved.Height, minimumSize.Height), best.Height);
        int x = Math.Clamp(saved.X, best.Left, best.Right - width);
        int y = Math.Clamp(saved.Y, best.Top, best.Bottom - height);
        return new Rectangle(x, y, width, height);
    }
}
