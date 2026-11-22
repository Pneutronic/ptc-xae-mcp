using System.Text.RegularExpressions;
using FluentAssertions;
using PtcXaeMcp.WinForms.Localization;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The installer script is checked as text (the compiler is not part of the test run): every wizard language has its
/// own texts, a reinstall in another language cleans up the old shortcut, and what the installer leaves for the app is
/// what the app reads.
/// </summary>
public class InstallerLanguageTests
{
    private static readonly string Script = ReadScript();

    private static string ReadScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "setup", "setup.iss");
            if (File.Exists(candidate)) return File.ReadAllText(candidate).Replace("\r\n", "\n");
        }
        throw new FileNotFoundException("setup\\setup.iss was not found above " + AppContext.BaseDirectory);
    }

    private static IReadOnlyList<string> Section(string name)
    {
        var m = Regex.Match(Script, $@"^\[{name}\]\n(.*?)(?=^\[|\z)", RegexOptions.Singleline | RegexOptions.Multiline);
        m.Success.Should().BeTrue($"[{name}] exists");
        return m.Groups[1].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';')).ToList();
    }

    private static List<string> Languages() =>
        Section("Languages").Select(l => Regex.Match(l, "^Name: \"([a-z]+)\"").Groups[1].Value).ToList();

    private static Dictionary<string, string> ShortcutNames() =>
        Section("CustomMessages")
            .Select(l => Regex.Match(l, @"^([a-z]+)\.UninstallShortcut=(.+)$"))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    [Fact]
    public void English_IsTheFirstLanguage_AndEveryLanguageHasItsOwnFile()
    {
        var languages = Languages();
        languages[0].Should().Be("english", "English is the fallback and comes first");
        languages.Should().OnlyHaveUniqueItems();
        languages.Should().Contain(["german", "french", "italian"], "the languages of the host come with the installer");
        Section("Languages").Should().OnlyContain(l => l.Contains("compiler:"), "only Inno's own language files, no foreign ones");
    }

    [Fact]
    public void EveryLanguage_HasAnUninstallShortcutName_AndNothingIsDefinedForAnUnlistedLanguage()
    {
        ShortcutNames().Keys.Should().BeEquivalentTo(Languages());
        ShortcutNames().Values.Should().OnlyContain(v => v.Contains("{#AppName}"));
    }

    [Fact]
    public void AReinstallInAnotherLanguage_RemovesTheShortcutOfEveryLanguage()
    {
        var deletes = Section("InstallDelete");
        foreach (var (language, name) in ShortcutNames())
            deletes.Should().Contain($"Type: files; Name: \"{{group}}\\{name}.lnk\"", $"the {language} shortcut is cleaned up");
    }

    [Fact]
    public void TheTaskTexts_ComeFromInnosOwnMessages_NotFromAGermanOnlyLiteral()
    {
        Script.Should().NotContain("Verknüpfung", "no German literal outside a language block");
        var tasks = string.Join("\n", Section("Tasks"));
        tasks.Should().Contain("{cm:CreateDesktopIcon}").And.Contain("{cm:AdditionalIcons}");
    }

    [Fact]
    public void TheValueTheInstallerLeaves_IsTheValueTheAppReads()
    {
        var registry = string.Join("\n", Section("Registry"));
        registry.Should().Contain($"Subkey: \"{HostLanguage.InstallerKeyPath}\"");
        registry.Should().Contain($"ValueName: \"{HostLanguage.InstallerValueName}\"");
        registry.Should().Contain("Root: HKLM");
    }

    [Fact]
    public void TheCodeMapsEveryHostLanguage_ToItsCode()
    {
        var inno = new Dictionary<string, string> { ["en"] = "english", ["de"] = "german", ["fr"] = "french", ["it"] = "italian" };
        foreach (var language in HostLanguage.Shipped)
        {
            inno.Should().ContainKey(language.Code, "the test needs the Inno name of every shipped host language");
            Script.Should().Contain($"ActiveLanguage = '{inno[language.Code]}' then Result := '{language.Code}'");
            Languages().Should().Contain(inno[language.Code], "the installer offers every language the app ships");
        }
    }
}
