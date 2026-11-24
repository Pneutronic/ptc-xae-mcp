using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed class ActiveProjectCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string GetActiveProject()
    {
        bool open = session.HasOpenSolution();
        string? path = open ? session.ActiveSolutionPath() : null;
        string? name = path is null ? null : Path.GetFileNameWithoutExtension(path);

        // Issue #58: the active platform and the target, with a warning when they do not fit. Best effort: a
        // solution without a System Manager (HMI only) or a busy XAE leaves them null.
        ProjectTargetInfo target = open ? ReadTargetOrUnknown() : ProjectTargetInfo.Unknown;
        PlatformFitResult fit = PlatformFit.Check(target);

        return JsonSerializer.Serialize(new
        {
            solutionOpen = open,
            solutionPath = path,
            solutionName = name,
            targetPlatform = target.Platform,
            targetNetId = target.TargetNetId,
            platformWarning = fit.Kind == PlatformFitKind.Ok ? null : fit.Message,
        }, JsonOpts);
    }

    private ProjectTargetInfo ReadTargetOrUnknown()
    {
        try { return session.ReadProjectTarget(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            return ProjectTargetInfo.Unknown;
        }
    }
}
