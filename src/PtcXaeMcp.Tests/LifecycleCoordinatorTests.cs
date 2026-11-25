using System.Runtime.InteropServices;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class LifecycleCoordinatorTests
{
    private static string TempSln()
    {
        var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sln");
        File.WriteAllText(p, "dummy");
        return p;
    }

    [Fact]
    public async Task OpenProject_InvalidPath_ReturnsErrorWithoutTouchingSession()
    {
        var fake = new FakeTwinCatSession { IsConnected = true };
        var coord = new LifecycleCoordinator(fake);

        var result = await coord.OpenProjectAsync("not_absolute.sln", false, CancellationToken.None);

        Assert.StartsWith("[Error]", result);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public async Task OpenProject_NoSolutionLoaded_OpensIt()
    {
        var sln = TempSln();
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        var coord = new LifecycleCoordinator(fake);

        try
        {
            var result = await coord.OpenProjectAsync(sln, false, CancellationToken.None);

            Assert.StartsWith("Opened", result);
            Assert.Contains("OpenSolution", string.Join(",", fake.CallLog));
            Assert.DoesNotContain("CloseSolution", string.Join(",", fake.CallLog));
        }
        finally { File.Delete(sln); }
    }

    [Fact]
    public async Task OpenProject_SolutionOpenWithoutCloseFlag_Errors()
    {
        var sln = TempSln();
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = @"C:\other.sln"
        };
        var coord = new LifecycleCoordinator(fake);

        try
        {
            var result = await coord.OpenProjectAsync(sln, closeCurrentFirst: false, CancellationToken.None);

            Assert.StartsWith("[Error]", result);
            Assert.Contains("already open", result);
            Assert.DoesNotContain("OpenSolution", string.Join(",", fake.CallLog));
        }
        finally { File.Delete(sln); }
    }

    [Fact]
    public async Task OpenProject_SolutionOpenWithCloseFlag_ClosesThenOpens()
    {
        var sln = TempSln();
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = @"C:\other.sln"
        };
        var coord = new LifecycleCoordinator(fake);

        try
        {
            var result = await coord.OpenProjectAsync(sln, closeCurrentFirst: true, CancellationToken.None);

            Assert.StartsWith("Opened", result);
            var log = string.Join("|", fake.CallLog);
            var closeIdx = log.IndexOf("CloseSolution", StringComparison.Ordinal);
            var openIdx = log.IndexOf("OpenSolution(", StringComparison.Ordinal);
            Assert.True(closeIdx >= 0 && openIdx >= 0 && closeIdx < openIdx,
                $"Close must precede Open. Log: {log}");
        }
        finally { File.Delete(sln); }
    }

    [Fact]
    public async Task OpenProject_NotConnected_LaunchesAndPasses_SlnPath()
    {
        var sln = TempSln();
        var fake = new FakeTwinCatSession { IsConnected = false };
        fake.ConnectCallback = () => { fake.SolutionOpen = true; fake.ActiveSlnPath = sln; return true; };
        var coord = new LifecycleCoordinator(fake);

        try
        {
            var result = await coord.OpenProjectAsync(sln, false, CancellationToken.None);

            Assert.StartsWith("Opened", result);
            Assert.Contains($"ConnectOrLaunch(launchWith={sln})", fake.CallLog);
            Assert.DoesNotContain("OpenSolution", string.Join(",", fake.CallLog));
        }
        finally { File.Delete(sln); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);

    private static string ShortPath(string path)
    {
        var buffer = new char[1024];
        uint length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : path;
    }

    [SkippableFact]
    public async Task OpenProject_LaunchedWithAShortPath_IsItsOwnSolution_NotAlreadyOpen()
    {
        // Seen live: the open launched the XAE with C:\Users\SOMEUS~1\…\X.sln, the XAE reported
        // C:\Users\some.user\…\X.sln, and the open answered "A solution is already open".
        var dir = Path.Combine(Path.GetTempPath(), "long directory name " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var longSln = Path.Combine(dir, "Fixture Solution.sln");
        File.WriteAllText(longSln, "dummy");
        try
        {
            string shortSln = ShortPath(longSln);
            Skip.If(string.Equals(shortSln, longSln, StringComparison.OrdinalIgnoreCase),
                "This volume creates no 8.3 short names.");

            var fake = new FakeTwinCatSession { IsConnected = false };
            fake.ConnectCallback = () => { fake.SolutionOpen = true; fake.ActiveSlnPath = longSln; return true; };
            var result = await new LifecycleCoordinator(fake).OpenProjectAsync(shortSln, false, CancellationToken.None);

            Assert.StartsWith("Opened", result);
            Assert.DoesNotContain("OpenSolution", string.Join(",", fake.CallLog));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task OpenProject_ConnectFailsToReachIde_ReturnsError()
    {
        var sln = TempSln();
        var fake = new FakeTwinCatSession { IsConnected = false };
        var coord = new LifecycleCoordinator(fake);

        try
        {
            var result = await coord.OpenProjectAsync(sln, false, CancellationToken.None);

            Assert.StartsWith("[Error]", result);
            Assert.Contains("Unable to connect", result);
        }
        finally { File.Delete(sln); }
    }

    [Fact]
    public void SaveProject_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var coord = new LifecycleCoordinator(fake);

        var result = coord.SaveProject();

        Assert.StartsWith("[Error]", result);
        Assert.Contains("No TwinCAT XAE", result);
        Assert.DoesNotContain("SaveAll", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void SaveProject_NoSolutionOpen_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        var coord = new LifecycleCoordinator(fake);

        var result = coord.SaveProject();

        Assert.StartsWith("[Error]", result);
        Assert.Contains("No solution is currently open", result);
        Assert.DoesNotContain("SaveAll", string.Join(",", fake.CallLog));
    }

    [Fact]
    // Was "CallsSaveAll": SaveProject now goes through SaveProjectVerified, which
    // saves via the Document object model and then checks the FILES on disk. The
    // old path only fired the File.SaveAll shell command and reported success
    // without looking at anything — that is the bug this replaced.
    public void SaveProject_HappyPath_VerifiesAndReportsPath()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = @"C:\projects\test.sln"
        };
        var coord = new LifecycleCoordinator(fake);

        var result = coord.SaveProject();

        Assert.StartsWith("Saved", result);
        Assert.Contains(@"C:\projects\test.sln", result);
        Assert.Contains("SaveProjectVerified", string.Join(",", fake.CallLog));
    }

    // The reported incident, pinned down: a document reports itself as saved but
    // the file on disk never changed. The old implementation could not see this
    // at all — it read back through the same in-memory accessors it had written.
    // It must now surface as an explicit failure, because building or committing
    // from that stale file is exactly the damage that followed.
    [Fact]
    public void SaveProject_FileOnDiskUnchanged_ReportedAsFailure()
    {
        var stale = new DateTime(2026, 9, 23, 8, 0, 0, DateTimeKind.Utc);
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = @"C:\projects\test.sln",
            FakeSaveProjectResult = new SaveProjectResult(
                false, @"C:\projects\test.sln",
                new[]
                {
                    new SavedDocument(@"C:\projects\FB_SafePartnerLogic.TcPOU",
                        SavedFlagAfter: true, MtimeBefore: stale, MtimeAfter: stale),
                },
                "[Error] Save did NOT reach the disk for: FB_SafePartnerLogic.TcPOU " +
                "[savedFlag=True, fileChanged=False]. The file on disk is still the old version.")
        };

        var result = new LifecycleCoordinator(fake).SaveProject();

        Assert.StartsWith("[Error]", result);
        Assert.Contains("FB_SafePartnerLogic.TcPOU", result);
        Assert.Contains("NOT FLUSHED", result);
    }

    [Fact]
    public void SaveProject_FlushedDocument_ReportedAsOk()
    {
        var before = new DateTime(2026, 9, 23, 8, 0, 0, DateTimeKind.Utc);
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = @"C:\projects\test.sln",
            FakeSaveProjectResult = new SaveProjectResult(
                true, @"C:\projects\test.sln",
                new[]
                {
                    new SavedDocument(@"C:\projects\FB_Foo.TcPOU",
                        SavedFlagAfter: true, MtimeBefore: before, MtimeAfter: before.AddSeconds(2)),
                },
                "Saved and verified on disk: 1 document(s) flushed.")
        };

        var result = new LifecycleCoordinator(fake).SaveProject();

        Assert.DoesNotContain("NOT FLUSHED", result);
        Assert.Contains("verified on disk", result);
    }

    [Fact]
    public void CloseProject_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var coord = new LifecycleCoordinator(fake);

        var result = coord.CloseProject(saveFirst: true);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("No TwinCAT XAE", result);
        Assert.DoesNotContain("CloseSolution", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void CloseProject_NoSolutionOpen_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        var coord = new LifecycleCoordinator(fake);

        var result = coord.CloseProject(saveFirst: true);

        Assert.StartsWith("[Error]", result);
        Assert.Contains("No solution is currently open", result);
        Assert.DoesNotContain("CloseSolution", string.Join(",", fake.CallLog));
    }

    [Theory]
    [InlineData(true, "Saved and closed")]
    [InlineData(false, "Closed (without save)")]
    public void CloseProject_HappyPath_PassesSaveFirstAndReports(bool saveFirst, string expectedPrefix)
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = @"C:\projects\test.sln"
        };
        var coord = new LifecycleCoordinator(fake);

        var result = coord.CloseProject(saveFirst);

        Assert.StartsWith(expectedPrefix, result);
        Assert.Contains(@"C:\projects\test.sln", result);
        Assert.Contains($"CloseSolution(saveFirst={saveFirst})", fake.CallLog);
    }
    [Fact]
    public void CloseProject_ReportsTheAnsweredSavePrompt()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = @"C:\projects\test.sln",
            CloseResult = new CloseSolutionResult("'TcXaeShell' answered 'Speichern' (UIA Invoke) — changes saved by TwinCAT"),
        };

        var result = new LifecycleCoordinator(fake).CloseProject(saveFirst: true);

        Assert.StartsWith("Saved and closed", result);
        Assert.Contains("XAE save prompt: 'TcXaeShell' answered 'Speichern'", result);
    }

    [Fact]
    public void CloseProject_NoPrompt_SaysNothingAboutOne()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, ActiveSlnPath = @"C:\p\a.sln" };

        var result = new LifecycleCoordinator(fake).CloseProject(saveFirst: true);

        Assert.DoesNotContain("prompt", result);
    }

    [Fact]
    public void CloseProject_ShellSaveFallback_IsReported()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true, SolutionOpen = true, ActiveSlnPath = @"C:\p\a.sln",
            CloseResult = new CloseSolutionResult(ShellSaveFallback: true),
        };

        var result = new LifecycleCoordinator(fake).CloseProject(saveFirst: true);

        Assert.StartsWith("Saved and closed", result);
        Assert.Contains("save-on-close", result);
    }

    [Fact]
    public void CloseProject_SolutionStillOpenAfterClose_IsAnError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true, SolutionOpen = true, ActiveSlnPath = @"C:\p\a.sln",
            CloseLeavesSolutionOpen = true,
            CloseResult = new CloseSolutionResult("'TcXaeShell' could NOT be answered 'Speichern' — cancelled instead", SavePromptFailed: true),
        };

        var result = new LifecycleCoordinator(fake).CloseProject(saveFirst: true);

        Assert.StartsWith("[Error] The solution is still open", result);
        Assert.Contains("cancelled instead", result);
    }

    [Fact]
    public void CloseProject_PromptFailedButClosed_IsAnError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true, SolutionOpen = true, ActiveSlnPath = @"C:\p\a.sln",
            CloseResult = new CloseSolutionResult("x", SavePromptFailed: true),
        };

        var result = new LifecycleCoordinator(fake).CloseProject(saveFirst: false);

        Assert.StartsWith("[Error] Closed", result);
    }

    [Fact]
    public void CloseProject_CloseThrows_ReturnsError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true, SolutionOpen = true,
            CloseThrows = new InvalidOperationException("boom"),
        };

        var result = new LifecycleCoordinator(fake).CloseProject(saveFirst: true);

        Assert.Equal("[Error] Close failed: boom", result);
    }
}
