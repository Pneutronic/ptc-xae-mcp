using System.Text;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// A build result lists the XAE's Error List, which keeps every message since the XAE started. These pin
/// that errors and warnings stay complete while the messages are limited, so the verdict stays readable.
/// </summary>
public class BuildErrorListFormatTests
{
    private static string Format(IEnumerable<BuildError> entries)
    {
        var sb = new StringBuilder();
        BuildCoordinator.AppendErrorListEntries(sb, entries.ToList());
        return sb.ToString();
    }

    private static BuildError Message(int i) => new("message", $"device 'EL{i}' already exists in file 'x.xml'", null, null);

    [Fact]
    public void ManyMessages_AreLimitedWithACountOfTheRest()
    {
        var entries = Enumerable.Range(1, 125).Select(Message).ToList();

        string text = Format(entries);

        Assert.Equal(BuildCoordinator.MaxListedMessages, text.Split('\n').Count(l => l.StartsWith("[MESSAGE]", StringComparison.Ordinal)));
        Assert.Contains($"[Note] {125 - BuildCoordinator.MaxListedMessages} more messages in the Error List not shown", text);
        Assert.Contains("twincat_get_error_list", text);
    }

    [Fact]
    public void ErrorsAndWarnings_AreNeverCutAndComeFirst()
    {
        var entries = Enumerable.Range(1, 30).Select(Message)
            .Append(new BuildError("error", "C0035: unknown", "POUs/MAIN.TcPOU", 4))
            .Append(new BuildError("warning", "C0371: unused", null, null))
            .ToList();

        string[] lines = Format(entries).Split(Environment.NewLine);

        Assert.Equal("[ERROR] (POUs/MAIN.TcPOU:4) C0035: unknown", lines[0]);
        Assert.Equal("[WARNING] C0371: unused", lines[1]);
    }

    [Fact]
    public void FewMessages_AreListedWithoutANote_AndDuplicatesOnce()
    {
        string text = Format([Message(1), Message(1), Message(2)]);

        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("[MESSAGE]", StringComparison.Ordinal)));
        Assert.DoesNotContain("[Note]", text);
    }
}
