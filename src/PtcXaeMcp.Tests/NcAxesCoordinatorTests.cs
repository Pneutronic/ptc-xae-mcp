using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// Unit coverage for the feature/nc-axes coordinator: input validation for
// every sub-tool, the unknown-field rejection for configure_axis, the
// not-connected / no-solution guards, and the success summaries. The
// browse-by-type COM walk itself is exercised live in the integration
// suite (it needs a real ITcSmTreeItem tree).
public class NcAxesCoordinatorTests
{
    private static FakeTwinCatSession OpenFake() =>
        new() { IsConnected = true, SolutionOpen = true };

    private static FakeTwinCatSession ClosedFake() =>
        new() { IsConnected = true, SolutionOpen = false };

    private static FakeTwinCatSession DisconnectedFake() =>
        new() { IsConnected = false };

    // ---- ListNcTasksAxes -----------------------------------------------------

    [Fact]
    public void List_NotConnected_ReturnsError()
    {
        var (summary, tasks) = new NcAxesCoordinator(DisconnectedFake()).ListNcTasksAxes();
        Assert.StartsWith("[Error]", summary);
        Assert.Empty(tasks);
    }

    [Fact]
    public void List_NoSolution_ReturnsError()
    {
        var (summary, _) = new NcAxesCoordinator(ClosedFake()).ListNcTasksAxes();
        Assert.StartsWith("[Error]", summary);
    }

    [Fact]
    public void List_Empty_ReportsNone()
    {
        var (summary, tasks) = new NcAxesCoordinator(OpenFake()).ListNcTasksAxes();
        Assert.Contains("No NC-Tasks", summary);
        Assert.Empty(tasks);
    }

    [Fact]
    public void List_CountsTasksAndAxes()
    {
        var fake = OpenFake();
        fake.NcTasks.Add(new NcTaskEntry("TINC^NC-Task 1 SAF", "NC-Task 1 SAF",
        [
            new NcAxisEntry("TINC^NC-Task 1 SAF^Axes^Transport", "Transport"),
            new NcAxisEntry("TINC^NC-Task 1 SAF^Axes^Schwenkarm", "Schwenkarm"),
        ]));

        var (summary, tasks) = new NcAxesCoordinator(fake).ListNcTasksAxes();

        Assert.Contains("1 NC-Task", summary);
        Assert.Contains("2 axes", summary);
        Assert.Single(tasks);
        Assert.Equal(2, tasks[0].Axes.Count);
    }

    [Fact]
    public void List_SessionThrows_ReturnsError()
    {
        var fake = OpenFake();
        fake.ListNcTasksAxesThrows = new InvalidOperationException("TINC not found");
        var (summary, _) = new NcAxesCoordinator(fake).ListNcTasksAxes();
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("TINC not found", summary);
    }

    // ---- CreateNcTask --------------------------------------------------------

