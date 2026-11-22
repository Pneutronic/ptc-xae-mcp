using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// twincat_task_list / twincat_task_configure / twincat_io_task_create: TaskDef parsing, task kinds, refusals before the logout,
/// the TaskDef payload, the read-back and when the project is saved.
/// </summary>
public sealed class TaskCoordinatorTests
{
    private const string Saf = "TINC^NC-Task 1 SAF";
    private const string Svb = "TINC^NC-Task 1 SAF^NC-Task 1 SVB";

    // PlcTask (20, port 350, linked), NC SAF (4, 501, image) and SVB (8, 511) as the XAE 3.1.4026 shows them.
    private static FakeTwinCatSession OpenFake()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, ConsumeXmlApplier = FakeTwinCatSession.TaskDefApplier };
        fake.AddTask("TIRT^PlcTask", 20, 350, 100000, image: false);
        fake.AddTask(Saf, 4, 501, 20000, image: true);
        fake.AddTask(Svb, 8, 511, 100000, image: false);
        fake.PlcTaskLinks.Add("TIRT^PlcTask");
        return fake;
    }

    private static bool Changed(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c == "LogoutPlc()" || c.StartsWith("Create", StringComparison.Ordinal)
                              || c.StartsWith("ConsumeTreeItemXml", StringComparison.Ordinal)
                              || c.StartsWith("SetDeviceDisabled", StringComparison.Ordinal) || c == "SaveAll()");

    // ---- TaskDef XML -------------------------------------------------------------------------

    [Fact]
    public void TaskDefXml_ReadsTheNcSvbTaskAsTheXaeWritesIt()
    {
        const string xml = "<?xml version=\"1.0\"?><TreeItem><ItemName>NC-Task 1 SVB</ItemName><PathName>TINC^NC-Task 1 SAF^NC-Task 1 SVB</PathName>" +
                           "<ItemType>1</ItemType><Disabled>false</Disabled><TaskDef><Priority>8</Priority><AmsPort>511</AmsPort>" +
                           "<CycleTime>100000</CycleTime><AutoStart>true</AutoStart><IoAtBegin>false</IoAtBegin></TaskDef>" +
                           "<TcModuleInstance><Module><Contexts><Context><Priority>99</Priority><CycleTime>1</CycleTime></Context></Contexts></Module></TcModuleInstance></TreeItem>";

        var s = TaskDefXml.Parse(xml)!;

        s.Name.Should().Be("NC-Task 1 SVB");
        s.Path.Should().Be(Svb);
        s.Priority.Should().Be(8);
        s.AmsPort.Should().Be(511);
        s.CycleTime100ns.Should().Be(100000u);
        s.AutoStart.Should().BeTrue();
        s.Disabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("<TreeItem><ItemName>Image</ItemName></TreeItem>")]
    [InlineData("not xml")]
    [InlineData("")]
    public void TaskDefXml_IsNullWithoutTaskDef(string xml) => TaskDefXml.Parse(xml).Should().BeNull();

    [Fact]
    public void TaskDefXml_BuildsOnlyTheGivenValues()
    {
        TaskDefXml.Build(5, null, null).Should().Be("<TreeItem><TaskDef><Priority>5</Priority></TaskDef></TreeItem>");
        TaskDefXml.Build(5, 301, 20000).Should().Be(
            "<TreeItem><TaskDef><Priority>5</Priority><AmsPort>301</AmsPort><CycleTime>20000</CycleTime></TaskDef></TreeItem>");
        TaskDefXml.Build(null, null, null).Should().BeNull();
    }

    [Theory]
    [InlineData("TINC^NC-Task 1 SAF", true, false, "nc")]
    [InlineData("TIRT^PlcTask", false, true, "plc")]
    [InlineData("TIRT^IoTask", true, false, "io")]
    [InlineData("TIRT^Spare", false, false, "other")]
    public void TaskKind_FollowsPathImageAndPlcLink(string path, bool image, bool linked, string expected) =>
        TaskKind.Of(path, image, linked).Should().Be(expected);

    // ---- list --------------------------------------------------------------------------------

    [Fact]
    public void List_ReportsEveryTaskWithKindImageAndValues()
    {
        var fake = OpenFake();

        string result = new TaskCoordinator(fake).List();

        result.Should().StartWith("3 real-time task(s): 1 under TIRT, 2 under TINC (NC).");
        using var doc = JsonDocument.Parse(result[result.IndexOf('{')..]);
        var tasks = doc.RootElement.GetProperty("tasks").EnumerateArray().ToList();
        tasks.Select(t => t.GetProperty("kind").GetString()).Should().Equal("plc", "nc", "nc");
        tasks[0].GetProperty("amsPort").GetInt32().Should().Be(350);
        tasks[0].GetProperty("cycleTimeMs").GetDouble().Should().Be(10);
        tasks[1].GetProperty("image").GetBoolean().Should().BeTrue();
        tasks[2].GetProperty("path").GetString().Should().Be(Svb);
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void List_WithoutReadablePlcReferences_WarnsAndStillLists()
    {
        var fake = OpenFake();
        fake.ListPlcTaskLinksThrows = new InvalidOperationException("no PLC");

        string result = new TaskCoordinator(fake).List();

        result.Should().Contain("\"kind\": \"other\"").And.Contain("kind 'plc' is not reported");
    }

    [Fact]
    public void List_WithoutSolution_IsRefused()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        new TaskCoordinator(fake).List().Should().StartWith("[Error] No solution is currently open.");
    }

    // ---- configure: refusals -----------------------------------------------------------------

    [Fact]
    public void Configure_NothingToChange_IsRefused()
    {
        var fake = OpenFake();
        new TaskCoordinator(fake).Configure("PlcTask", null, null, null, null).Should().StartWith("[Error] Nothing to change");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Configure_UnknownTask_ListsTheTasks()
    {
        var fake = OpenFake();
        string result = new TaskCoordinator(fake).Configure("Nope", 5, null, null, null);
        result.Should().StartWith("[Error] No real-time task named 'Nope'").And.Contain(Svb);
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Configure_PriorityOfAnNcTask_IsRefused()
    {
        var fake = OpenFake();
        string result = new TaskCoordinator(fake).Configure("PlcTask", 8, null, null, null);
        result.Should().StartWith("[Error] priority 8 is already used by NC-Task 1 SVB");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Configure_KeepingItsOwnPriority_IsNotACollision()
    {
        var fake = OpenFake();
        new TaskCoordinator(fake).Configure("PlcTask", 20, 5, null, null).Should().NotStartWith("[Error]");
    }

    [Fact]
    public void Configure_UsedAmsPort_IsRefused()
    {
        var fake = OpenFake();
        new TaskCoordinator(fake).Configure("PlcTask", null, null, 511, null).Should().StartWith("[Error] AMS port 511 is already used by NC-Task 1 SVB");
        Changed(fake).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(64, null, null)]
    [InlineData(null, 0.0, null)]
    [InlineData(null, 1.00001, null)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, 70000)]
    public void Configure_InvalidValues_AreRefusedWithoutXae(int? priority, double? cycle, int? port)
    {
        var fake = OpenFake();
        new TaskCoordinator(fake).Configure("PlcTask", priority, cycle, port, null).Should().StartWith("[Error]");
        fake.CallLog.Should().BeEmpty();
    }

    // ---- configure: writes -------------------------------------------------------------------

    [Fact]
    public void Configure_WritesOnlyTheGivenValues_ReadsBack_AndSaves()
    {
        var fake = OpenFake();

        string result = new TaskCoordinator(fake).Configure(Svb, 9, 2, null, null);

        fake.ConsumedXml.Should().ContainSingle().Which.Should().Be(
            (Svb, "<TreeItem><TaskDef><Priority>9</Priority><CycleTime>20000</CycleTime></TaskDef></TreeItem>"));
        result.Should().StartWith($"Changed task '{Svb}' (read-back): priority 8 -> 9; cycle time 10 ms (100000 x 100 ns) -> 2 ms (20000 x 100 ns).");
        result.Should().Contain("Project saved.");
        fake.CallLog.Should().ContainInOrder("LogoutPlc()", $"ConsumeTreeItemXml({Svb})", "SaveAll()");
    }

    [Fact]
    public void Configure_Disabled_GoesThroughTheDisabledFlag()
    {
        var fake = OpenFake();
        fake.OnSetDeviceDisabled = (path, d) => fake.TreeItemXml[path] = fake.TreeItemXml[path].Replace("<Disabled>false</Disabled>", $"<Disabled>{(d ? "true" : "false")}</Disabled>");

        string result = new TaskCoordinator(fake).Configure("PlcTask", null, null, null, true);

        fake.ConsumedXml.Should().BeEmpty();
        fake.CallLog.Should().Contain("SetDeviceDisabled(TIRT^PlcTask, True)");
        result.Should().Contain("disabled false -> true").And.Contain("Project saved.");
    }

    [Fact]
    public void Configure_NothingTaken_IsAnErrorWithoutSaving()
    {
        var fake = OpenFake();
        fake.ConsumeXmlApplier = null; // the XAE ignores the payload

        string result = new TaskCoordinator(fake).Configure("PlcTask", 5, null, null, null);

        result.Should().StartWith("[Error] The XAE did not take any of the changes on 'TIRT^PlcTask' (priority: requested 5, the read-back shows 20)");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    [Fact]
    public void Configure_PartlyTaken_SavesAndWarns()
    {
        var fake = OpenFake();
        fake.ConsumeXmlApplier = (path, current, payload) =>
            FakeTwinCatSession.TaskDefApplier(path, current, payload.Replace("<AmsPort>302</AmsPort>", ""));

        string result = new TaskCoordinator(fake).Configure("PlcTask", 5, null, 302, null);

        result.Should().Contain("priority 20 -> 5").And.Contain("[Warn] AMS port: requested 302, the read-back shows 350");
        fake.CallLog.Should().Contain("SaveAll()");
    }

    // ---- io task -----------------------------------------------------------------------------

    [Fact]
    public void CreateIoTask_TakesTheNextFreePriority_SetsPortAndCycle_AndSaves()
    {
        var fake = OpenFake();

        string result = new TaskCoordinator(fake).CreateIoTask("IoTask", 2, null, 301);

        fake.CallLog.Should().ContainInOrder("LogoutPlc()", "CreateImageTask(IoTask)", "ConsumeTreeItemXml(TIRT^IoTask)", "SaveAll()");
        fake.ConsumedXml.Should().ContainSingle().Which.Xml.Should().Be(
            "<TreeItem><TaskDef><Priority>21</Priority><AmsPort>301</AmsPort><CycleTime>20000</CycleTime></TaskDef></TreeItem>");
        result.Should().StartWith("Created I/O task 'TIRT^IoTask' with process image (read-back: cycle time 2 ms (20000 x 100 ns), priority 21, AMS port 301).");
        result.Should().NotContain("[Warn]");
    }

    [Fact]
    public void CreateIoTask_WithoutPort_WritesPriorityAnyway()
    {
        var fake = OpenFake();

        new TaskCoordinator(fake).CreateIoTask("IoTask", 10, 30, null);

        fake.ConsumedXml.Single().Xml.Should().Be("<TreeItem><TaskDef><Priority>30</Priority><CycleTime>100000</CycleTime></TaskDef></TreeItem>");
    }

    [Fact]
    public void CreateIoTask_NameOfAnNcTask_IsRefused()
    {
        var fake = OpenFake();
        new TaskCoordinator(fake).CreateIoTask("NC-Task 1 SVB", 2, null, null).Should().StartWith("[Error] Task name");
        new TaskCoordinator(fake).CreateIoTask("PlcTask", 2, null, null).Should().StartWith("[Error] A real-time task 'PlcTask' already exists");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void CreateIoTask_PriorityOfTheSafTask_IsRefused()
    {
        var fake = OpenFake();
        new TaskCoordinator(fake).CreateIoTask("IoTask", 2, 4, null).Should().StartWith("[Error] priority 4 is already used by NC-Task 1 SAF");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void CreateIoTask_UsedPort_IsRefused()
    {
        var fake = OpenFake();
        new TaskCoordinator(fake).CreateIoTask("IoTask", 2, null, 350).Should().StartWith("[Error] AMS port 350 is already used by PlcTask");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void CreateIoTask_PriorityNotTaken_Warns()
    {
        var fake = OpenFake();
        fake.ConsumeXmlApplier = null; // the new task keeps priority 1, port 301 and 10 ms

        string result = new TaskCoordinator(fake).CreateIoTask("IoTask", 2, 30, null);

        result.Should().Contain("[Warn] The XAE set priority 1, not 30").And.Contain("[Warn] The XAE set cycle time");
    }

    [Fact]
    public void CreateIoTask_FailingTaskDef_NamesTheCreatedTask()
    {
        var fake = OpenFake();
        fake.ConsumeTreeItemXmlThrows = new InvalidOperationException("E_FAIL");

        string result = new TaskCoordinator(fake).CreateIoTask("IoTask", 10, null, null);

        result.Should().StartWith("[Error] Setting cycle time and priority of 'TIRT^IoTask' failed")
            .And.Contain("Already created: real-time task 'TIRT^IoTask'");
        fake.CallLog.Should().NotContain("SaveAll()");
    }
}
