using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class ErrorListDedupTests
{
    private static string Read(params BuildError[] entries)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.FakeErrorList.AddRange(entries);
        return new BuildCoordinator(fake).GetErrorList();
    }

    private static BuildError Message(string text) => new("message", text, null, null);

    [Fact]
    public void NoDuplicates_KeepsTheHeaderAndLinesAsBefore()
    {
        string text = Read(
            new BuildError("error", "C0035: unknown", "POUs/MAIN.TcPOU", 4),
            new BuildError("warning", "C0371: unused", null, null));

        Assert.Equal(
            "2 entries (1 error, 1 warning, 0 message)." + Environment.NewLine +
            "[ERROR] (POUs/MAIN.TcPOU:4) C0035: unknown" + Environment.NewLine +
            "[WARNING] C0371: unused",
            text);
    }

    [Fact]
    public void IdenticalEntries_AreMergedWithTheirCount()
    {
        string text = Read(Message("Alarm 7 raised"), Message("Alarm 7 raised"));

        Assert.StartsWith("2 entries (1 distinct; 0 error, 0 warning, 1 message).", text);
        Assert.Contains("[MESSAGE] Alarm 7 raised (2× in the Error List)", text);
        Assert.Single(text.Split(Environment.NewLine), l => l.Contains("Alarm 7 raised", StringComparison.Ordinal));
    }

    [Fact]
    public void OrderIsThatOfTheFirstOccurrence()
    {
        string text = Read(Message("B"), Message("A"), Message("B"), Message("C"), Message("A"), Message("B"));
        string[] lines = text.Split(Environment.NewLine);

        Assert.StartsWith("6 entries (3 distinct;", lines[0]);
        Assert.Equal("[MESSAGE] B (3× in the Error List)", lines[1]);
        Assert.Equal("[MESSAGE] A (2× in the Error List)", lines[2]);
        Assert.Equal("[MESSAGE] C", lines[3]);
    }

    [Fact]
    public void EntriesThatDifferInLevelFileOrLine_StayApart()
    {
        string text = Read(
            new BuildError("error", "same text", "A.TcPOU", 1),
            new BuildError("warning", "same text", "A.TcPOU", 1),
            new BuildError("error", "same text", "B.TcPOU", 1),
            new BuildError("error", "same text", "A.TcPOU", 2),
            new BuildError("error", "same text", "A.TcPOU", 1));

        Assert.StartsWith("5 entries (4 distinct; 3 error, 1 warning, 0 message).", text);
        Assert.Contains("[ERROR] (A.TcPOU:1) same text (2× in the Error List)", text);
        Assert.Contains("[WARNING] (A.TcPOU:1) same text" + Environment.NewLine, text + Environment.NewLine);
        Assert.Contains("[ERROR] (B.TcPOU:1) same text", text);
        Assert.Contains("[ERROR] (A.TcPOU:2) same text", text);
    }

    [Fact]
    public void EmptyList_IsStillTheEmptyMessage() =>
        Assert.Equal("Error list is empty.", Read());
}
