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
    public void Report_SuccessNeedsTheProjectAndAPublishFinishedEventSinceTheStart()
    {
        var before = new HmiPublishTargetState(Project, "1.0.0.0", "Demo", "admin", null, "2026-11-19T13:57:44.47Z", "2026-11-19T13:54:24.58Z");
        var ok = HmiCoordinator.PublishReport(Url, before, Run(new(true, false, null, null, null, null, 48.1)),
            before with { ServerTime = "2026-11-19T13:58:33.16Z", LastPublished = "2026-11-19T13:58:12.4544936Z" });
        Assert.Equal($"Published '{Project}' to {Url} — confirmed by the engineer, result code none, completed unknown, 48.1 s; " +
                     $"verified: the target logged 'Publish finished' at 2026-11-19T13:58:12.4544936Z and reports project '{Project}' 1.0.0.0 " +
                     $"(before: '{Project}').", ok.Summary);

        // same project, but the newest "Publish finished" is older than the start: not proven
        var stale = HmiCoordinator.PublishReport(Url, before, Run(new(true, false, null, null, null, null, 3)), before);
        Assert.StartsWith("[Warn] the XAE finished the publish", stale.Summary, StringComparison.Ordinal);
        Assert.Contains("no 'Publish finished' since the publish began", stale.Summary, StringComparison.Ordinal);

        var other = HmiCoordinator.PublishReport(Url, Empty, Run(new(true, false, true, 3, 7, null, 3)),
            new HmiPublishTargetState("OtherHmi", "", "Demo", "admin", null));
        Assert.StartsWith("[Error] the XAE finished the publish", other.Summary, StringComparison.Ordinal);
        Assert.Contains("reports project 'OtherHmi'", other.Summary, StringComparison.Ordinal);
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
    public void Report_AFailedPublish_PointsToTheXaeLog_AndNamesTheRestoredProfile()
    {
        var run = new HmiPublishRun(Project, new HmiPublishOutcome(true, false, true, 2, 1, null, 4), null, "confirmed by the engineer", "restored (x)");
        var (summary, json) = HmiCoordinator.PublishReport(Url, Empty, run, new HmiPublishTargetState("", "", null, null, null));
        Assert.StartsWith("[Error] the XAE finished the publish (result code 2, completed True, 4.0 s)", summary, StringComparison.Ordinal);
        Assert.Contains("the Publish dialog of the XAE shows the publish log.", summary, StringComparison.Ordinal);
        Assert.Contains("Profile file: restored (x).", summary, StringComparison.Ordinal);
        Assert.Equal(2, JsonNode.Parse(json)!["publish"]!["resultCode"]!.GetValue<int>());
    }

    [Fact]
    public void TargetState_ReadsProjectLicenseAndUser_AndNamesAMissingRight()
    {
        var ok = HmiCoordinator.TargetState(JsonNode.Parse("""
            { "commands": [
              { "symbol": "GetServerInformation", "readValue": { "projectName": "PlantHmi", "projectVersion": "1.2.0.0", "serverTime": "2026-11-19T13:59:08Z" } },
              { "symbol": "Diagnostics", "readValue": { "LICENSE": { "STATE": "OK" } } },
              { "symbol": "GetCurrentUser", "readValue": { "name": "admin" } },
              { "symbol": "ListEvents", "readValue": [
                  { "domain": "TcHmiSrv", "name": "PROJECT_PUBLISHED", "localizedString": "Publish finished", "payloadType": 0, "payload": { "severity": 1, "timeRaised": "2026-11-19T13:54:24.3592198Z" } },
                  { "domain": "TcHmiSrv", "name": "PROJECT_PUBLISHED", "localizedString": "Publish finished", "payload": { "severity": 1, "timeRaised": "2026-11-19T13:58:12.4544936Z" } },
                  { "domain": "TcHmiSrv", "name": "PROJECT_PUBLISH_START", "localizedString": "Publish started", "payload": { "severity": 1, "timeRaised": "2026-11-19T13:59:00Z" } } ] } ] }
            """)!.AsObject());
        Assert.Equal(new HmiPublishTargetState("PlantHmi", "1.2.0.0", "OK", "admin", null, "2026-11-19T13:59:08Z", "2026-11-19T13:58:12.4544936Z"), ok);

        var guest = HmiCoordinator.TargetState(JsonNode.Parse("""
            { "commands": [ { "symbol": "GetServerInformation", "error": { "code": 4101, "message": "INSUFFICIENT_ACCESS" } } ] }
            """)!.AsObject());
        Assert.StartsWith(HmiCoordinator.NoRightsPrefix, guest.Error, StringComparison.Ordinal);
    }
}