    [Fact]
    public void CreateNcTask_EmptyName_ErrorsBeforeSession()
    {
        var fake = OpenFake();
        var summary = new NcAxesCoordinator(fake).CreateNcTask("  ");
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("name", summary);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("CreateNcTask", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateNcTask_NoSolution_ReturnsError()
    {
        var summary = new NcAxesCoordinator(ClosedFake()).CreateNcTask("NC-Task");
        Assert.StartsWith("[Error]", summary);
    }

    [Fact]
    public void CreateNcTask_ReportsActualPathFromSession()
    {
        var fake = OpenFake();
        fake.CreateNcTaskResult = "TINC^NC-Task 1";          // TwinCAT auto-suffixed
        var summary = new NcAxesCoordinator(fake).CreateNcTask("NC-Task");
        Assert.Contains("TINC^NC-Task 1", summary);
        Assert.Contains("CreateNcTask(NC-Task)", fake.CallLog);
    }

    [Theory]
    [InlineData("NC-Task SAF", "NC-Task", "SAF")]
    [InlineData("NC-Task 1 saf", "NC-Task 1", "SAF")]
    [InlineData("NC-Task SVB", "NC-Task", "SVB")]
    public void CreateNcTask_TrailingXaeSuffix_IsRemovedAndReported(string given, string passed, string suffix)
    {
        // #53: the XAE appends " SAF" itself; "NC-Task SAF" became "TINC^NC-Task SAF SAF".
        var fake = OpenFake();
        fake.CreateNcTaskResult = $"TINC^{passed} SAF";
        var summary = new NcAxesCoordinator(fake).CreateNcTask(given);
        Assert.Contains($"CreateNcTask({passed})", fake.CallLog);
        Assert.Contains($"TINC^{passed} SAF.", summary);
        Assert.Contains($"'{suffix}' was removed", summary);
    }

    [Fact]
    public void CreateNcTask_NameWithoutSuffix_IsPassedUnchangedWithoutNote()
    {
        var fake = OpenFake();
        var summary = new NcAxesCoordinator(fake).CreateNcTask("SAFety task");
        Assert.Contains("CreateNcTask(SAFety task)", fake.CallLog);
        Assert.DoesNotContain("removed", summary);
    }

    [Fact]
    public void CreateNcTask_OnlyTheSuffix_ErrorsBeforeSession()
    {
        var fake = OpenFake();
        var summary = new NcAxesCoordinator(fake).CreateNcTask(" SAF");
        Assert.StartsWith("[Error]", summary);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("CreateNcTask", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateNcTask_AlreadyExists_RewrittenToGuidance()
    {
        var fake = OpenFake();
        fake.CreateNcTaskThrows = new InvalidOperationException("Item already exists!");
        var summary = new NcAxesCoordinator(fake).CreateNcTask("NC-Task 2");
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("green-field", summary);
        Assert.Contains("twincat_list_nc_tasks_axes", summary);
        Assert.DoesNotContain("Item already exists!", summary);   // raw COM text hidden
    }

    [Fact]
    public void CreateNcTask_OtherError_PassesThrough()
    {
        var fake = OpenFake();
        fake.CreateNcTaskThrows = new InvalidOperationException("TINC root not found.");
        var summary = new NcAxesCoordinator(fake).CreateNcTask("NC-Task");
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("TINC root not found.", summary);
    }

    // ---- CreateAxis ----------------------------------------------------------

    [Fact]
    public void CreateAxis_MissingTaskPath_Errors()
    {
        var summary = new NcAxesCoordinator(OpenFake()).CreateAxis("", "Axis 1");
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("ncTaskPath", summary);
    }

    [Fact]
    public void CreateAxis_MissingAxisName_Errors()
    {
        var summary = new NcAxesCoordinator(OpenFake()).CreateAxis("TINC^NC-Task 1 SAF", " ");
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("axisName", summary);
    }

    [Fact]
    public void CreateAxis_Success_ReportsActualPath()
    {
        var fake = OpenFake();
        fake.CreateAxisResult = "TINC^NC-Task 1 SAF^Axes^Axis 1";
        var summary = new NcAxesCoordinator(fake).CreateAxis("TINC^NC-Task 1 SAF", "Axis 1");
        Assert.Contains("TINC^NC-Task 1 SAF^Axes^Axis 1", summary);
        Assert.Contains("CreateAxis(TINC^NC-Task 1 SAF,Axis 1)", fake.CallLog);
    }

    // ---- ConfigureAxis -------------------------------------------------------

    [Fact]
    public void ConfigureAxis_MissingPath_Errors()
    {
        var summary = new NcAxesCoordinator(OpenFake())
            .ConfigureAxis("", new Dictionary<string, string> { ["unit"] = "mm" });
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("axisPath", summary);
    }

    [Fact]
    public void ConfigureAxis_NoFields_Errors()
    {
        var fake = OpenFake();
        var summary = new NcAxesCoordinator(fake)
            .ConfigureAxis("TINC^NC-Task 1 SAF^Axes^Transport", new Dictionary<string, string>());
        Assert.StartsWith("[Error]", summary);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ConfigureAxis", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigureAxis_UnknownField_RejectedBeforeSession()
    {
        var fake = OpenFake();
        var summary = new NcAxesCoordinator(fake).ConfigureAxis(
            "TINC^NC-Task 1 SAF^Axes^Transport",
            new Dictionary<string, string> { ["bogusField"] = "1" });
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("Unknown axis field", summary);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ConfigureAxis", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigureAxis_Success_PassesFieldsAndSummarises()
    {
        var fake = OpenFake();
        fake.ConfigureAxisResult = "scaleFactorNumerator=0.0002";
        var fields = new Dictionary<string, string> { ["scaleFactorNumerator"] = "0.0002" };

        var summary = new NcAxesCoordinator(fake)
            .ConfigureAxis("TINC^NC-Task 1 SAF^Axes^Transport", fields);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("scaleFactorNumerator=0.0002", summary);
        Assert.Same(fields, fake.LastConfigureAxisFields);
    }

    [Fact]
    public void ConfigureAxis_NoSolution_ReturnsError()
    {
        var summary = new NcAxesCoordinator(ClosedFake()).ConfigureAxis(
            "TINC^NC-Task 1 SAF^Axes^Transport",
            new Dictionary<string, string> { ["unit"] = "mm" });
        Assert.StartsWith("[Error]", summary);
    }

    // ---- ReadAxisParams ------------------------------------------------------

    [Fact]
    public void ReadAxisParams_MissingPath_ErrorsBeforeSession()
    {
        var fake = OpenFake();
        var (summary, p) = new NcAxesCoordinator(fake).ReadAxisParams("  ");
        Assert.StartsWith("[Error]", summary);
        Assert.Null(p);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ReadAxisParams", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadAxisParams_NoSolution_ReturnsError()
    {
        var (summary, p) = new NcAxesCoordinator(ClosedFake())
            .ReadAxisParams("TINC^NC-Task 1 SAF^Axes^Transport");
        Assert.StartsWith("[Error]", summary);
        Assert.Null(p);
    }

    [Fact]
    public void ReadAxisParams_Success_ReturnsParamsAndCount()
    {
        var fake = OpenFake();
        fake.ReadAxisParamsResult = new NcAxisParams("<TreeItem/>",
            new Dictionary<string, string> { ["unit"] = "mm", ["velocityMax"] = "2000" });

        var (summary, p) = new NcAxesCoordinator(fake)
            .ReadAxisParams("TINC^NC-Task 1 SAF^Axes^Transport");

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("2 known parameter", summary);
        Assert.NotNull(p);
        Assert.Equal("mm", p!.Fields["unit"]);
    }

    [Fact]
    public void ReadAxisParams_SessionThrows_ReturnsError()
    {
        var fake = OpenFake();
        fake.ReadAxisParamsThrows = new InvalidOperationException("Axis not found by browsing");
        var (summary, p) = new NcAxesCoordinator(fake)
            .ReadAxisParams("TINC^bogus");
        Assert.StartsWith("[Error]", summary);
        Assert.Contains("Axis not found", summary);
        Assert.Null(p);
    }
}
