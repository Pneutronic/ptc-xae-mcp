using System.Text.Json.Nodes;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>twincat_hmi_publish: popup, publish call, report from the target before and after, and the test seam
/// that never auto-confirms a publish to another machine.</summary>
public sealed class HmiPublishTests
{
    private const string Project = "PlantHmi";
    private const string Url = "https://127.0.0.1:2020";
    private static readonly HmiPublishTargetState Empty = new("", "", "Demo", "admin", null);

    private static FakeTwinCatSession Session() => new()
    {
        IsConnected = true,
        SolutionOpen = true,
        ActiveSlnPath = @"C:\T\run\Fixture.sln",
        HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Project, @"C:\T\run\PlantHmi", true, false)], null),
    };

    private static HmiPublishRun Publish(FakeTwinCatSession fake, IHmiWriteConfirmer? confirmer, HmiPublishTargetState? before = null) =>
        new HmiCoordinator(fake, confirmer).PublishProject(null, "Plant", Url, "admin", 300, 120, before ?? Empty);

    [Fact]
    public void Publish_AsksFirst_ShowsTargetProfileAndWhatRunsThere_ThenPublishesByName()
    {
        var fake = Session();
        var popup = new FakeHmiConfirmer();
        HmiPublishRun run = Publish(fake, popup);

        Assert.Null(run.Refusal);
        var request = Assert.Single(popup.Requests).Request;
        Assert.Equal(HmiWriteKind.Publish, request.Kind);
        Assert.Equal(Url, request.Symbol);
        Assert.Equal("no project published (empty server)", request.CurrentValue);
        Assert.Equal($"project '{Project}', publish profile 'Plant' (user 'admin')", request.NewValue);
        Assert.Contains($"PublishHmiProject({Project}, profile=Plant, {Url}, timeout=300s)", fake.CallLog);
        Assert.Contains("Target", WinForms.HmiConfirmText.Details(request), StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_AnUnreadableTarget_IsShownAsSuch()
    {
        var popup = new FakeHmiConfirmer();
        Publish(Session(), popup, HmiPublishTargetState.Failed(HmiCoordinator.NoRightsPrefix + " — log in"));
        Assert.StartsWith("(not readable: the server does not let this user read", Assert.Single(popup.Requests).Request.CurrentValue,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePublishProfile_ReadsTargetAndUser_OrListsTheSavedOnes()
    {
        const string saved = """
            [{"profileName":"Plant","tcHmiDestinationUrl":"https://192.168.1.10:2020","tcHmiServerUserName":"admin","encryptedTcHmiServerPassword":"abc"},
             {"profileName":"Lab","tcHmiServerHost":"10.0.0.5","tcHmiServerPort":1020,"tcHmiUseTLS":false}]
            """;
        var (plant, _) = HmiCoordinator.ParsePublishProfile(saved, "Plant", Project);
        Assert.Equal(new HmiCoordinator.HmiPublishProfileInfo("Plant", "https://192.168.1.10:2020", "admin", true), plant);
        var (lab, _) = HmiCoordinator.ParsePublishProfile(saved, "Lab", Project);
        Assert.Equal("http://10.0.0.5:1020", lab!.Url);
        Assert.False(lab.HasPassword);
        var (none, error) = HmiCoordinator.ParsePublishProfile(saved, "plant", Project);
        Assert.Null(none);
        Assert.Contains("saved: Plant, Lab.", error, StringComparison.Ordinal);
        Assert.Contains("(none saved)", HmiCoordinator.ParsePublishProfile("[]", "Plant", Project).Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "rejected")]
    [InlineData(false, "expired")]
    public void Publish_NotConfirmed_PublishesNothing(bool rejected, string word)
    {
        var fake = Session();
        HmiPublishRun run = Publish(fake, new FakeHmiConfirmer(rejected ? HmiConfirmOutcome.Rejected : HmiConfirmOutcome.TimedOut));
        Assert.Contains(word, run.Refusal, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was published.", run.Refusal, StringComparison.Ordinal);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("PublishHmiProject", StringComparison.Ordinal));
    }

    [Fact]
    public void Publish_WithoutAHostWindow_IsRefused() =>
        Assert.StartsWith("[Error] No confirmation channel", Publish(Session(), null).Refusal, StringComparison.Ordinal);

    [Fact]
    public void AutoConfirm_ATempProject_OnlyForATargetOnThisMachine()
    {
        var popup = new FakeHmiConfirmer(HmiConfirmOutcome.Rejected);
        var seam = new HmiTempAutoConfirmer(popup, () => "1", () => @"C:\T", p => p);
        HmiWriteRequest To(string url) => new(HmiWriteKind.Publish, Project, @"C:\T\run\PlantHmi", url, null, null, null, false);

        Assert.Equal(HmiConfirmOutcome.AutoConfirmed, seam.Confirm(To("https://127.0.0.1:2020"), TimeSpan.FromSeconds(1)));
        Assert.Equal(HmiConfirmOutcome.AutoConfirmed, seam.Confirm(To("http://localhost:1020"), TimeSpan.FromSeconds(1)));
        Assert.Empty(popup.Requests);
        Assert.Equal(HmiConfirmOutcome.Rejected, seam.Confirm(To("https://192.168.1.10:2020"), TimeSpan.FromSeconds(1)));
        Assert.Single(popup.Requests);
    }

    private static HmiPublishRun Run(HmiPublishOutcome o) => new(Project, o, null, "confirmed by the engineer");

    [Fact]
    public void Report_SuccessOnlyWhenTheTargetReportsTheProject()
    {
        var ok = HmiCoordinator.PublishReport(Url, Empty, Run(new(true, false, true, 0, 7, null, 12.3)),
            new HmiPublishTargetState(Project, "1.0.0.0", "Demo", "admin", null));
        Assert.StartsWith($"Published '{Project}' to {Url} — confirmed by the engineer, result code 0, completed True, 12.3 s; " +
                          $"verified: the target now reports project '{Project}' 1.0.0.0 (before: none).", ok.Summary, StringComparison.Ordinal);
        Assert.Equal(7, JsonNode.Parse(ok.Json)!["publish"]!["submissionId"]!.GetValue<int>());

        var other = HmiCoordinator.PublishReport(Url, Empty, Run(new(true, false, true, 0, 7, null, 3)),
            new HmiPublishTargetState("OtherHmi", "", "Demo", "admin", null));
        Assert.StartsWith("[Error] the XAE finished the publish", other.Summary, StringComparison.Ordinal);
        Assert.Contains("reports project 'OtherHmi'", other.Summary, StringComparison.Ordinal);

        var same = HmiCoordinator.PublishReport(Url, new HmiPublishTargetState(Project, "", null, null, null),
            Run(new(true, false, true, 0, 1, null, 3)), new HmiPublishTargetState(Project, "", null, null, null));
        Assert.Contains("ran the same project before", same.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_NotStarted_StillRunning_AndUnreadableTarget()
    {
        Assert.StartsWith("[Error] the XAE did not start the publish of 'PlantHmi'", HmiCoordinator.PublishReport(Url, Empty,
            Run(new(false, false, null, null, null, "a publish of this project is already running", 0)), null).Summary, StringComparison.Ordinal);
        Assert.StartsWith("[Warn] the publish of 'PlantHmi' to https://127.0.0.1:2020 was still running after 300 s",
            HmiCoordinator.PublishReport(Url, Empty, Run(new(true, true, null, null, null, null, 300)), null).Summary, StringComparison.Ordinal);
        Assert.StartsWith("[Warn] the XAE finished the publish", HmiCoordinator.PublishReport(Url, Empty,
            Run(new(true, false, true, 0, 1, null, 3)), HmiPublishTargetState.Failed("login failed")).Summary, StringComparison.Ordinal);
        Assert.Equal("[Error] nope", HmiCoordinator.PublishReport(Url, Empty, new HmiPublishRun("", null, "[Error] nope", null), null).Summary);
    }

    [Fact]
    public void ProfileFile_TheSavedPasswordProfileIsRolledBack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ptc-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, HmiCoordinator.PublishConfigFile);
            File.WriteAllText(path, "[]");
            byte[] before = File.ReadAllBytes(path);
            Assert.Equal("unchanged", HmiCoordinator.RestoreProfileFile(path, before));

            File.WriteAllText(path, """[{"profileName":"McpPublish","encryptedTcHmiServerPassword":"x"}]""");
            Assert.StartsWith("restored", HmiCoordinator.RestoreProfileFile(path, before), StringComparison.Ordinal);
            Assert.Equal("[]", File.ReadAllText(path));

            File.WriteAllText(path, "[{}]");
            Assert.StartsWith("restored", HmiCoordinator.RestoreProfileFile(path, null), StringComparison.Ordinal);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Report_NamesTheXaeMessagesAndTheRestoredProfile()
    {
        var run = new HmiPublishRun(Project, new HmiPublishOutcome(true, false, false, 3, 1, null, 4,
            ["[type 0, level 1] Connecting", "[type 2, level 3] Authentication failed"]), null, "confirmed by the engineer", "restored (x)");
        var (summary, json) = HmiCoordinator.PublishReport(Url, Empty, run, new HmiPublishTargetState("", "", null, null, null));
        Assert.Contains("The XAE said (2 message(s)): [type 2, level 3] Authentication failed.", summary, StringComparison.Ordinal);
        Assert.Equal("[type 0, level 1] Copy a.view", HmiCoordinator.PublishMessage("""{"MessageType":0,"ImportanceLevel":1,"Message":"Copy a.view"}"""));
        Assert.Equal("plain text", HmiCoordinator.PublishMessage(" plain text "));
        Assert.Equal(["[type 0, level 1] c", "[type 0, level 1] d", "[type 0, level 1] e"],
            HmiCoordinator.NotablePublishMessages(["[type 0, level 1] a", "[type 0, level 1] b", "[type 0, level 1] c", "[type 0, level 1] d", "[type 0, level 1] e"]));
        Assert.Contains("Profile file: restored (x).", summary, StringComparison.Ordinal);
        Assert.Equal(2, JsonNode.Parse(json)!["publish"]!["messages"]!.AsArray().Count);
    }

    [Fact]
    public void TargetState_ReadsProjectLicenseAndUser_AndNamesAMissingRight()
    {
        var ok = HmiCoordinator.TargetState(JsonNode.Parse("""
            { "commands": [
              { "symbol": "GetServerInformation", "readValue": { "projectName": "PlantHmi", "projectVersion": "1.2.0.0" } },
              { "symbol": "Diagnostics", "readValue": { "LICENSE": { "STATE": "OK" } } },
              { "symbol": "GetCurrentUser", "readValue": { "name": "admin" } } ] }
            """)!.AsObject());
        Assert.Equal(new HmiPublishTargetState("PlantHmi", "1.2.0.0", "OK", "admin", null), ok);

        var guest = HmiCoordinator.TargetState(JsonNode.Parse("""
            { "commands": [ { "symbol": "GetServerInformation", "error": { "code": 4101, "message": "INSUFFICIENT_ACCESS" } } ] }
            """)!.AsObject());
        Assert.StartsWith(HmiCoordinator.NoRightsPrefix, guest.Error, StringComparison.Ordinal);
    }
}
