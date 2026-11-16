namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// The deploy chain on the live session: the coordinators of the single tools, called directly (never through the tool
/// layer, so the human confirmation, the access gate and the request key of <c>twincat_deploy</c> are not asked
/// again for each step). <see cref="WaitForLifeSign"/> takes the dispatcher lock itself, only while it registers its
/// notifications, so it must be called after the locked steps.
/// </summary>
internal sealed class DeployOps(ITwinCatSession session, Func<Func<string>, string>? runLocked = null) : IDeployOps
{
    public ActivationTarget? ResolveTarget()
    {
        string? projectTarget = session.ProjectTargetNetId();
        if (projectTarget is null) return null; // no solution, or a .tsproj that cannot be read
        return ActivationTarget.Resolve(projectTarget, LocalAmsNetIdResolver.Shared.Resolve());
    }

    public string Build(DeployRequest request)
    {
        var coordinator = new BuildCoordinator(session);
        var outcome = request.BuildSolution
            ? coordinator.BuildSolutionDetailed(request.IgnoreStaleFiles, request.ExpectSymbols)
            : coordinator.BuildPlcDetailed(request.PlcProjectName, request.IgnoreStaleFiles, request.ExpectSymbols);
        return outcome.Summary;
    }

    public string Activate(DeployRequest request) =>
        new BuildCoordinator(session).ActivateConfig(
            request.Restart, request.AutoGenerateLicenses, request.AutoConfirmLogin, request.LoginMode,
            request.UpdateBootProject, request.AllowDownloadFallback, request.RunTimeoutSeconds);

    public TargetStateReading ReadSystemState(string netId)
    {
        using var reader = TargetStateReader.Open(netId);
        return reader.Read();
    }

    public TargetStateReading ReadPlcState(string netId, int port)
    {
        using var reader = TargetStateReader.Open(netId, port);
        return reader.Read();
    }

    public string StartPlc(string netId, int port) => new PlcControlCoordinator(session).PlcStart(netId, port);

    public string WaitForLifeSign(IReadOnlyList<string> conditions, int timeoutMs, string netId, int port, CancellationToken cancellationToken) =>
        new SignalTestbenchCoordinator(session, runLocked)
            .WaitUntil(conditions, combine: "all", timeoutMs, netId, port, cancellationToken);
}
