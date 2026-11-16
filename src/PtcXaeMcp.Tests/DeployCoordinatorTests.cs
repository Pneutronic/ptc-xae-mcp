using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>twincat_deploy: the order of the steps and every abort rule, on a fake chain.</summary>
public sealed class DeployCoordinatorTests
{
    private const string Net = "127.0.0.1.1.1";

    private const string BuildOk =
        "Build succeeded (failedProjects=0).\nCompiler: 0 errors, 2 warnings\nArtifact: C:\\p\\PLC.compileinfo (2026-11-16 10:00:00Z, 3s old)";

    private const string BuildFailed =
        "Build failed (failedProjects=1).\nReason: 1 project(s) failed to build\n[ERROR] (POUs\\MAIN.TcPOU:12) C0077: Unknown type: 'FB_Gone'";

    private const string ActivatedWithRestart =
        "Activated: Configuration activated and TwinCAT restarted (ActivateConfiguration + StartRestartTwinCAT): system Run after 12 s. Login modal: none. XAE dialogs answered: none.";

    /// <summary>Everything the chain calls, recorded in order; the answers are set per test.</summary>
    private sealed class FakeOps : IDeployOps
    {
        public List<string> Calls { get; } = [];
        public bool InLock { get; set; }
        public List<string> CallsOutsideLock { get; } = [];

        public ActivationTarget? Target { get; set; } = new(Net, IsLocal: true);
        public string BuildText { get; set; } = BuildOk;
        public string ActivateText { get; set; } = ActivatedWithRestart;
        /// <summary>The structured flag the chain decides on; the text alone no longer counts.</summary>
        public bool ActivateRestarted { get; set; } = true;
        public TargetStateReading SystemState { get; set; } = TargetStateReading.Of("Run");
        public Queue<TargetStateReading> PlcStates { get; } = new();
        public TargetStateReading PlcStateDefault { get; set; } = TargetStateReading.Of("Run");
        public string StartPlcText { get; set; } = "The PLC runtime was started.";
        public string LifeSignText { get; set; } = """{ "success": true, "met": true, "timedOut": false, "elapsedMs": 120 }""";
        public Exception? BuildThrows { get; set; }

        private void Log(string call) { Calls.Add(call); if (!InLock) CallsOutsideLock.Add(call); }

        public ActivationTarget? ResolveTarget() { Log("target"); return Target; }

        public string Build(DeployRequest request)
        {
            Log("build");
            if (BuildThrows is not null) throw BuildThrows;
            return BuildText;
        }

        public ActivationOutcome Activate(DeployRequest request) { Log("activate"); return new ActivationOutcome(ActivateText, ActivateRestarted); }
        public TargetStateReading ReadSystemState(string netId) { Log("system"); return SystemState; }

        public TargetStateReading ReadPlcState(string netId, int port)
        {
            Log("plc");
            return PlcStates.Count > 0 ? PlcStates.Dequeue() : PlcStateDefault;
        }

        public string StartPlc(string netId, int port) { Log("start"); return StartPlcText; }

        public string WaitForLifeSign(IReadOnlyList<string> conditions, int timeoutMs, string netId, int port, CancellationToken ct)
        {
            Log("lifesign");
            return LifeSignText;
        }
    }

    private static (string Text, FakeOps Ops) Run(Action<FakeOps>? arrange = null, DeployRequest? request = null)
    {
        var ops = new FakeOps();
        arrange?.Invoke(ops);
        // The lock is a flag, like the dispatcher's: the steps run inside it, the life sign outside.
        var coordinator = new DeployCoordinator(ops, body =>
        {
            ops.InLock = true;
            try { return body(); }
            finally { ops.InLock = false; }
        }, sleep: _ => { });
        return (coordinator.Deploy(request ?? new DeployRequest(), default), ops);
    }

    private static JsonElement Json(string text)
    {
        int start = text.StartsWith('{') ? 0 : text.IndexOf("\n{", StringComparison.Ordinal) + 1;
        return JsonDocument.Parse(text[start..]).RootElement.Clone();
    }

