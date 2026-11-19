using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>What a TF2000 server says about itself before or after a publish (read through its WebSocket API).</summary>
internal sealed record HmiPublishTargetState(string? ProjectName, string? ProjectVersion, string? License, string? User, string? Error)
{
    public static HmiPublishTargetState Failed(string error) => new(null, null, null, null, error);
}

/// <summary>The part of a publish that runs on the dispatcher: the popup and the publish call.</summary>
/// <param name="ProfileFile">What happened to Properties/tchmipublish.config.json, into which the XAE saves the
/// profile with the password: "unchanged", "restored" or a problem.</param>
internal sealed record HmiPublishRun(string ProjectName, HmiPublishOutcome? Outcome, string? Refusal, string? Confirmation,
    string? ProfileFile = null);

// hmi-tf2000: publish the HMI project to a TF2000 runtime server. The tool reads the target first (reachable, login,
// which project runs there), the dispatcher asks the engineer and publishes through the Automation Interface, then the
// tool reads the target again: only the target reporting this project counts as success.
internal sealed partial class HmiCoordinator
{
    internal const int MinPublishTimeoutSeconds = 30, MaxPublishTimeoutSeconds = 1800;

    internal static JsonArray TargetStateCommands() =>
        [new JsonObject { ["symbol"] = "GetServerInformation" }, new JsonObject { ["symbol"] = "Diagnostics" },
         new JsonObject { ["symbol"] = "GetCurrentUser" }];

    /// <summary>Reads project name/version, license state and user from <paramref name="targetUrl"/>.</summary>
    public static async Task<HmiPublishTargetState> ReadPublishTargetAsync(string targetUrl, string? user, string? password,
        bool trustServerCertificate, int timeoutMs, CancellationToken ct)
    {
        var (answer, error) = await HmiServerApi.QueryAsync(targetUrl, user, password, trustServerCertificate, TargetStateCommands(),
            timeoutMs, ct).ConfigureAwait(false);
        return answer is null ? HmiPublishTargetState.Failed(error!) : TargetState(answer);
    }

    internal const string NoRightsPrefix = "the server does not let this user read GetServerInformation";

    internal static HmiPublishTargetState TargetState(JsonObject answer)
    {
        JsonNode? info = HmiServerApi.ReadValue(answer, "GetServerInformation");
        if (info is null)
            return HmiPublishTargetState.Failed(NoRightsPrefix + " — pass user and passwordVariable of a user who may read it " +
                                                "(e.g. an administrator).");
        return new HmiPublishTargetState(info["projectName"]?.ToString(), info["projectVersion"]?.ToString(),
            HmiServerApi.ReadValue(answer, "Diagnostics")?["LICENSE"]?["STATE"]?.ToString(),
            HmiServerApi.ReadValue(answer, "GetCurrentUser")?["name"]?.ToString(), null);
    }

    /// <summary>A publish profile saved in Properties/tchmipublish.config.json: its target and user.</summary>
    internal sealed record HmiPublishProfileInfo(string Name, string Url, string? User, bool HasPassword);

