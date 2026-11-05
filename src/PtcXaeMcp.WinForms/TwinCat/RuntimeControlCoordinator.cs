using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed class RuntimeControlCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public string BroadcastSearch(string? searchTarget)
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        IReadOnlyList<BroadcastTarget> targets;
        try { targets = session.BroadcastSearch(searchTarget); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        return JsonSerializer.Serialize(targets, JsonOpts);
    }

    public string AddRoute(string netId, string? ipAddr, string? hostName,
                           string? userName, string? password, bool projectRoute)
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(netId))
            return "[Error] amsNetId is required.";
        if (ipAddr is null && hostName is null)
            return "[Error] Either ipAddr or hostName must be provided.";

        try { return session.AddRoute(netId, ipAddr, hostName, userName, password, projectRoute); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }
    }

    public string SetTarget(string netId, string? platform)
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(netId))
            return "[Error] amsNetId is required.";

        try { return session.SetTarget(netId, platform); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }
    }

    public string RestartRuntime()
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        try { return session.RestartRuntime(); }
        catch (Exception ex) { return $"[Error] {ex.Message}.{TwinCatComSession.AccessDeniedHint(ex)}"; }
    }
}
