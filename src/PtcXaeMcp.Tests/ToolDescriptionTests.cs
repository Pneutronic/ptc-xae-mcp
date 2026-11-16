using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Some clients list a tool only by its first sentence, clipped at 80 characters (Hermes Agent's tool catalog). The
/// first sentence of every description therefore has to say what the tool does within those 80 characters.
/// </summary>
public sealed class ToolDescriptionTests
{
    // A sentence ends at . ! or ? followed by whitespace or the end; e.g./i.e./etc. do not end one.
    private static readonly Regex SentenceEnd = new(@"(?<!\be\.g)(?<!\bi\.e)(?<!\betc)[.!?](?=\s|$)");

    internal static string FirstSentence(string description)
    {
        string text = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var m = SentenceEnd.Match(text);
        return m.Success ? text[..(m.Index + 1)] : text;
    }

    [Theory]
    [InlineData("Read a file. More text.", "Read a file.")]
    [InlineData("List items, e.g. POUs. More.", "List items, e.g. POUs.")]
    [InlineData("No end", "No end")]
    public void FirstSentence_StopsAtTheFirstSentenceEnd(string description, string expected) =>
        Assert.Equal(expected, FirstSentence(description));

    [Fact]
    public void EveryToolsFirstSentence_FitsIn80Characters()
    {
        var tooLong = typeof(TwinCatMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => (Name: m.GetCustomAttribute<McpServerToolAttribute>()!.Name,
                          First: FirstSentence(m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "")))
            .Where(t => t.First.Length is 0 or > 80)
            .Select(t => $"{t.Name} ({t.First.Length}): {t.First}")
            .ToList();

        Assert.True(tooLong.Count == 0, "First sentence missing or longer than 80 characters:\n" + string.Join("\n", tooLong));
    }
}