    /// <summary>The saved profile <paramref name="profileName"/> of the project (dispatcher), or why there is none.</summary>
    public (HmiPublishProfileInfo? Profile, string? Error) FindPublishProfile(string? hmiProject, string profileName)
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (null, error);
        var (project, resolveError) = ResolveProject(hmiProject, requireReady: false);
        if (project is null) return (null, resolveError);
        string path = Path.Combine(project.ProjectDirectory, "Properties", PublishConfigFile);
        byte[]? bytes = ReadIfExists(path);
        return ParsePublishProfile(bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes), profileName, project.Name);
    }

    internal static (HmiPublishProfileInfo? Profile, string? Error) ParsePublishProfile(string? json, string profileName, string projectName)
    {
        var names = new List<string>();
        try
        {
            if (json is not null && JsonNode.Parse(json.TrimStart('﻿')) is JsonArray list)
            {
                foreach (JsonNode? p in list)
                {
                    string? name = p?["profileName"]?.ToString();
                    if (name is null) continue;
                    names.Add(name);
                    if (!name.Equals(profileName, StringComparison.Ordinal)) continue;
                    string? url = p!["tcHmiDestinationUrl"]?.ToString();
                    if (string.IsNullOrWhiteSpace(url) && p["tcHmiServerHost"]?.ToString() is { Length: > 0 } host)
                        url = $"{(p["tcHmiUseTLS"]?.GetValue<bool>() == true ? "https" : "http")}://{host}:{p["tcHmiServerPort"]}";
                    if (string.IsNullOrWhiteSpace(url))
                        return (null, $"[Error] publish profile '{profileName}' of '{projectName}' has no target URL.");
                    return (new HmiPublishProfileInfo(name, url.Trim(), p["tcHmiServerUserName"]?.ToString(),
                        !string.IsNullOrEmpty(p["encryptedTcHmiServerPassword"]?.ToString())), null);
                }
            }
        }
        catch (JsonException ex) { return (null, $"[Error] Properties/{PublishConfigFile} of '{projectName}' is not valid JSON: {ex.Message}"); }
        return (null, $"[Error] '{projectName}' has no publish profile '{profileName}'" +
                      (names.Count == 0 ? " (none saved)." : $"; saved: {string.Join(", ", names)}.") +
                      " Create it once in the XAE (Publish dialog of the HMI project, save the password there).");
    }

    /// <summary>Popup, then <c>ITcHmiProject.Publish</c>. Runs on the dispatcher; the target was read before.</summary>
    public HmiPublishRun PublishProject(string? hmiProject, string profileName, string targetUrl, string? profileUser,
        int timeoutSeconds, int confirmTimeoutSeconds, HmiPublishTargetState before)
    {
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return new("", null, timeoutError, null);
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return new("", null, error, null);
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return new("", null, resolveError, null);

        string current = before.Error is not null
            ? $"(not readable: {HmiServerApi.ShortText(before.Error)})"
            : string.IsNullOrEmpty(before.ProjectName)
                ? "no project published (empty server)"
                : $"runs project '{before.ProjectName}'{(string.IsNullOrEmpty(before.ProjectVersion) ? "" : " " + before.ProjectVersion)}";
        string next = $"project '{project.Name}', publish profile '{profileName}' (user '{profileUser ?? "-"}')";
        var request = new HmiWriteRequest(HmiWriteKind.Publish, project.Name, project.ProjectDirectory, targetUrl,
            Domain: null, NewValue: next, CurrentValue: current, WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return new(project.Name, null, refusal.Replace("Nothing was written.", "Nothing was published.", StringComparison.Ordinal), null);

        var target = new HmiPublishTarget(profileName, targetUrl);
        string profilePath = Path.Combine(project.ProjectDirectory, "Properties", PublishConfigFile);
        byte[]? profileBefore = ReadIfExists(profilePath);
        HmiPublishOutcome result;
        try { result = session.PublishHmiProject(project.Name, target, TimeSpan.FromSeconds(timeoutSeconds)); }
        catch (Exception ex) when (IsHmiFailure(ex))
        {
            RestoreProfileFile(profilePath, profileBefore);
            return new(project.Name, null, WithDialogHint($"[Error] Publish failed: {ex.Message}"), null);
        }
        return new(project.Name, result, null, Describe(outcome), RestoreProfileFile(profilePath, profileBefore));
    }

    internal const string PublishConfigFile = "tchmipublish.config.json";

    /// <summary>"[type 0, level 1] Copy …" from the JSON text the XAE passes to the publish callback; other text unchanged.</summary>
    internal static string PublishMessage(string? data)
    {
        string text = (data ?? "").Trim();
        if (!text.StartsWith('{')) return text;
        try
        {
            if (JsonNode.Parse(text) is JsonObject o && o["Message"] is { } message)
                return $"[type {o["MessageType"]}, level {o["ImportanceLevel"]}] {message}";
        }
        catch (JsonException) { /* not JSON after all */ }
        return text;
    }

    /// <summary>Messages worth showing in the summary: anything but plain progress (type 0, level ≤ 1) or naming a problem;
    /// without such, the last three.</summary>
    internal static IReadOnlyList<string> NotablePublishMessages(IReadOnlyList<string> messages)
    {
        static bool Plain(string m) => m.StartsWith("[type 0, level 0]", StringComparison.Ordinal)
                                       || m.StartsWith("[type 0, level 1]", StringComparison.Ordinal);
        static bool Problem(string m) => m.Contains("error", StringComparison.OrdinalIgnoreCase)
                                         || m.Contains("fail", StringComparison.OrdinalIgnoreCase)
                                         || m.Contains("denied", StringComparison.OrdinalIgnoreCase);
        var notable = messages.Where(m => !Plain(m) || Problem(m)).ToList();
        return notable.Count > 0 ? notable.TakeLast(5).ToList() : messages.TakeLast(3).ToList();
    }

    private static byte[]? ReadIfExists(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The XAE saves the publish profile, password included, into Properties/tchmipublish.config.json. The project must
    /// not keep it: the file goes back to its bytes before the publish (or away when there was none).
    /// </summary>
    internal static string RestoreProfileFile(string path, byte[]? before)
    {
        try
        {
            byte[]? now = ReadIfExists(path);
            if (now is null && before is null) return "unchanged (no profile file)";
            if (now is not null && before is not null && now.AsSpan().SequenceEqual(before)) return "unchanged";
            if (before is null) File.Delete(path);
            else File.WriteAllBytes(path, before);
            return $"restored (the XAE had rewritten Properties/{PublishConfigFile} during the publish)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"NOT restored: {ex.Message} — Properties/{PublishConfigFile} may hold the publish profile with the password";
        }
    }

    /// <summary>The tool's answer from the target before, the publish run and the target after.</summary>
    internal static (string Summary, string Json) PublishReport(string targetUrl, HmiPublishTargetState before, HmiPublishRun run,
        HmiPublishTargetState? after)
    {
        if (run.Refusal is not null) return (run.Refusal, "{}");
        HmiPublishOutcome o = run.Outcome!;
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = run.ProjectName,
            targetUrl,
            before = new { before.ProjectName, before.ProjectVersion, before.License, before.User },
            publish = new
            {
                o.Started, o.StillRunning, o.Completed, o.ResultCode, o.SubmissionId, o.LastError, seconds = Math.Round(o.Seconds, 1),
                messages = (o.Messages ?? []).TakeLast(50),
            },
            after = after is null ? null : new { after.ProjectName, after.ProjectVersion, after.License, after.User, after.Error },
            profileFile = run.ProfileFile,
            confirmation = run.Confirmation,
        }, JsonOpts);

        string profile = run.ProfileFile is { } pf && !pf.StartsWith("unchanged", StringComparison.Ordinal) ? $" Profile file: {pf}." : "";
        string said = o.Messages is { Count: > 0 } m
            ? $" The XAE said ({m.Count} message(s)): " + string.Join(" | ", NotablePublishMessages(m).Select(HmiServerApi.ShortText)) + "."
            : " The XAE sent no progress message.";
        if (!o.Started)
            return ($"[Error] the XAE did not start the publish of '{run.ProjectName}' to {targetUrl}: {o.LastError ?? "no error text"}. " +
                    "Nothing was published." + said + profile, json);
        if (o.StillRunning)
            return ($"[Warn] the publish of '{run.ProjectName}' to {targetUrl} was still running after {o.Seconds:0} s. " +
                    "Check later with twincat_hmi_server_diagnostics on the target." + said + profile, json);

        string call = $"result code {(o.ResultCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")}, " +
                      $"completed {(o.Completed?.ToString() ?? "unknown")}, {o.Seconds:0.0} s";
        if (after?.Error is { } readError)
            return ($"[Warn] the XAE finished the publish of '{run.ProjectName}' to {targetUrl} ({call}), but the target could not be " +
                    $"read afterwards: {readError}" + said + profile, json);
        if (!string.Equals(after?.ProjectName, run.ProjectName, StringComparison.Ordinal))
            return ($"[Error] the XAE finished the publish ({call}), but {targetUrl} reports project '{after?.ProjectName}', not " +
                    $"'{run.ProjectName}'. {o.LastError}".TrimEnd() + said + profile, json);

        string same = string.Equals(before.ProjectName, run.ProjectName, StringComparison.Ordinal)
            ? " It ran the same project before, so the name alone does not prove that every file is new."
            : "";
        return ($"Published '{run.ProjectName}' to {targetUrl} — {run.Confirmation}, {call}; verified: the target now reports project " +
                $"'{after!.ProjectName}'{(string.IsNullOrEmpty(after.ProjectVersion) ? "" : " " + after.ProjectVersion)} " +
                $"(before: {(string.IsNullOrEmpty(before.ProjectName) ? "none" : "'" + before.ProjectName + "'")}).{same}{profile}", json);
    }
}
