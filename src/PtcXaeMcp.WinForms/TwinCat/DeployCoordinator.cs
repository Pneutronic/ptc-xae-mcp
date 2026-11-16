using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>What <c>twincat_deploy</c> was asked to do.</summary>
internal sealed record DeployRequest
{
    public string? PlcProjectName { get; init; }
    /// <summary>Build the whole solution (PLC code and IO mapping) instead of the PLC project only.</summary>
    public bool BuildSolution { get; init; }
    public bool IgnoreStaleFiles { get; init; }
    public IReadOnlyList<string>? ExpectSymbols { get; init; }

    /// <summary>Restart TwinCAT into the new configuration (the activation call confirms the restart dialog).</summary>
    public bool Restart { get; init; } = true;
    public bool AutoGenerateLicenses { get; init; }
    public bool AutoConfirmLogin { get; init; }
    public LoginMode LoginMode { get; init; } = LoginMode.OnlineChange;
    public bool UpdateBootProject { get; init; } = true;
    public bool AllowDownloadFallback { get; init; }
    public int? RunTimeoutSeconds { get; init; }

    /// <summary>Put a PLC runtime that does not read Run after the activation into Run.</summary>
    public bool StartPlc { get; init; } = true;
    public int PlcPort { get; init; } = SystemModeSwitch.FirstPlcPort;

    /// <summary>Conditions of <c>twincat_ads_wait_until</c> that prove the PLC program runs; empty skips the step.</summary>
    public IReadOnlyList<string>? LifeSign { get; init; }
    public int LifeSignTimeoutMs { get; init; } = 10_000;
}

/// <summary>One row of the step table <c>twincat_deploy</c> answers with.</summary>
internal sealed record DeployStep(string Step, bool Ok, long DurationMs, string Message);

/// <summary>
/// The actions of the deploy chain, each one a call that an individual tool makes today. Everything but
/// <see cref="WaitForLifeSign"/> runs under the dispatcher lock (see <see cref="DeployCoordinator"/>). A fake in the tests.
/// </summary>
internal interface IDeployOps
{
    /// <summary>The target the activation reaches and the ADS reads ask; null when the project names none that an ADS read could confirm.</summary>
    ActivationTarget? ResolveTarget();

    /// <summary>The build's result text (<c>twincat_build_plc</c> / <c>twincat_build_solution</c>), with the expectSymbols report when asked for.</summary>
    string Build(DeployRequest request);

    /// <summary>The result text of <c>twincat_activate_config</c> with the activation parameters of the request.</summary>
    ActivationOutcome Activate(DeployRequest request);

    TargetStateReading ReadSystemState(string netId);
    TargetStateReading ReadPlcState(string netId, int port);

    /// <summary>The result text of <c>twincat_plc_start</c>.</summary>
    string StartPlc(string netId, int port);

    /// <summary>The result text of <c>twincat_ads_wait_until</c>; called without the dispatcher lock.</summary>
    string WaitForLifeSign(IReadOnlyList<string> conditions, int timeoutMs, string netId, int port, CancellationToken cancellationToken);
}

