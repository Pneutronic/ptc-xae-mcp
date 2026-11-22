using FluentAssertions;
using PtcXaeMcp.WinForms.Dialogs;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Theme;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The host language: which language is chosen, how a text is looked up and that a missing key or language never
/// leaves an empty label. No test here changes <see cref="HostLanguage.Current"/> (it is process-wide and other tests
/// read English texts); they use the explicit-language overloads.
/// </summary>
public class HostLanguageTests
{
    private static Func<string, string?> Env(string? language = null) =>
        n => n == HostLanguage.EnvVar ? language : null;

    [Theory]
    [InlineData("en", "en")]
    [InlineData("de", "de")]
    [InlineData("DE", "de")]
    [InlineData(" fr ", "fr")]
    [InlineData("it-CH", "it")]
    [InlineData("de_AT", "de")]
    [InlineData("de-CH", "de")]
    [InlineData("German", "de")]
    [InlineData("Deutsch", "de")]
    [InlineData("Français", "fr")]
    [InlineData("italian", "it")]
    [InlineData("es-MX", "es")]
    [InlineData("Español", "es")]
    [InlineData("pt-BR", "pt")]
    [InlineData("pt-PT", "pt")]
    [InlineData("Português (Brasil)", "pt")]
    [InlineData("nl-BE", "nl")]
    [InlineData("pl-PL", "pl")]
    [InlineData("tr-TR", "tr")]
    [InlineData("Turkish", "tr")]
    [InlineData("zh", "zh")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-SG", "zh")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("zh-Hans-CN", "zh")]
    [InlineData("中文（简体）", "zh")]
    public void Match_AcceptsCodesRegionalFormsAndNames(string value, string expected) =>
        HostLanguage.Match(value).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xx")]
    [InlineData("zh-TW")]
    [InlineData("zh-HK")]
    [InlineData("zh_MO")]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hant-TW")]
    [InlineData("ja-JP")]
    [InlineData("ru")]
    [InlineData("Deutschland")]
    [InlineData("d")]
    public void Match_RejectsEmptyAndUnknown(string? value) => HostLanguage.Match(value).Should().BeNull();

    [Fact]
    public void Resolve_TakesTheSourcesInTheirOrder()
    {
        HostLanguage.Resolve(Env("fr"), "de", "it", "en-US", out _).Should().Be("fr", "the environment wins");
        HostLanguage.Resolve(Env(), "de", "it", "fr-FR", out _).Should().Be("de", "the saved choice beats the installer");
        HostLanguage.Resolve(Env(), null, "it", "fr-FR", out _).Should().Be("it", "the installer's value beats Windows");
        HostLanguage.Resolve(Env(), null, null, "fr-FR", out _).Should().Be("fr", "Windows decides last");
        HostLanguage.Resolve(Env(), null, null, "de-CH", out _).Should().Be("de");
        HostLanguage.Resolve(Env(), null, null, null, out _).Should().Be("en");
    }

    [Fact]
    public void Resolve_SkipsAnUnknownSourceInsteadOfFailing()
    {
        HostLanguage.Resolve(Env(), "xx", "yy", "ja-JP", out _).Should().Be("en", "no shipped language anywhere");
        HostLanguage.Resolve(Env(), "xx", "it", "ja-JP", out _).Should().Be("it", "an unknown saved value is skipped");
    }

    [Fact]
    public void Resolve_ReportsAnEnvironmentValueItIgnored()
    {
        HostLanguage.Resolve(Env("klingon"), "de", null, null, out string? ignored).Should().Be("de");
        ignored.Should().Be("klingon");
        HostLanguage.Resolve(Env("fr"), "de", null, null, out ignored).Should().Be("fr");
        ignored.Should().BeNull();
        HostLanguage.Resolve(Env(""), "de", null, null, out ignored).Should().Be("de");
        ignored.Should().BeNull("an empty variable is not set");
    }

    [Fact]
    public void Apply_OfEnglishOrAnUnknownCode_KeepsEnglish()
    {
        HostLanguage.Apply("xx");
        HostLanguage.Apply("en");
        HostLanguage.Current.Should().Be("en");
    }

    [Fact]
    public void Shipped_StartsWithEnglish_AndEveryCodeMatchesItself()
    {
        HostLanguage.Shipped[0].Code.Should().Be(HostLanguage.Fallback);
        HostLanguage.Shipped[0].Reviewed.Should().BeTrue("English is the source");
        HostLanguage.Shipped.Select(l => l.Code).Should().OnlyHaveUniqueItems();
        foreach (var l in HostLanguage.Shipped)
        {
            HostLanguage.Match(l.Code).Should().Be(l.Code);
            HostLanguage.Match(l.NativeName).Should().Be(l.Code);
        }
    }

    // ---- Loc ---------------------------------------------------------------------------------------------

    [Fact]
    public void Loc_EnglishTextOfAKnownKey()
    {
        Loc.English("settings.language.label").Should().Be("Language");
        Loc.Get("settings.language.label", "en").Should().Be("Language");
    }

    [Fact]
    public void Loc_AnUnknownKey_GivesTheKey_NeverAnEmptyLabel()
    {
        Loc.English("no.such.key").Should().Be("no.such.key");
        Loc.Get("no.such.key", "de").Should().Be("no.such.key");
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("")]
    [InlineData("not a culture!")]
    public void Loc_AnUnknownLanguage_FallsBackToEnglish(string language) =>
        Loc.Get("settings.language.label", language).Should().Be("Language");

    [Fact]
    public void Loc_Format_FillsPlaceholders_AndNeverThrowsOnAMismatch()
    {
        Loc.FormatEnglish("settings.language.envOverride", "PTC_XAE_MCP_LANGUAGE", "de")
            .Should().Be("PTC_XAE_MCP_LANGUAGE=de is set and overrides this choice.");
        // Too few arguments for {0}={1}: the template comes back, the call does not throw.
        Loc.FormatEnglish("settings.language.envOverride", "only-one").Should().Contain("{1}");
    }

    // ---- ui.json -----------------------------------------------------------------------------------------

    [Fact]
    public void UiSettings_Language_RoundTrips_AndAnOldFileHasNone()
    {
        UiSettings.Parse("""{ "theme": "dark" }""").Language.Should().BeNull("an old ui.json has no choice");
        var s = new UiSettings { Language = "fr" };
        UiSettings.Parse(s.ToJson()).Language.Should().Be("fr");
        new UiSettings().ToJson().Should().NotContain("language", "no choice is written until one is made");
    }

    [Fact]
    public void UiSettings_AnUnknownOrWrongTypedLanguage_MeansNoChoice()
    {
        UiSettings.Parse("""{ "language": "tlh" }""").Language.Should().BeNull();
        UiSettings.Parse("""{ "language": 7 }""").Language.Should().BeNull();
        UiSettings.Parse("""{ "language": "de-CH" }""").Language.Should().Be("de");
    }

    // ---- prompts and the settings row ------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("xx")]
    public void Prompts_EveryPromptHasATextInEveryLanguage_OrFallsBackToEnglish(string language)
    {
        foreach (string name in McpPrompts.Needs.Keys)
            McpPrompts.Text(name, language).Should().NotBeNullOrWhiteSpace($"{name} in {language}");
    }

    [Fact]
    public void SettingsInfo_ShowsTheLanguageRow_AndNamesAnIgnoredEnvironmentValue()
    {
        var ok = SettingsInfo.Rows(5010, false, n => null).Single(r => r.Name == "Language");
        ok.Value.Should().Be(HostLanguage.Current);
        ok.HowToChange.Should().Contain(HostLanguage.EnvVar).And.Contain(string.Join(", ", HostLanguage.Shipped.Select(l => l.Code)));

        var bad = SettingsInfo.Rows(5010, false, n => n == HostLanguage.EnvVar ? "klingon" : null).Single(r => r.Name == "Language");
        bad.Value.Should().Contain("klingon").And.Contain("ignored");
    }
}
