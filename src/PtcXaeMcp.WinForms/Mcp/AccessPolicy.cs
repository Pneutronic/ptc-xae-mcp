using System.Text.Json;
using System.Text.Json.Nodes;
using PtcXaeMcp.WinForms.Diagnostics;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// What the MCP may do: an access level (<c>read</c>, <c>engineer</c>, <c>runtime</c>) and NetId rules for runtime
/// actions. Set in the window (saved to <see cref="FileName"/>) or by environment variables; when both are set the
/// stricter wins. No tool can read or change it — only <c>twincat_get_status</c> shows it.
/// </summary>
internal sealed record AccessPolicy(
    AccessLevel Level,
    IReadOnlySet<string> BlockedNetIds,
    IReadOnlySet<string>? AllowedNetIds,
    string Source)
{
    public const string LevelEnvVar = "PTC_XAE_MCP_ACCESS";
    public const string BlockedEnvVar = "PTC_XAE_MCP_BLOCKED_NETIDS";
    public const string AllowedEnvVar = "PTC_XAE_MCP_ALLOWED_NETIDS";
    public const string FileName = "policy.json";

    /// <summary>In the NetId lists: the local system — the AMS router and the User Mode Runtime instances on this machine.</summary>
    public const string LocalKeyword = "local";

    // Declared before Unrestricted: static fields initialise in text order.
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    /// <summary>Without any setting: everything allowed, as before the policy existed.</summary>
    public static readonly AccessPolicy Unrestricted = new(AccessLevel.Runtime, Empty, null, "default");

    public bool HasNetIdRules => BlockedNetIds.Count > 0 || AllowedNetIds is not null;

    public static string Name(AccessLevel level) => level.ToString().ToLowerInvariant();

    public static bool TryParseLevel(string? text, out AccessLevel level)
    {
        level = AccessLevel.Runtime;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Enum.TryParse(text.Trim(), ignoreCase: true, out level) && Enum.IsDefined(level)
               && !int.TryParse(text.Trim(), out _);
    }

    /// <summary>NetIds separated by ';' or ','; <c>local</c> kept as the keyword, everything else trimmed.</summary>
    public static IReadOnlySet<string> ParseList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Empty
            : text.Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(Normalize)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string netId) =>
        string.Equals(netId.Trim(), LocalKeyword, StringComparison.OrdinalIgnoreCase) ? LocalKeyword : netId.Trim();

    /// <summary>The policy the environment variables set, or null when none is set. An invalid level is read as <c>read</c>.</summary>
    public static AccessPolicy? FromEnvironment(Func<string, string?> getEnv)
    {
        string? level = getEnv(LevelEnvVar);
        string? blocked = getEnv(BlockedEnvVar);
        string? allowed = getEnv(AllowedEnvVar);
        if (string.IsNullOrWhiteSpace(level) && string.IsNullOrWhiteSpace(blocked) && string.IsNullOrWhiteSpace(allowed))
            return null;
        var parsedLevel = string.IsNullOrWhiteSpace(level) ? AccessLevel.Runtime
            : TryParseLevel(level, out var l) ? l : AccessLevel.Read;
        return new AccessPolicy(parsedLevel, ParseList(blocked),
            string.IsNullOrWhiteSpace(allowed) ? null : ParseList(allowed), "environment");
    }

    /// <summary>The stricter of two policies: the lower level, every blocked NetId of both, only NetIds both allow.</summary>
    public static AccessPolicy Merge(AccessPolicy? a, AccessPolicy? b)
    {
        if (a is null) return b ?? Unrestricted;
        if (b is null) return a;
        var level = (AccessLevel)Math.Min((int)a.Level, (int)b.Level);
        var blocked = a.BlockedNetIds.Union(b.BlockedNetIds, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string>? allowed = (a.AllowedNetIds, b.AllowedNetIds) switch
        {
            (null, null) => null,
            (null, var only) => only,
            (var only, null) => only,
            var (x, y) => x.Intersect(y, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
        return new AccessPolicy(level, blocked, allowed, $"{a.Source} + {b.Source}");
    }

    // ---- file ---------------------------------------------------------------------------------------------

    /// <summary><c>%LOCALAPPDATA%\ptc-xae-mcp\policy.json</c>. The file tools may never touch it (PathPolicy).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ptc-xae-mcp", FileName);

    /// <summary>The policy in the file, or null when there is none. A file that cannot be read is <c>read</c> only.</summary>
    public static AccessPolicy? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AccessPolicy(AccessLevel.Read, Empty, null, $"window (policy.json unreadable: {ex.Message})");
        }
    }

    /// <summary>
    /// <c>{ "access": "engineer", "blockedNetIds": ["10.1.2.3.1.1"], "allowedNetIds": ["local"] }</c>; a missing
    /// <c>allowedNetIds</c> means no allow list. A file that is not this JSON makes the policy <c>read</c>: a broken
    /// restriction must not turn into no restriction.
    /// </summary>
    public static AccessPolicy Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new AccessPolicy(AccessLevel.Read, Empty, null, "window (policy.json is not an object)");

            var level = AccessLevel.Runtime;
            if (root.TryGetProperty("access", out var a) && a.ValueKind == JsonValueKind.String
                && !TryParseLevel(a.GetString(), out level))
                level = AccessLevel.Read;

            return new AccessPolicy(level, ReadList(root, "blockedNetIds") ?? Empty, ReadList(root, "allowedNetIds"), "window");
        }
        catch (JsonException ex)
        {
            return new AccessPolicy(AccessLevel.Read, Empty, null, $"window (policy.json invalid: {ex.Message})");
        }
    }

    private static IReadOnlySet<string>? ReadList(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return null;
        return list.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
            .Select(e => Normalize(e.GetString()!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["access"] = Name(Level),
            ["blockedNetIds"] = new JsonArray(BlockedNetIds.Order().Select(n => (JsonNode)n).ToArray()),
        };
        if (AllowedNetIds is not null)
            root["allowedNetIds"] = new JsonArray(AllowedNetIds.Order().Select(n => (JsonNode)n).ToArray());
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }

    // ---- decisions ----------------------------------------------------------------------------------------

    /// <summary>
    /// Why <paramref name="tool"/> may not run, or null when it may. <paramref name="target"/> resolves the
    /// target system's NetId for a runtime action (null when it cannot be told), <paramref name="localNetIds"/> the
    /// NetIds that count as <c>local</c>. <paramref name="warning"/> carries a note for an allowed call whose target
    /// could not be checked.
    /// </summary>
    public string? Deny(ToolInfo tool, Func<string?> target, Func<IReadOnlyCollection<string>> localNetIds, out string? warning)
    {
        warning = null;
        if (tool.AlwaysAllowed) return null;

        if (tool.Level > Level)
            return $"[Error] Access policy '{Name(Level)}' blocks {tool.Name} (needs '{Name(tool.Level)}'). " +
                   $"Set the access level in the ptc-xae-mcp window (Settings) or with {LevelEnvVar}.";

        if (tool.Level != AccessLevel.Runtime || !HasNetIdRules || tool.Target is TargetRule.None or TargetRule.CheckedEarlier)
            return null;

        string? netId = tool.Target == TargetRule.Local ? LocalKeyword : target();
        if (netId is null)
        {
            if (AllowedNetIds is not null)
                return $"[Error] Access policy: the target system of {tool.Name} cannot be determined and only " +
                       $"{Describe(AllowedNetIds)} are allowed. Nothing was done.";
            warning = $"[Warn] Access policy: the target system of {tool.Name} could not be determined, so the blocked " +
                      "NetIds were not checked for this call.";
            return null;
        }

        var locals = localNetIds();
        bool isLocal = netId == LocalKeyword || locals.Contains(netId, StringComparer.OrdinalIgnoreCase);
        string shown = netId == LocalKeyword ? "the local system" : netId;

        if (BlockedNetIds.Contains(netId) || (isLocal && BlockedNetIds.Contains(LocalKeyword)))
            return $"[Error] Access policy blocks runtime actions on {shown} ({tool.Name}). Reading stays allowed. " +
                   $"Change the blocked NetIds in the ptc-xae-mcp window (Settings) or {BlockedEnvVar}.";

        if (AllowedNetIds is not null && !AllowedNetIds.Contains(netId) && !(isLocal && AllowedNetIds.Contains(LocalKeyword)))
            return $"[Error] Access policy allows runtime actions only on {Describe(AllowedNetIds)}, not on {shown} " +
                   $"({tool.Name}). Change the allowed NetIds in the ptc-xae-mcp window (Settings) or {AllowedEnvVar}.";

        return null;
    }

    private static string Describe(IReadOnlySet<string> netIds) =>
        netIds.Count == 0 ? "no system" : string.Join(", ", netIds.Order());

    public string Summary()
    {
        string text = $"access {Name(Level)}";
        if (BlockedNetIds.Count > 0) text += $", blocked NetIds: {Describe(BlockedNetIds)}";
        if (AllowedNetIds is not null) text += $", runtime actions only on: {Describe(AllowedNetIds)}";
        return $"{text} (from {Source})";
    }
}