/// <summary>
/// <c>twincat_deploy</c>: build, optional symbol check, activation, target and PLC checks and an optional life-sign
/// wait, in a fixed order with fixed abort rules. A step that fails ends the run: nothing after it is started, so a
/// build with errors is never activated, and a configuration that cannot be shown to be active is never followed by a
/// PLC start. Every attempted step is a row of the answer; steps not reached are named.
/// <para>
/// All steps up to the PLC state run in one dispatcher call (no other tool can slip in between the build and the
/// activation); the life-sign wait runs after it has been released, as <c>twincat_ads_wait_until</c> does.
/// </para>
/// </summary>
internal sealed class DeployCoordinator(
    IDeployOps ops, Func<Func<DeployCoordinator.DeployRun>, DeployCoordinator.DeployRun>? runLocked = null, Action<TimeSpan>? sleep = null)
{
    /// <summary>How long a PLC runtime gets to read Run before the deploy puts it there (and again after that).</summary>
    internal static readonly TimeSpan PlcRunGrace = SystemModeSwitch.PlcStartGrace;

    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal const int MaxLifeSignConditions = 8;

    /// <summary>The steps in the order they run.</summary>
    internal static class Names
    {
        public const string Target = "target";
        public const string Build = "build";
        public const string ExpectSymbols = "expect_symbols";
        public const string Activate = "activate";
        public const string VerifyTarget = "verify_target";
        public const string PlcStart = "plc_start";
        public const string PlcState = "plc_state";
        public const string LifeSign = "life_sign";
    }

    private static readonly string[] Order =
        [Names.Build, Names.ExpectSymbols, Names.Target, Names.Activate, Names.VerifyTarget, Names.PlcStart, Names.PlcState, Names.LifeSign];

    /// <summary>What one run produced so far; the locked part hands it to the part after the lock.</summary>
    internal sealed class DeployRun
    {
        public List<DeployStep> Steps { get; } = [];
        public List<string> Warnings { get; } = [];
        public string? Failed { get; set; }
        public string? NetId { get; set; }
        /// <summary>The activation was started: from here on the target may have been changed, whatever the outcome.</summary>
        public bool ActivationAttempted { get; set; }

        /// <summary>The expectSymbols report that closes the build's text (empty when none was asked for).</summary>
        public string ExpectReport { get; set; } = string.Empty;
    }

    public string Deploy(DeployRequest request, CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid) return invalid;

        DeployRun run = Locked(() => RunLocked(request, cancellationToken));
        if (run.Failed is null && run.NetId is not null && request.LifeSign is { Count: > 0 })
            Step(run, Names.LifeSign, () => LifeSign(request, run.NetId, cancellationToken));
        return Format(request, run);
    }

    private DeployRun Locked(Func<DeployRun> body) => runLocked is null ? body() : runLocked(body);

    internal static string? Validate(DeployRequest request)
    {
        if (request.LifeSign is { Count: > MaxLifeSignConditions })
            return $"[Error] lifeSign takes at most {MaxLifeSignConditions} conditions (those of twincat_ads_wait_until). Nothing was done.";
        if (request.LifeSign is { Count: > 0 } && (request.LifeSignTimeoutMs < 1 || request.LifeSignTimeoutMs > SignalLimits.MaxWaitTimeoutMs))
            return $"[Error] lifeSignTimeoutMs must be 1..{SignalLimits.MaxWaitTimeoutMs.ToString(CultureInfo.InvariantCulture)}. Nothing was done.";
        if (request.PlcPort is < 1 or > 65535)
            return "[Error] adsPort must be an AMS port, e.g. 851. Nothing was done.";
        if (request.BuildSolution && !string.IsNullOrWhiteSpace(request.PlcProjectName))
            return "[Error] plcProjectName names one PLC project: it cannot be combined with buildSolution=true. Nothing was done.";
        return null;
    }

    // ---- the steps under the lock -------------------------------------------------------------------------------

    private DeployRun RunLocked(DeployRequest request, CancellationToken ct)
    {
        var run = new DeployRun();

        if (!Step(run, Names.Build, () => BuildStep(request, run, ct))) return run;
        if (request.ExpectSymbols is { Count: > 0 } && !Step(run, Names.ExpectSymbols, () => ExpectStep(run))) return run;

        // After the build, which saves the project: the target is read from the saved project, so a target set in the XAE
        // a moment ago (twincat_set_target) is the one the state checks ask.
        ActivationTarget? target = null;
        if (!Step(run, Names.Target, () =>
            {
                target = ops.ResolveTarget();
                return target is null
                    ? Fail("the open solution names no target that an ADS read can confirm (no solution open, or a target that is no AMS NetId)")
                    : Pass($"target {target.Describe()}");
            }))
            return run;
        run.NetId = target!.NetId;

        run.ActivationAttempted = true;
        if (!Step(run, Names.Activate, () => ActivateStep(request, run))) return run;
        if (!Step(run, Names.VerifyTarget, () => VerifyTargetStep(request, run))) return run;

        Step(run, Names.PlcState, () => PlcStateStep(request, run));
        return run;
    }

    private (bool Ok, string Message) BuildStep(DeployRequest request, DeployRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string text = ops.Build(request);
        var (buildPart, expectPart) = SplitBuildText(text);
        run.ExpectReport = expectPart;
        string first = FirstLine(buildPart);

        if (McpErrors.IsError(buildPart) || buildPart.Contains("Build failed", StringComparison.Ordinal))
            return Fail(Digest(buildPart));
        if (!buildPart.Contains("Build succeeded", StringComparison.Ordinal))
            return Fail("the build result does not say 'Build succeeded': " + Digest(buildPart));

        foreach (string line in buildPart.Split('\n').Select(l => l.Trim()))
            if (line.StartsWith("[Warn]", StringComparison.Ordinal)) run.Warnings.Add(Truncate(line, 300));
        string compiler = buildPart.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Compiler:", StringComparison.Ordinal)) ?? "";
        return Pass(compiler.Length > 0 ? $"{first} {compiler}" : first);
    }

    private static (bool Ok, string Message) ExpectStep(DeployRun run)
    {
        string part = run.ExpectReport;
        if (part.Length == 0)
            return Fail("the build result carries no expectSymbols report, so the names were not checked");
        string first = FirstLine(part);
        return first.StartsWith("[Error]", StringComparison.Ordinal) ? Fail(Digest(part)) : Pass(first);
    }

    private (bool Ok, string Message) ActivateStep(DeployRequest request, DeployRun run)
    {
        var outcome = ops.Activate(request);
        string text = outcome.Text;
        if (McpErrors.IsError(text)) return Fail(Digest(text));
        if (!text.StartsWith("Activated", StringComparison.Ordinal))
            return Fail("the activation did not report 'Activated': " + Digest(text));

        if (request.Restart)
        {
            // The activation call restarts only after the configuration was saved without a refused dialog, and sets
            // Restarted only when the target's system state read Run again. Anything else is not "took effect".
            if (!outcome.Restarted)
                return Fail("the configuration was saved but the activation does not report a restart of TwinCAT into it: " + Digest(text));
            return Pass(Truncate(text, 400));
        }

        run.Warnings.Add("restart=false: the configuration was only saved to the registry; the running PLC keeps its previous code until TwinCAT restarts or the application is downloaded.");
        return Pass(Truncate(text, 400));
    }

    private (bool Ok, string Message) VerifyTargetStep(DeployRequest request, DeployRun run)
    {
        var state = ops.ReadSystemState(run.NetId!);
        if (state.Error is not null)
            return Fail($"the target {run.NetId} does not answer over ADS (port {ActivationTarget.SystemServicePort}): {state.Error}. The activation cannot be shown to be effective");
        if (request.Restart && !state.IsRun)
            return Fail($"the target's TwinCAT system reads {state} after the activation (Run expected). The activation cannot be shown to be effective");
        return Pass($"the TwinCAT system of {run.NetId} reads {state}");
    }

    private (bool Ok, string Message) PlcStateStep(DeployRequest request, DeployRun run)
    {
        string netId = run.NetId!;
        var plc = WaitForPlc(netId, request.PlcPort, PlcRunGrace);
        if (plc.IsRun) return Pass($"the PLC runtime on port {request.PlcPort} reads Run");

        if (!request.StartPlc)
            return Fail($"the PLC runtime on port {request.PlcPort} reads {plc} (Run expected; startPlc=false)");

        if (!Step(run, Names.PlcStart, () =>
            {
                string text = ops.StartPlc(netId, request.PlcPort);
                return McpErrors.IsError(text) ? Fail($"the PLC runtime read {plc} and could not be started: {Digest(text)}") : Pass(Truncate(text, 400));
            }))
            return Fail("the PLC runtime was not started (see plc_start)");

        plc = WaitForPlc(netId, request.PlcPort, PlcRunGrace);
        return plc.IsRun
            ? Pass($"the PLC runtime on port {request.PlcPort} was {plc.State ?? "not Run"} before the start and reads Run now")
            : Fail($"the PLC runtime on port {request.PlcPort} still reads {plc} {PlcRunGrace.TotalSeconds:F0} s after the start");
    }

    private TargetStateReading WaitForPlc(string netId, int port, TimeSpan grace)
    {
        var plc = ops.ReadPlcState(netId, port);
        // Counted polls, not a clock: a read that blocks on a silent target still ends after the polls it allows.
        int polls = (int)(grace / PollInterval);
        for (int i = 0; i < polls && !plc.IsRun; i++)
        {
            (sleep ?? Thread.Sleep)(PollInterval);
            plc = ops.ReadPlcState(netId, port);
        }
        return plc;
    }

    // ---- the step after the lock --------------------------------------------------------------------------------

    private (bool Ok, string Message) LifeSign(DeployRequest request, string netId, CancellationToken ct)
    {
        string text = ops.WaitForLifeSign(request.LifeSign!, request.LifeSignTimeoutMs, netId, request.PlcPort, ct);
        if (McpErrors.IsError(text)) return Fail(Digest(text));
        try
        {
            using var doc = JsonDocument.Parse(text);
            bool met = doc.RootElement.TryGetProperty("met", out var m) && m.ValueKind == JsonValueKind.True;
            long elapsed = doc.RootElement.TryGetProperty("elapsedMs", out var e) && e.TryGetInt64(out long ms) ? ms : 0;
            return met
                ? Pass($"{string.Join(" and ", request.LifeSign!)} held after {elapsed} ms")
                : Fail($"the life sign ({string.Join("; ", request.LifeSign!)}) did not hold within {request.LifeSignTimeoutMs} ms");
        }
        catch (JsonException)
        {
            return Fail("the life-sign wait answered something that is not its JSON result: " + Digest(text));
        }
    }

    // ---- the answer ---------------------------------------------------------------------------------------------

    private static string Format(DeployRequest request, DeployRun run)
    {
        string[] attempted = run.Steps.Select(s => s.Step).ToArray();
        var notRun = Order.Where(s => !attempted.Contains(s, StringComparer.Ordinal) && Applies(s, request, run)).ToList();
        var steps = run.Steps.Select(s => new { step = s.Step, ok = s.Ok, durationMs = s.DurationMs, message = s.Message }).ToArray();

        if (run.Failed is null)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                amsNetId = run.NetId,
                steps,
                warnings = run.Warnings.ToArray(),
            }, JsonOpts);
        }

        var failed = run.Steps.Last(s => s.Step == run.Failed);
        string untouched = run.ActivationAttempted ? "" : " Nothing was done on the target system.";
        string head = $"[Error] twincat_deploy stopped at step '{run.Failed}': {failed.Message}.{untouched}" +
                      (notRun.Count == 0 ? "" : $" Not run: {string.Join(", ", notRun)}.");
        return head + "\n" + JsonSerializer.Serialize(new
        {
            success = false,
            failedStep = run.Failed,
            amsNetId = run.NetId,
            steps,
            notRun,
            warnings = run.Warnings.ToArray(),
        }, JsonOpts);
    }

    // The steps an aborted run did not reach, without the ones this request never asks for.
    private static bool Applies(string step, DeployRequest request, DeployRun run) => step switch
    {
        Names.ExpectSymbols => request.ExpectSymbols is { Count: > 0 },
        Names.PlcStart => false, // only ever a consequence of plc_state
        Names.LifeSign => request.LifeSign is { Count: > 0 },
        _ => true,
    };

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static (bool Ok, string Message) Pass(string message) => (true, message);

    private static (bool Ok, string Message) Fail(string message) => (false, message);

    /// <summary>Runs one step and records its row; an exception is a failed step, not a crash of the chain.</summary>
    private static bool Step(DeployRun run, string name, Func<(bool Ok, string Message)> body)
    {
        var watch = Stopwatch.StartNew();
        bool ok;
        string message;
        try
        {
            (ok, message) = body();
        }
        catch (OperationCanceledException)
        {
            run.Steps.Add(new DeployStep(name, false, watch.ElapsedMilliseconds, "cancelled before the step finished"));
            run.Failed ??= name;
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Includes an XAE that rejected the call until its deadline. A call held by a dialog is no exception in a step:
            // the dispatcher answers [Blocked] for the whole run while the body keeps going.
            (ok, message) = Fail(ComCall.Describe(ex));
        }
        run.Steps.Add(new DeployStep(name, ok, watch.ElapsedMilliseconds, message));
        if (!ok && run.Failed is null) run.Failed = name;
        return ok;
    }

    /// <summary>Build text split into the build proper and the report of expectSymbols (the tail), which is empty when none was asked.</summary>
    internal static (string Build, string Expect) SplitBuildText(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int at = Array.FindIndex(lines, l =>
            l.StartsWith("Expected symbols:", StringComparison.Ordinal)
            || l.StartsWith("[Error] Expected symbols", StringComparison.Ordinal));
        return at < 0
            ? (text, string.Empty)
            : (string.Join('\n', lines.Take(at)), string.Join('\n', lines.Skip(at)));
    }

    private static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

    /// <summary>The first lines of a result text on one line, so the step table stays readable.</summary>
    private static string Digest(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("---- Logger", StringComparison.Ordinal)).Take(8);
        string joined = string.Join(" | ", lines);
        return Truncate(joined.StartsWith("[Error]", StringComparison.Ordinal) ? joined[7..].TrimStart() : joined, 600);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