    private static string[] Steps(string text) =>
        Json(text).GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetString()!).ToArray();

    // ---- the whole chain ----------------------------------------------------------------------------------------

    [Fact]
    public void AllGreen_RunsTheStepsInOrder_AndAnswersTheStepTable()
    {
        var (text, ops) = Run();

        Assert.False(McpErrors.IsError(text), text);
        var json = Json(text);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(Net, json.GetProperty("amsNetId").GetString());
        Assert.Equal(["build", "target", "activate", "verify_target", "plc_state"], Steps(text));
        Assert.All(json.GetProperty("steps").EnumerateArray(), s =>
        {
            Assert.True(s.GetProperty("ok").GetBoolean());
            Assert.True(s.GetProperty("durationMs").GetInt64() >= 0);
            Assert.False(string.IsNullOrEmpty(s.GetProperty("message").GetString()));
        });
        Assert.Equal(["build", "target", "activate", "system", "plc"], ops.Calls);
    }

    [Fact]
    public void TheSteps_RunInOneLock_AndTheLifeSignAfterIt()
    {
        var (text, ops) = Run(request: new DeployRequest { LifeSign = ["MAIN.nCycle changed"] });

        Assert.False(McpErrors.IsError(text), text);
        Assert.Equal(["lifesign"], ops.CallsOutsideLock);
        Assert.Equal("lifesign", ops.Calls[^1]);
    }

    [Fact]
    public void ABuildWithWarnings_PassesOn_AndLibraryWarningsAreCarried()
    {
        var (text, _) = Run(o => o.BuildText = "[Warn] Build result not conclusive: 1 library reference not satisfied — PLC: Tc2_Foo 1.0.0 missing.\n" + BuildOk);

        var json = Json(text);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Contains("Tc2_Foo", Assert.Single(json.GetProperty("warnings").EnumerateArray()).GetString());
    }

    // ---- abort rules: build ------------------------------------------------------------------------------------

    [Fact]
    public void BuildErrors_NeverActivate()
    {
        var (text, ops) = Run(o => o.BuildText = BuildFailed);

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'build'", text, StringComparison.Ordinal);
        Assert.Contains("FB_Gone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("activate", ops.Calls);
        Assert.DoesNotContain("system", ops.Calls);
        Assert.DoesNotContain("start", ops.Calls);
        var json = Json(text);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("build", json.GetProperty("failedStep").GetString());
        Assert.Contains("activate", json.GetProperty("notRun").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void ARefusedBuild_NeverActivates()
    {
        var (text, ops) = Run(o => o.BuildText = "[Error] 2 PLC files changed on disk outside the XAE since the project was loaded: MAIN.TcPOU.");

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'build'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("activate", ops.Calls);
    }

    [Fact]
    public void ABuildTextWithoutSuccess_IsNotTakenForOne()
    {
        var (text, ops) = Run(o => o.BuildText = "Compiler: no summary line in this build's output");

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'build'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("activate", ops.Calls);
    }

    [Fact]
    public void AFailedBuild_LeavesTheKeyFree_ButAFailureAfterTheActivationDoesNot()
    {
        var (beforeActivation, _) = Run(o => o.BuildText = BuildFailed);
        var (afterActivation, _) = Run(o => o.SystemState = TargetStateReading.Of("Config"));

        // The request key (RequestReceipts) is freed by an answer that says nothing was done.
        Assert.Null(RequestReceipts.Classify(beforeActivation, isError: false));
        Assert.Equal(ReceiptOutcome.Error, RequestReceipts.Classify(afterActivation, isError: false));
        Assert.Equal(ReceiptOutcome.Ok, RequestReceipts.Classify(Run().Text, isError: false));
    }

    [Fact]
    public void ABuildThatThrows_IsAFailedStep()
    {
        var (text, ops) = Run(o => o.BuildThrows = new InvalidOperationException("The XAE lost its solution."));

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'build'", text, StringComparison.Ordinal);
        Assert.Contains("lost its solution", text, StringComparison.Ordinal);
        Assert.DoesNotContain("activate", ops.Calls);
    }

    [Fact]
    public void AnXaeThatRejectsTheCall_IsAFailedStep_NotACrash()
    {
        var (text, ops) = Run(o => o.BuildThrows = new XaeBlockedException("The XAE rejected the call.", "Title", null));

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'build'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("activate", ops.Calls);
    }

    // ---- abort rules: expectSymbols ---------------------------------------------------------------------------

    [Fact]
    public void AMissingExpectedSymbol_NeverActivates()
    {
        var (text, ops) = Run(
            o => o.BuildText = BuildOk + "\n[Error] Expected symbols: 1 of 2 missing from the compiled PLC: MAIN.fbGone. Only objects a task reaches are compiled.",
            new DeployRequest { ExpectSymbols = ["MAIN.fbAxis", "MAIN.fbGone"] });

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'expect_symbols'", text, StringComparison.Ordinal);
        Assert.Contains("MAIN.fbGone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("activate", ops.Calls);
        Assert.Equal(["build", "expect_symbols"], Steps(text));
    }

    [Fact]
    public void ExpectedSymbolsThatAreThere_AreAStepOfTheirOwn()
    {
        var (text, _) = Run(
            o => o.BuildText = BuildOk + "\nExpected symbols: all 1 found in the compiled PLC.\nFound: MAIN.fbAxis (symbol)",
            new DeployRequest { ExpectSymbols = ["MAIN.fbAxis"] });

        Assert.Equal(["build", "expect_symbols", "target", "activate", "verify_target", "plc_state"], Steps(text));
        Assert.True(Json(text).GetProperty("success").GetBoolean());
    }

    [Fact]
    public void ExpectedSymbols_WithoutAReportInTheBuildText_AreNotTakenForChecked()
    {
        var (text, ops) = Run(request: new DeployRequest { ExpectSymbols = ["MAIN.fbAxis"] });

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'expect_symbols'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("activate", ops.Calls);
    }

    // ---- abort rules: activation --------------------------------------------------------------------------------

    [Fact]
    public void AFailedActivation_IsNotFollowedByAnyRunStep()
    {
        var (text, ops) = Run(o => o.ActivateText = "[Error] Activation failed: The login modal offered no online change.");

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'activate'", text, StringComparison.Ordinal);
        Assert.Equal(["build", "target", "activate"], ops.Calls);
        // The activation was started: the answer does not claim that the target is untouched.
        Assert.DoesNotContain("Nothing was done", text, StringComparison.Ordinal);
        Assert.Equal(["verify_target", "plc_state"], Json(text).GetProperty("notRun").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void ARestartThatTheActivationDoesNotReport_IsNotAnActivationThatTookEffect()
    {
        var (text, ops) = Run(o =>
        {
            o.ActivateText = "Activated: Configuration saved to registry only (ActivateConfiguration, no restart).";
            o.ActivateRestarted = false;
        });

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'activate'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("system", ops.Calls);
        Assert.DoesNotContain("start", ops.Calls);
    }

    [Fact]
    public void TheRestartFlag_NotTheWording_Decides()
    {
        // A text that still says "TwinCAT restarted" but without the flag (e.g. reworded or misreported) does not pass…
        var (refused, refusedOps) = Run(o => o.ActivateRestarted = false);
        Assert.StartsWith("[Error] twincat_deploy stopped at step 'activate'", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("start", refusedOps.Calls);

        // …and a reworded success text with the flag set does.
        var (passed, _) = Run(o => o.ActivateText = "Activated: the configuration is live (new wording).");
        Assert.DoesNotContain("[Error]", passed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Config")]
    [InlineData("Stop")]
    [InlineData("Error")]
    public void ASystemThatDoesNotReadRunAfterTheActivation_NeverGetsAPlcStart(string state)
    {
        var (text, ops) = Run(o => o.SystemState = TargetStateReading.Of(state));

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'verify_target'", text, StringComparison.Ordinal);
        Assert.Contains("cannot be shown to be effective", text, StringComparison.Ordinal);
        Assert.DoesNotContain("start", ops.Calls);
        Assert.DoesNotContain("plc", ops.Calls);
        Assert.DoesNotContain("lifesign", ops.Calls);
    }

    [Fact]
    public void ATargetThatDoesNotAnswer_NeverGetsAPlcStart()
    {
        var (text, ops) = Run(o => o.SystemState = TargetStateReading.Unreachable("timeout"));

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'verify_target'", text, StringComparison.Ordinal);
        Assert.Contains("timeout", text, StringComparison.Ordinal);
        Assert.DoesNotContain("start", ops.Calls);
    }

    [Fact]
    public void WithoutARestart_TheRegistryOnlyIsAWarning_AndTheTargetOnlyHasToAnswer()
    {
        var (text, _) = Run(
            o =>
            {
                o.ActivateText = "Activated: Configuration saved to registry only (ActivateConfiguration, no restart). The running PLC keeps the PREVIOUS code.";
                o.ActivateRestarted = false;
                o.SystemState = TargetStateReading.Of("Config");
                o.PlcStateDefault = TargetStateReading.Of("Stop");
            },
            new DeployRequest { Restart = false, StartPlc = false });

        var json = Json(text);
        Assert.Contains("restart=false", Assert.Single(json.GetProperty("warnings").EnumerateArray()).GetString());
        // The PLC is not Run in Config mode: with startPlc=false that is a failed check, not a silent pass.
        Assert.StartsWith("[Error] twincat_deploy stopped at step 'plc_state'", text, StringComparison.Ordinal);
    }

    // ---- the PLC ------------------------------------------------------------------------------------------------

    [Fact]
    public void APlcThatReadsRunLater_NeedsNoStart()
    {
        var (text, ops) = Run(o =>
        {
            o.PlcStates.Enqueue(TargetStateReading.Of("Stop"));
            o.PlcStates.Enqueue(TargetStateReading.Of("Run"));
        });

        Assert.True(Json(text).GetProperty("success").GetBoolean(), text);
        Assert.DoesNotContain("start", ops.Calls);
    }

    [Fact]
    public void APlcThatStaysStopped_IsStarted_ThenChecked()
    {
        // Never reads Run: started once, still Stop afterwards.
        var (text, ops) = Run(o => o.PlcStateDefault = TargetStateReading.Of("Stop"));

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'plc_state'", text, StringComparison.Ordinal);
        Assert.Equal(1, ops.Calls.Count(c => c == "start"));
        Assert.Equal(["build", "target", "activate", "verify_target", "plc_start", "plc_state"], Steps(text));
    }

    [Fact]
    public void APlcStartedAndRunning_IsAStepOfItsOwn()
    {
        // 1 read and 30 polls while stopped, then Run after the start.
        var (text, ops) = Run(o =>
        {
            for (int i = 0; i < 31; i++) o.PlcStates.Enqueue(TargetStateReading.Of("Stop"));
        });

        Assert.True(Json(text).GetProperty("success").GetBoolean(), text);
        Assert.Equal(["build", "target", "activate", "verify_target", "plc_start", "plc_state"], Steps(text));
        Assert.Equal(1, ops.Calls.Count(c => c == "start"));
    }

    [Fact]
    public void WithoutStartPlc_AStoppedPlcIsAFailedCheck_AndNothingIsStarted()
    {
        var (text, ops) = Run(o => o.PlcStateDefault = TargetStateReading.Of("Stop"), new DeployRequest { StartPlc = false });

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'plc_state'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("start", ops.Calls);
        Assert.DoesNotContain("lifesign", ops.Calls);
    }

    [Fact]
    public void APlcStartThatFails_EndsTheRun()
    {
        var (text, ops) = Run(o =>
        {
            o.PlcStateDefault = TargetStateReading.Of("Stop");
            o.StartPlcText = "[Error] WriteControl to 127.0.0.1.1.1:851 was refused: ADS error 1861";
        });

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'plc_start'", text, StringComparison.Ordinal);
        Assert.Equal(1, ops.Calls.Count(c => c == "start"));
        Assert.DoesNotContain("lifesign", ops.Calls);
    }

    // ---- the life sign ------------------------------------------------------------------------------------------

    [Fact]
    public void ALifeSignThatHolds_IsTheLastStep()
    {
        var (text, ops) = Run(request: new DeployRequest { LifeSign = ["MAIN.nCycle changed"] });

        Assert.Equal(["build", "target", "activate", "verify_target", "plc_state", "life_sign"], Steps(text));
        Assert.Equal("lifesign", ops.Calls[^1]);
        Assert.True(Json(text).GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData("""{ "success": true, "met": false, "timedOut": true, "elapsedMs": 10000 }""")]
    [InlineData("[Error] A symbol is missing: MAIN.nCycle")]
    [InlineData("not json at all")]
    public void ALifeSignThatDoesNotHold_IsAFailedDeploy(string answer)
    {
        var (text, _) = Run(o => o.LifeSignText = answer, new DeployRequest { LifeSign = ["MAIN.nCycle changed"] });

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'life_sign'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was done", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLifeSign_IsNotWaitedForAfterAnEarlierFailure()
    {
        var (_, ops) = Run(o => o.SystemState = TargetStateReading.Of("Config"), new DeployRequest { LifeSign = ["MAIN.nCycle changed"] });

        Assert.DoesNotContain("lifesign", ops.Calls);
    }

    // ---- before anything runs -----------------------------------------------------------------------------------

    [Fact]
    public void ATargetThatNoAdsReadCanConfirm_StopsBeforeTheActivation()
    {
        var (text, ops) = Run(o => o.Target = null);

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'target'", text, StringComparison.Ordinal);
        Assert.Equal(["build", "target"], ops.Calls);
        Assert.Contains("Nothing was done", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(9, 10_000, 851, false, null)]
    [InlineData(1, 0, 851, false, null)]
    [InlineData(1, 300_001, 851, false, null)]
    [InlineData(1, 10_000, 0, false, null)]
    [InlineData(1, 10_000, 851, true, "PLC")]
    public void InvalidArguments_AreRefusedBeforeAnythingRuns(int conditions, int timeout, int port, bool solution, string? project)
    {
        var request = new DeployRequest
        {
            LifeSign = Enumerable.Range(0, conditions).Select(i => $"MAIN.n{i} changed").ToArray(),
            LifeSignTimeoutMs = timeout,
            PlcPort = port,
            BuildSolution = solution,
            PlcProjectName = project,
        };

        var (text, ops) = Run(request: request);

        Assert.StartsWith("[Error]", text, StringComparison.Ordinal);
        Assert.Contains("Nothing was done", text, StringComparison.Ordinal);
        Assert.Empty(ops.Calls);
    }

    // ---- the tools in the catalog: one question, one key -------------------------------------------------------

    [Fact]
    public void TheTool_IsARuntimeAction_ThatAsksAHumanAndTakesARequestKey()
    {
        var tool = ToolCatalog.Find("twincat_deploy");
        Assert.NotNull(tool);
        Assert.Equal(Toolset.Build, tool.Toolset);
        Assert.Equal(AccessLevel.Runtime, tool.Level);
        Assert.Equal(TargetRule.Project, tool.Target);
        Assert.Equal(ConfirmClass.Always, tool.Confirm);
        Assert.True(tool.Receipts);
        Assert.False(tool.ReadOnly);
        Assert.Contains("requestKey", ReservedArguments.For("twincat_deploy").Select(r => r.Name));
        Assert.Contains("confirmationToken", ReservedArguments.For("twincat_deploy").Select(r => r.Name));
    }

    [Fact]
    public void TheReleaseCheck_IsReadOnly_AndNeverAsksOrKeepsAReceipt()
    {
        var tool = ToolCatalog.Find("twincat_release_check");
        Assert.NotNull(tool);
        Assert.Equal(AccessLevel.Read, tool.Level);
        Assert.True(tool.ReadOnly);
        Assert.Equal(ConfirmClass.Never, tool.Confirm);
        Assert.False(tool.Receipts);
        Assert.Empty(ReservedArguments.For("twincat_release_check"));
    }

    [Fact]
    public async Task AHumanIsAskedOnce_ForTheWholeWorkflow()
    {
        var registry = new ConfirmationRegistry();
        var asker = new CountingAsker();
        var sut = new HumanConfirmation(
            () => new AccessPolicy(AccessLevel.Runtime, AccessPolicy.ParseList(null), null, "test", Confirm: ConfirmMode.All),
            () => "", () => [], registry);
        var arguments = JsonSerializer.SerializeToElement(new { buildSolution = true, lifeSign = new[] { "MAIN.nCycle changed" } })
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        CallToolResult? answer = await sut.CheckAsync("twincat_deploy", arguments, asker, default);

        Assert.Null(answer); // the human said yes: the workflow runs
        string question = Assert.Single(asker.Questions);
        Assert.Contains("twincat_deploy", question, StringComparison.Ordinal);

        // The steps call coordinators, never tools: nothing in the chain can reach the filter again.
        var ops = new FakeOps();
        new DeployCoordinator(ops, body => body(), _ => { }).Deploy(new DeployRequest(), default);
        Assert.Single(asker.Questions);
    }

    private sealed class CountingAsker : IConfirmAsker
    {
        public bool CanAsk => true;
        public List<string> Questions { get; } = [];

        public Task<ConfirmAnswer> AskAsync(string message, CancellationToken cancellationToken)
        {
            Questions.Add(message);
            return Task.FromResult(ConfirmAnswer.Yes);
        }
    }
}