/// <summary>
/// The policy in force: the window's (policy.json) merged with the environment's, read once at start and again
/// when the window saves. <see cref="Changed"/> fires after every change so the tool lists can be refreshed.
/// </summary>
internal sealed class AccessPolicyState
{
    private readonly string _path;
    private readonly AccessPolicy? _environment;
    private AccessPolicy? _window;

    public AccessPolicyState(string path, Func<string, string?> getEnv)
    {
        _path = path;
        _environment = AccessPolicy.FromEnvironment(getEnv);
        _window = AccessPolicy.Load(path);
        Current = AccessPolicy.Merge(_window, _environment);
    }

    public AccessPolicy Current { get; private set; }

    /// <summary>The window's own part, for the Settings dialog.</summary>
    public AccessPolicy Window => _window ?? AccessPolicy.Unrestricted;

    /// <summary>The environment's part, shown read-only in the Settings dialog; null when no variable is set.</summary>
    public AccessPolicy? Environment => _environment;

    public string FilePath => _path;

    public event Action? Changed;

    /// <summary>Saves the window's part and applies it. Throws when the file cannot be written: the change must not look done.</summary>
    public void SetWindowPolicy(AccessPolicy policy)
    {
        var window = policy with { Source = "window" };
        window.Save(_path);
        _window = window;
        Current = AccessPolicy.Merge(_window, _environment);
        HostLog.Shared.Info($"Access policy changed in the window: {Current.Summary()}.");
        Changed?.Invoke();
    }
}

/// <summary>The NetIds that count as <c>local</c> in the access policy: the AMS router's, the loopback, the UmRT instances'.</summary>
internal static class LocalNetIds
{
    public static IReadOnlyCollection<string> Collect(Func<IReadOnlyList<string>> umrInstanceNetIds)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LocalAmsNetId.LoopbackNetId,
            LocalAmsNetIdResolver.Shared.Resolve().NetId,
        };
        foreach (var id in umrInstanceNetIds()) ids.Add(id);
        return ids;
    }
}
