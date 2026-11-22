using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The text files of every language are complete and consistent: the same keys as English, the same placeholders, no
/// empty text, every key the code asks for exists, no key is left unused, and the prompts of a language carry the same
/// placeholders, tool names and code spans as the English ones. The texts of a language are checked as files (so a
/// mistake shows with its key); one test also loads them through the real lookup.
/// </summary>
public class LocalizationCompletenessTests
{
    private static readonly string HostDir = FindHostDir();

    private static string FindHostDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "PtcXaeMcp.WinForms");
            if (File.Exists(Path.Combine(candidate, "Resources", "Strings.resx"))) return candidate;
        }
        throw new DirectoryNotFoundException("src\\PtcXaeMcp.WinForms was not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Others => HostLanguage.Shipped.Select(l => l.Code).Where(c => c != HostLanguage.Fallback);

    private static string ResxPath(string language) =>
        Path.Combine(HostDir, "Resources", language == HostLanguage.Fallback ? "Strings.resx" : $"Strings.{language}.resx");

    private static Dictionary<string, string> Read(string language)
    {
        var doc = XDocument.Load(ResxPath(language));
        return doc.Root!.Elements("data")
            .Where(d => d.Attribute("type") is null && d.Attribute("mimetype") is null)
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);
    }

    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    public static TheoryData<string> OtherLanguages() => new(Others);

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void EveryLanguage_HasExactlyTheKeysOfEnglish(string language)
    {
        var english = Read("en").Keys;
        var other = Read(language).Keys;
        english.Except(other).Should().BeEmpty($"{language} lacks these keys");
        other.Except(english).Should().BeEmpty($"{language} has keys English does not have");
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void EveryText_HasTheSamePlaceholdersAsEnglish(string language)
    {
        var english = Read("en");
        var other = Read(language);
        var wrong = english.Where(e => other.TryGetValue(e.Key, out var text) &&
                Placeholder.Matches(e.Value).Select(m => m.Value).Order().SequenceEqual(
                    Placeholder.Matches(text).Select(m => m.Value).Order()) is false)
            .Select(e => e.Key).ToList();
        wrong.Should().BeEmpty($"{language}: the placeholders ({{0}}, {{1}}) differ from English");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("it")]
    public void NoText_IsEmpty(string language) =>
        Read(language).Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).Should().BeEmpty();

    [Fact]
    public void German_UsesSwissSpelling() =>
        Read("de").Where(p => p.Value.Contains('ß')).Select(p => p.Key).Should().BeEmpty("the Swiss spelling writes ss");

    [Fact]
    public void AnAccelerator_IsKeptInEveryLanguage()
    {
        var english = Read("en");
        foreach (string language in Others)
        {
            var other = Read(language);
            var lost = english.Where(e => other.TryGetValue(e.Key, out var t)
                    && HasAccelerator(e.Value) != HasAccelerator(t)).Select(e => e.Key).ToList();
            lost.Should().BeEmpty($"{language}: a '&' accelerator was added or lost");
        }
    }

    private static bool HasAccelerator(string text) => Regex.IsMatch(text.Replace("&&", string.Empty), @"&\S");

    // ---- the code and the files agree ------------------------------------------------------------------------

    // A key is written as a string literal in the code, also inside `cond ? "a.b" : "c.d"` or a switch; so every literal
    // that looks like a key (dotted, lower-case start) is looked at, not only the ones right after Loc.Get(.
    private static readonly Regex KeyLike = new(@"""(?<key>[a-z][a-zA-Z0-9_]*(?:\.[a-zA-Z0-9_]+)+)""", RegexOptions.Compiled);

    private static readonly string[] FileExtensions =
        [".json", ".md", ".xml", ".exe", ".dll", ".txt", ".log", ".csv", ".html", ".sln", ".tmc", ".resx", ".png", ".ico"];

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(HostDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.EndsWith("Loc.cs", StringComparison.Ordinal));

    private static IEnumerable<(string File, string Literal)> KeyLiterals() =>
        SourceFiles().SelectMany(f => KeyLike.Matches(File.ReadAllText(f))
            .Select(m => (Path.GetFileName(f), m.Groups["key"].Value)));

    [Fact]
    public void EveryKeyLikeLiteralOfAKeyArea_IsInTheEnglishFile()
    {
        var english = Read("en").Keys.ToHashSet();
        var areas = english.Select(k => k[..k.IndexOf('.')]).ToHashSet();
        var missing = KeyLiterals()
            .Where(t => areas.Contains(t.Literal[..t.Literal.IndexOf('.')]) && !english.Contains(t.Literal)
                     && !FileExtensions.Any(e => t.Literal.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Select(t => $"{t.File}: {t.Literal}").Distinct().ToList();
        missing.Should().BeEmpty("a key the code asks for must exist (else the label shows the key)");
    }

    [Fact]
    public void NoEnglishKeyIsUnused()
    {
        var used = KeyLiterals().Select(t => t.Literal).ToHashSet();
        Read("en").Keys.Where(k => !used.Contains(k)).Should().BeEmpty("a key nobody asks for is dead text in four files");
    }

    // ---- the lookup finds the satellite files at run time ---------------------------------------------------------

    [Fact]
    public void TheLookup_FindsEachLanguage_AndFallsBackToEnglishForTheRest()
    {
        Loc.Get("settings.language.label", "en").Should().Be("Language");
        Loc.Get("settings.language.label", "de").Should().Be("Sprache");
        Loc.Get("settings.language.label", "fr").Should().Be("Langue");
        Loc.Get("settings.language.label", "it").Should().Be("Lingua");
        Loc.Get("settings.language.label", "ja").Should().Be("Language", "no Japanese texts: English");
    }

    // ---- prompts -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("it")]
    public void EveryText_CanBeFilledWithItsArguments(string language)
    {
        // A stray '{' or '}' in a translation would throw in string.Format: Loc would then show the raw template.
        var broken = new List<string>();
        foreach (var (key, text) in Read(language))
        {
            int count = Placeholder.Matches(text).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 1).DefaultIfEmpty(0).Max();
            object[] args = Enumerable.Range(0, count).Select(i => (object)$"<{i}>").ToArray();
            try { _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args); }
            catch (FormatException) { broken.Add(key); }
        }
        broken.Should().BeEmpty($"{language}: these texts are not valid format strings");
    }

    private static readonly Regex PromptParts =new(@"\{\{[a-zA-Z]+\}\}|`[^`\n]+`|\btwincat_[a-z0-9_]+", RegexOptions.Compiled);

    public static TheoryData<string, string> PromptsPerLanguage()
    {
        var data = new TheoryData<string, string>();
        foreach (string language in Others)
            foreach (string name in McpPrompts.Needs.Keys.Order())
                data.Add(language, name);
        return data;
    }

    [Theory]
    [MemberData(nameof(PromptsPerLanguage))]
    public void ATranslatedPrompt_KeepsPlaceholdersToolNamesAndCodeSpans(string language, string name)
    {
        string english = File.ReadAllText(Path.Combine(HostDir, "Mcp", "Prompts", $"{name}.md"));
        string path = Path.Combine(HostDir, "Mcp", "Prompts", language, $"{name}.md");
        File.Exists(path).Should().BeTrue($"{language} has a text for the prompt {name}");
        string translated = File.ReadAllText(path);

        PromptParts.Matches(translated).Select(m => m.Value).Order()
            .Should().Equal(PromptParts.Matches(english).Select(m => m.Value).Order(),
                $"{language}/{name}: placeholders, tool names and code spans stay exactly as in English");
        translated.Split('\n').Length.Should().Be(english.Split('\n').Length, $"{language}/{name}: same number of lines");
        translated.Should().NotContain("ß", "no sharp s in the Swiss spelling");
    }

    [Theory]
    [MemberData(nameof(PromptsPerLanguage))]
    public void ATranslatedPrompt_IsServedInThatLanguage(string language, string name)
    {
        string english = McpPrompts.Text(name, "en");
        string translated = McpPrompts.Text(name, language);
        translated.Should().NotBe(english, $"the embedded {language} text of {name} is not the English one");
    }
}
