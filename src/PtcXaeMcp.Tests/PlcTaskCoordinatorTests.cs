using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// twincat_plc_task_create: refusals before the logout and before anything is created, the priority choice, the order of
/// the steps with their read-backs, and partial failures that name what already exists.
/// </summary>
public sealed class PlcTaskCoordinatorTests
{
    private const string Project = "TIPC^Plc^Plc Project";

    private static FakeTwinCatSession OpenFake()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.RealtimeTasks.Add(new RealtimeTaskInfo("PlcTask", "TIRT^PlcTask", 100000, 20));
        fake.PlcTaskReferences.Add(new PlcTaskReferenceInfo("PlcTask", $"{Project}^PlcTask", "TIRT^PlcTask"));
        return fake;
    }

    private static string Create(FakeTwinCatSession fake, string name = "FastTask", double cycleTimeMs = 10,
        int? priority = null, string? plc = null, string? program = null) =>
        new PlcTaskCoordinator(fake).Create(name, cycleTimeMs, priority, plc, program);

    private static bool Changed(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c == "LogoutPlc()" || c.StartsWith("Create", StringComparison.Ordinal)
                              || c.StartsWith("Set", StringComparison.Ordinal) || c == "SaveAll()");

    // ---- refusals ----------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("1Task")]
    [InlineData("Fast Task")]
    [InlineData("PROGRAM")]
    public void InvalidName_IsRefused(string name)
    {
        var fake = OpenFake();

        string result = Create(fake, name: name);

        result.Should().StartWith("[Error] Task name");
        Changed(fake).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonPositiveCycleTime_IsRefused(double ms)
    {
        var fake = OpenFake();

        Create(fake, cycleTimeMs: ms).Should().StartWith("[Error] cycleTimeMs must be greater than 0");
        Changed(fake).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.00005)]
    [InlineData(1.00001)]
    public void CycleTimeNotAMultipleOf100ns_IsRefused(double ms)
    {
        var fake = OpenFake();

        Create(fake, cycleTimeMs: ms).Should().StartWith("[Error]").And.Contain("not a whole multiple of 0.0001 ms");
        Changed(fake).Should().BeFalse();
    }

    [Theory]
    [InlineData(10, 100000u)]
    [InlineData(0.5, 5000u)]
    [InlineData(0.0001, 1u)]
    [InlineData(1000, 10000000u)]
    public void CycleTime_IsConvertedTo100nsUnits(double ms, uint expected)
    {
        PlcTaskCoordinator.TryCycleTicks(ms, out uint ticks).Should().BeNull();
        ticks.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void PriorityOutOfRange_IsRefused(int priority)
    {
        var fake = OpenFake();

        Create(fake, priority: priority).Should().StartWith($"[Error] priority {priority} is outside 1..63");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void ExistingTaskName_IsRefused()
    {
        var fake = OpenFake();

        string result = Create(fake, name: "plctask");

        result.Should().StartWith("[Error] A real-time task 'PlcTask' already exists (TIRT^PlcTask)");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void UsedPriority_IsRefusedAndTheUsersListed()
    {
        var fake = OpenFake();
        fake.RealtimeTasks.Add(new RealtimeTaskInfo("SlowTask", "TIRT^SlowTask", 1000000, 30));

        string result = Create(fake, priority: 20);

        result.Should().StartWith("[Error] priority 20 is already used by PlcTask")
            .And.Contain("Used priorities: 20 (PlcTask), 30 (SlowTask)");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void UnknownProgram_IsRefusedAndTheProgramsListed()
    {
        var fake = OpenFake();
        fake.PlcPrograms.Add("PRG_Fast");

        string result = Create(fake, program: "PRG_Missing");

        result.Should().StartWith("[Error] PLC 'Plc' has no PROGRAM 'PRG_Missing' (PROGRAMs: MAIN, PRG_Fast)");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void UnknownPlc_IsRefused()
    {
        var fake = OpenFake();

        string result = Create(fake, plc: "OtherPlc");

        result.Should().StartWith("[Error] PLC project 'OtherPlc' not found");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void ReferenceOfTheSameNameLinkedElsewhere_IsRefused()
    {
        var fake = OpenFake();
        fake.PlcTaskReferences.Add(new PlcTaskReferenceInfo("FastTask", $"{Project}^FastTask", "TIRT^OtherTask"));

        string result = Create(fake);

        result.Should().StartWith("[Error] PLC 'Plc' already has a task reference 'FastTask'").And.Contain("'TIRT^OtherTask'");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void NoSolution_IsRefused()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        Create(fake).Should().StartWith("[Error] No solution is currently open.");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void XaeStaysOnline_NothingIsCreated()
    {
        var fake = OpenFake();
        fake.FakeLogoutPlcResult = new LogoutPlcResult(true, true, "still online (fake)");

        string result = Create(fake);

        result.Should().StartWith("[Error] XAE is Online");
        fake.CallLog.Should().NotContain(c => c.StartsWith("CreateRealtimeTask", StringComparison.Ordinal));
    }

    // ---- priority choice ---------------------------------------------------------------------

    [Theory]
    [InlineData(new int[0], 20)]
    [InlineData(new[] { 20 }, 21)]
    [InlineData(new[] { 4, 8, 20 }, 21)]
    [InlineData(new[] { 63 }, 62)]
    [InlineData(new[] { 10, 62, 63 }, 61)]
    public void NextFreePriority_IsTheHighestPlusOne_OrTheHighestFreeBelow(int[] used, int expected)
    {
        PlcTaskCoordinator.NextFreePriority(used).Should().Be(expected);
    }

    [Fact]
    public void NextFreePriority_AllUsed_IsNull()
    {
        PlcTaskCoordinator.NextFreePriority(Enumerable.Range(1, 63)).Should().BeNull();
    }

    [Fact]
    public void AllPrioritiesUsed_IsRefused()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        foreach (int p in Enumerable.Range(1, 63))
            fake.RealtimeTasks.Add(new RealtimeTaskInfo($"T{p}", $"TIRT^T{p}", 100000, p));

        Create(fake).Should().StartWith("[Error] No free priority left in 1..63");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void UnreadablePriority_IsWarnedAbout()
    {
        var fake = OpenFake();
        fake.RealtimeTasks.Add(new RealtimeTaskInfo("Odd", "TIRT^Odd", 100000, null));

        string result = Create(fake);

        result.Should().NotStartWith("[Error]").And.Contain("[Warn] The priority of Odd could not be read");
    }

    // ---- the steps ---------------------------------------------------------------------------

    [Fact]
    public void Create_RunsTheStepsInOrder_AndReportsTheReadBack()
    {
        var fake = OpenFake();

        string result = Create(fake, program: "MAIN");

        fake.CallLog.Should().ContainInOrder(
            "LogoutPlc()",
            "CreateRealtimeTask(FastTask)",
            "SetRealtimeTaskTiming(TIRT^FastTask,100000,21)",
            $"CreatePlcTaskReference({Project},FastTask)",
            $"SetPlcTaskLink({Project}^FastTask,TIRT^FastTask)",
            $"CreatePlcChild(parent={Project}^FastTask, name=MAIN, subType={PlcItemType.ProgRef}, label=TASKREF (PROGRAM))",
            "SaveAll()");
        result.Should().StartWith("Created real-time task 'TIRT^FastTask' (read-back: cycle time 10 ms (100000 x 100 ns), priority 21).")
            .And.Contain($"Created PLC task reference '{Project}^FastTask' of PLC 'Plc', linked to 'TIRT^FastTask' (read-back).")
            .And.Contain($"The task calls PROGRAM 'MAIN' ({Project}^FastTask^MAIN).")
            .And.Contain("Project saved.")
            .And.NotContain("[Warn]");
    }

    [Fact]
    public void Create_WithoutProgram_SaysHowToAddOne()
    {
        var fake = OpenFake();

        string result = Create(fake, priority: 5, cycleTimeMs: 0.5);

        fake.CallLog.Should().Contain("SetRealtimeTaskTiming(TIRT^FastTask,5000,5)");
        fake.CallLog.Should().NotContain(c => c.StartsWith("CreatePlcChild", StringComparison.Ordinal));
        result.Should().Contain("cycle time 0.5 ms (5000 x 100 ns), priority 5")
            .And.Contain($"No program assigned: add one with twincat_plc_create (kind=TASKREF, parentPath='{Project}^FastTask'");
    }

    [Fact]
    public void ExistingUnlinkedReference_IsLinkedNotCreatedAgain()
    {
        var fake = OpenFake();
        fake.PlcTaskReferences.Add(new PlcTaskReferenceInfo("FastTask", $"{Project}^FastTask", null));

        string result = Create(fake);

        fake.CallLog.Should().NotContain(c => c.StartsWith("CreatePlcTaskReference", StringComparison.Ordinal));
        fake.CallLog.Should().Contain($"SetPlcTaskLink({Project}^FastTask,TIRT^FastTask)");
        result.Should().Contain($"Linked the existing PLC task reference '{Project}^FastTask'");
    }

    [Fact]
    public void XaeAdjustsCycleTimeAndPriority_IsWarnedAbout()
    {
        var fake = OpenFake();
        fake.RealtimeTaskCycleTimeStored = 20000;
        fake.RealtimeTaskPriorityStored = 22;

        string result = Create(fake, cycleTimeMs: 1.5);

        result.Should().NotStartWith("[Error]")
            .And.Contain("read-back: cycle time 2 ms (20000 x 100 ns), priority 22")
            .And.Contain("[Warn] The XAE set cycle time 2 ms (20000 x 100 ns), not the requested 1.5 ms (15000 x 100 ns)")
            .And.Contain("[Warn] The XAE set priority 22, not the requested 21.");
    }

    [Fact]
    public void UnreadableTimingReadBack_IsWarnedAbout()
    {
        var differences = PlcTaskCoordinator.TimingDifferences(100000, 21, new RealtimeTaskInfo("T", "TIRT^T", null, null)).ToList();

        differences.Should().Contain(d => d.Contains("cycle time could not be read back"))
            .And.Contain(d => d.Contains("priority could not be read back"));
    }

    // ---- partial failures --------------------------------------------------------------------

    [Fact]
    public void CreateTaskFails_NothingElseHappens()
    {
        var fake = OpenFake();
        fake.CreateRealtimeTaskThrows = new InvalidOperationException("TIRT.CreateChild('FastTask', 1) failed — the task was not created: boom");

        string result = Create(fake);

        result.Should().StartWith("[Error] TIRT.CreateChild('FastTask', 1) failed");
        fake.CallLog.Should().NotContain(c => c.StartsWith("SetRealtimeTaskTiming", StringComparison.Ordinal) || c == "SaveAll()");
    }

    [Fact]
    public void TimingFails_NamesTheCreatedTask()
    {
        var fake = OpenFake();
        fake.SetRealtimeTaskTimingThrows = new InvalidOperationException("Setting CycleTime failed: E_FAIL");

        string result = Create(fake);

        result.Should().StartWith("[Error] setting cycle time and priority failed: Setting CycleTime failed: E_FAIL")
            .And.Contain("Already created: real-time task 'TIRT^FastTask'.")
            .And.Contain("The project was not saved.")
            .And.Contain("right-click > Delete");
        fake.CallLog.Should().NotContain(c => c.StartsWith("CreatePlcTaskReference", StringComparison.Ordinal) || c == "SaveAll()");
    }

    [Fact]
    public void ReferenceFails_NamesTheCreatedTask()
    {
        var fake = OpenFake();
        fake.CreatePlcTaskReferenceThrows = new InvalidOperationException("CreateChild('FastTask', 621) failed: refused");

        string result = Create(fake);

        result.Should().StartWith($"[Error] creating the PLC task reference under '{Project}' failed")
            .And.Contain("Already created: real-time task 'TIRT^FastTask'.");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    [Fact]
    public void LinkNotLanded_NamesTaskAndReference()
    {
        var fake = OpenFake();
        fake.PlcTaskLinkIsNoOp = true;

        string result = Create(fake);

        result.Should().StartWith($"[Error] linking '{Project}^FastTask' to 'TIRT^FastTask' failed: the read-back shows LinkedTask '<unreadable>'")
            .And.Contain($"Already created: real-time task 'TIRT^FastTask'; PLC task reference '{Project}^FastTask'.");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    [Fact]
    public void LinkThrows_NamesTaskAndReference()
    {
        var fake = OpenFake();
        fake.SetPlcTaskLinkThrows = new InvalidOperationException("LinkedTask = 'TIRT^FastTask' failed: TSM_E_ITEMNOTFOUND");

        string result = Create(fake);

        result.Should().StartWith("[Error] linking").And.Contain("TSM_E_ITEMNOTFOUND")
            .And.Contain($"PLC task reference '{Project}^FastTask'");
    }

    [Fact]
    public void ProgramCallFails_SaysHowToAddItLater()
    {
        var fake = OpenFake();
        fake.CreatePlcChildThrows = new InvalidOperationException("CreateChild failed for TASKREF (PROGRAM) 'MAIN'");

        string result = Create(fake, program: "MAIN");

        result.Should().StartWith("[Error] adding the call of PROGRAM 'MAIN' failed")
            .And.Contain($"Already created: real-time task 'TIRT^FastTask'; PLC task reference '{Project}^FastTask'.")
            .And.Contain($"twincat_plc_create (kind=TASKREF, parentPath='{Project}^FastTask', name='MAIN')");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    // ---- TaskDef XML -------------------------------------------------------------------------

    [Fact]
    public void TaskDefXml_RoundTrips()
    {
        string xml = RealtimeTaskXml.BuildTimingXml(100000, 21);

        xml.Should().Be("<TreeItem><TaskDef><CycleTime>100000</CycleTime><Priority>21</Priority></TaskDef></TreeItem>");
        RealtimeTaskXml.ReadTiming(xml).Should().Be(((uint?)100000, (int?)21));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<TreeItem/>")]
    [InlineData("not xml")]
    [InlineData("<TreeItem><TaskDef><CycleTime>x</CycleTime></TaskDef></TreeItem>")]
    public void TaskDefXml_MissingValues_AreNull(string? xml)
    {
        RealtimeTaskXml.ReadTiming(xml).Should().Be(((uint?)null, (int?)null));
    }
}
