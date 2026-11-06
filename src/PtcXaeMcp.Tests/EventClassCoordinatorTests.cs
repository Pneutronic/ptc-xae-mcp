using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// twincat_event_class_create / _read / _update and twincat_plc_add_global_data_type with the fake session: refusals
/// before anything changes, the read-backs, and the pin written while the solution is closed.
/// </summary>
public sealed class EventClassCoordinatorTests
{
    private const string Plcproj = @"C:\sln\Plc\Plc.plcproj";
    private const string ExternalTypes = @"C:\sln\Plc\ExternalTypes.tmc";
    private const string PlcprojText =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
        "  <ProjectExtensions />\r\n</Project>";

    private static readonly EventClassEventInput[] Events =
    [
        new(1, "Started", "info", "Started {0}"),
        new(2, "Failed", "Error", null),
    ];

    private static FakeTwinCatSession OpenFake()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.TypeSystem.Add("<DataType><Name GUID=\"{bb2a9999-102e-421a-8d3d-b0660e07b1fe}\" TcBaseType=\"true\">TcSystemEventClass</Name>" +
                            "<DisplayName TxtId=\"\"><![CDATA[TcSystemEventClass]]></DisplayName><EventId><Name Id=\"1\">InternalError</Name>" +
                            "<DisplayName TxtId=\"\"><![CDATA[Internal error.]]></DisplayName><Severity>Error</Severity></EventId></DataType>");
        fake.TypeSystem.Add("<DataType><Name GUID=\"{18071995-0000-0000-0000-000000000030}\">BOOL</Name><BitSize>8</BitSize></DataType>");
        fake.EventPlcs.Add(("Plc", Plcproj));
        fake.Files[Plcproj] = PlcprojText;
        return fake;
    }

    private static EventClassCoordinator Coordinator(FakeTwinCatSession fake) => new(fake);

    private static bool Changed(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c == "LogoutPlc()" || c.StartsWith("Create", StringComparison.Ordinal)
                              || c.StartsWith("Replace", StringComparison.Ordinal) || c.StartsWith("EditFiles", StringComparison.Ordinal)
                              || c == "SaveAll()");

    private static FakeTwinCatSession WithClass(string name = "MyEvents")
    {
        var fake = OpenFake();
        Coordinator(fake).Create(name, "My events", Events).Should().StartWith("Created");
        fake.CallLog.Clear();
        return fake;
    }

    // ---- create -------------------------------------------------------------------------------

    [Fact]
    public void Create_WritesAPersistentClass_ReadsItBack_AndSaves()
    {
        var fake = OpenFake();

        string result = Coordinator(fake).Create("MyEvents", null, Events);

        result.Should().StartWith("Created event class 'MyEvents' {00000000-0000-0000-0000-000000000001} in the type system");
        result.Should().Contain("1 Started (Info) 'Started {0}'").And.Contain("2 Failed (Error) 'Failed'");
        result.Should().Contain("Project saved.").And.Contain("TC_EVENTS.MyEvents.Started").And.Contain("twincat_plc_add_global_data_type");
        result.Should().NotContain("[Warn]");
        fake.CallLog.Should().ContainInOrder("ReadEventClassXml()", "CreateTypeSystemType(MyEvents)", "SaveAll()");
        fake.CallLog.Should().NotContain("LogoutPlc()");
        EventClassXml.Parse(fake.TypeSystem.Last())!.Persistent.Should().BeTrue();
    }

    [Theory]
    [InlineData("1Bad")]
    [InlineData("My Events")]
    [InlineData("")]
    public void Create_InvalidClassName_IsRefused(string name)
    {
        var fake = OpenFake();
        Coordinator(fake).Create(name, null, Events).Should().StartWith("[Error] Event class name");
        Changed(fake).Should().BeFalse();
    }

    public static TheoryData<EventClassEventInput[], string> BadEvents => new()
    {
        { [], "events is required" },
        { [new(1, "A"), new(1, "B")], "id 1 is used twice" },
        { [new(1, "A"), new(2, "a")], "the name 'a' is used twice" },
        { [new(-1, "A")], "outside 0..4294967295" },
        { [new(4294967296, "A")], "outside 0..4294967295" },
        { [new(1, "A", "Fatal")], "severity 'Fatal' is none of Verbose, Info, Warning, Error, Critical" },
        { [new(1, "not valid")], "events[0].name" },
    };

    [Theory]
    [MemberData(nameof(BadEvents))]
    public void Create_BadEvents_AreRefused(EventClassEventInput[] events, string message)
    {
        var fake = OpenFake();

        string result = Coordinator(fake).Create("MyEvents", null, events);

        result.Should().StartWith("[Error]").And.Contain(message);
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Create_ExistingName_IsRefused()
    {
        var fake = WithClass();

        string result = Coordinator(fake).Create("myevents", null, Events);

        result.Should().StartWith("[Error] An event class 'MyEvents'").And.Contain("twincat_event_class_update");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Create_WithoutSolution_IsRefused()
    {
        var fake = OpenFake();
        fake.SolutionOpen = false;

        Coordinator(fake).Create("MyEvents", null, Events).Should().StartWith("[Error] No solution is currently open");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Create_ReadBackWithoutPersistentType_Warns()
    {
        var fake = OpenFake();
        fake.CreateDropsPersistent = true;

        Coordinator(fake).Create("MyEvents", null, Events).Should().Contain("[Warn] The class reads back without PersistentType");
    }

    [Fact]
    public void Create_CreateTypeFails_IsAnError_AndNothingIsSaved()
    {
        var fake = OpenFake();
        fake.CreateTypeSystemTypeThrows = new InvalidOperationException("ITcTypeSystem.CreateType refused the type 'MyEvents': bad");

        string result = Coordinator(fake).Create("MyEvents", null, Events);

        result.Should().StartWith("[Error]").And.Contain("refused the type").And.EndWith("Nothing was saved.");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    // ---- read ---------------------------------------------------------------------------------

    [Fact]
    public void Read_List_HidesBuiltIns_UnlessAsked()
    {
        var fake = WithClass();

        using var list = JsonDocument.Parse(Coordinator(fake).Read(null, includeBuiltIn: false));
        list.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        list.RootElement.GetProperty("builtInHidden").GetInt32().Should().Be(1);
        var only = list.RootElement.GetProperty("eventClasses")[0];
        only.GetProperty("name").GetString().Should().Be("MyEvents");
        only.GetProperty("origin").GetString().Should().Be("project (.tsproj)");
        only.GetProperty("events").GetInt32().Should().Be(2);

        using var all = JsonDocument.Parse(Coordinator(fake).Read(null, includeBuiltIn: true));
        all.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        all.RootElement.GetProperty("eventClasses")[1].GetProperty("origin").GetString().Should().Be("built-in");
    }

    [Fact]
    public void Read_OneClass_ListsEventsAndPins()
    {
        var fake = WithClass();
        Coordinator(fake).AddGlobalDataType("MyEvents", null).Should().Contain("Pinned event class 'MyEvents'");

        using var doc = JsonDocument.Parse(Coordinator(fake).Read("myevents", false));
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("MyEvents");
        root.GetProperty("events").GetArrayLength().Should().Be(2);
        root.GetProperty("events")[1].GetProperty("severity").GetString().Should().Be("Error");
        var pin = root.GetProperty("pinnedIn")[0];
        pin.GetProperty("plc").GetString().Should().Be("Plc");
        pin.GetProperty("upToDate").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Read_UnknownClass_IsAnError_ListingTheProjectClasses()
    {
        var fake = WithClass();
        Coordinator(fake).Read("Nope", false).Should().StartWith("[Error] No event class 'Nope'").And.Contain("MyEvents")
            .And.NotContain("TcSystemEventClass");
    }

    // ---- update -------------------------------------------------------------------------------

    [Fact]
    public void Update_ReplacesTheClassWithTheSameGuid_AndSaves()
    {
        var fake = WithClass();
        string guid = EventClassXml.Parse(fake.TypeSystem.Last())!.Guid;

        string result = Coordinator(fake).Update("MyEvents", null, [new(1, "Started", "Warning", "x"), new(3, "Third", null, null)]);

        result.Should().StartWith($"Updated event class 'MyEvents' {guid} (GUID kept)");
        result.Should().Contain("Added events: 3 Third.").And.Contain("Removed events: 2 Failed");
        result.Should().Contain("Project saved.");
        fake.CallLog.Should().ContainInOrder("LogoutPlc()", $"ReplaceTypeSystemType({guid},MyEvents)", "SaveAll()");
        fake.CallLog.Should().NotContain(c => c.StartsWith("EditFiles", StringComparison.Ordinal));
        EventClassXml.Parse(fake.TypeSystem.Last())!.Events.Select(e => e.Id).Should().Equal(1u, 3u);
    }

    [Fact]
    public void Update_PinnedClass_IsPinnedAgainWithTheNewContent()
    {
        var fake = WithClass();
        Coordinator(fake).AddGlobalDataType("MyEvents", null);
        fake.CallLog.Clear();

        string result = Coordinator(fake).Update("MyEvents", "renamed", null);

        result.Should().Contain("Pinned event class 'MyEvents'").And.Contain("ExternalTypes.tmc^Global.ST_MyEvents")
            .And.Contain("the update dropped the pin");
        fake.CallLog.Should().ContainInOrder("SaveAll()", "EditFilesWithSolutionClosed(ExternalTypes.tmc,Plc.plcproj)");
        EventClassXml.EventClassesInTmc(fake.Files[ExternalTypes]).Single().DisplayText.Should().Be("renamed");
    }

    [Fact]
    public void Update_Unchanged_DoesNothing()
    {
        var fake = WithClass();

        Coordinator(fake).Update("MyEvents", "My events", null).Should().Contain("is unchanged");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Update_NothingGiven_IsRefused()
    {
        var fake = WithClass();
        Coordinator(fake).Update("MyEvents", null, null).Should().StartWith("[Error] Nothing to update");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Update_BuiltIn_IsRefused()
    {
        var fake = WithClass();
        Coordinator(fake).Update("TcSystemEventClass", "x", null).Should().StartWith("[Error] 'TcSystemEventClass' is a built-in");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Update_ClassFromATmcFile_IsRefused()
    {
        var fake = OpenFake();
        fake.TypeSystem.Add("<DataType><Name GUID=\"{E9FD4BA4-0DA2-44D6-B26C-5E46503FE62A}\">FromTmc</Name><DisplayName TxtId=\"\"><![CDATA[x]]></DisplayName>" +
                            "<EventId><Name Id=\"1\">A</Name><DisplayName TxtId=\"\"><![CDATA[a]]></DisplayName><Severity>Info</Severity></EventId></DataType>");

        string result = Coordinator(fake).Update("FromTmc", "y", null);

        result.Should().StartWith("[Error] 'FromTmc'").And.Contain("comes from a TMC file").And.Contain("same GUID");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void Update_UnknownClass_IsRefused()
    {
        var fake = WithClass();
        Coordinator(fake).Update("Nope", "y", null).Should().StartWith("[Error] No event class 'Nope'");
        Changed(fake).Should().BeFalse();
    }

    // ---- add global data type -------------------------------------------------------------------

    [Fact]
    public void AddGlobalDataType_WritesExternalTypesAndThePlcprojEntry_AndReadsThePinBack()
    {
        var fake = WithClass();

        string result = Coordinator(fake).AddGlobalDataType("MyEvents", null);

        result.Should().StartWith("Pinned event class 'MyEvents'").And.Contain("into PLC 'Plc'")
            .And.Contain("ExternalTypes.tmc^Global.ST_MyEvents").And.Contain("saved, closed and reopened");
        result.Should().NotContain("[Error]").And.NotContain("[Warn]");
        fake.CallLog.Should().ContainInOrder("LogoutPlc()", "EditFilesWithSolutionClosed(ExternalTypes.tmc,Plc.plcproj)", "ReadPlcExternalTypes()");
        fake.Files[Plcproj].Should().Contain("<None Include=\"ExternalTypes.tmc\">");
        EventClassXml.EventClassesInTmc(fake.Files[ExternalTypes]).Single().Name.Should().Be("MyEvents");
    }

    [Fact]
    public void AddGlobalDataType_AlreadyPinnedAndUpToDate_DoesNothing()
    {
        var fake = WithClass();
        Coordinator(fake).AddGlobalDataType("MyEvents", null);
        fake.CallLog.Clear();

        Coordinator(fake).AddGlobalDataType("MyEvents", "Plc").Should().Contain("already pinned").And.Contain("up to date");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void AddGlobalDataType_XaeShowsNoPinAfterReopen_IsAnError()
    {
        var fake = WithClass();
        fake.ReopenShowsNoPins = true;

        Coordinator(fake).AddGlobalDataType("MyEvents", null).Should().Contain("[Error] PLC 'Plc': ExternalTypes.tmc was written")
            .And.Contain("no ExternalTypes.tmc node");
    }

    [Fact]
    public void AddGlobalDataType_EditFails_IsAnError()
    {
        var fake = WithClass();
        fake.EditFilesThrows = new InvalidOperationException("The solution did not close; no file was changed.");

        Coordinator(fake).AddGlobalDataType("MyEvents", null).Should().StartWith("[Error] The solution did not close")
            .And.Contain("did not complete");
    }

    [Fact]
    public void AddGlobalDataType_Refusals()
    {
        var fake = WithClass();
        Coordinator(fake).AddGlobalDataType("Nope", null).Should().StartWith("[Error] No event class 'Nope'").And.Contain("event classes only");
        Coordinator(fake).AddGlobalDataType("TcSystemEventClass", null).Should().StartWith("[Error] 'TcSystemEventClass' is a built-in");
        Coordinator(fake).AddGlobalDataType("MyEvents", "Other").Should().StartWith("[Error] PLC project 'Other' not found");
        Coordinator(fake).AddGlobalDataType("1bad", null).Should().StartWith("[Error] typeName");
        fake.EventPlcs.Add(("Plc2", @"C:\sln\Plc2\Plc2.plcproj"));
        Coordinator(fake).AddGlobalDataType("MyEvents", null).Should().StartWith("[Error] 2 PLC projects").And.Contain("plcProjectName");
        Changed(fake).Should().BeFalse();
    }
}
