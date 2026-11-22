using System.Globalization;
using System.Resources;

namespace PtcXaeMcp.WinForms.Localization;

/// <summary>
/// The texts a human reads, by key: <c>Resources\Strings.resx</c> is the English text of every key, the satellite files
/// (<c>Strings.de.resx</c>, ...) carry the other languages. A key or a language that is missing, or an empty text, gives
/// the English text; a key that is not in the English file at all gives the key itself (visible, never an empty label).
/// The language is <see cref="HostLanguage.Current"/>, passed to the lookup explicitly: no thread culture changes,
/// so number and date formatting elsewhere stays as it was.
/// </summary>
internal static class Loc
{
    internal const string ResourceBaseName = "PtcXaeMcp.WinForms.Resources.Strings";

    private static readonly ResourceManager Manager = new(ResourceBaseName, typeof(Loc).Assembly);

    /// <summary>The text of <paramref name="key"/> in the host language.</summary>
    public static string Get(string key) => Get(key, HostLanguage.Current);

    /// <summary>The text of <paramref name="key"/> in <paramref name="language"/>, else in English, else the key.</summary>
    public static string Get(string key, string language)
    {
        if (!string.Equals(language, HostLanguage.Fallback, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string? text = Manager.GetString(key, CultureInfo.GetCultureInfo(language));
                // GetString falls back to the neutral text itself when the language has no value: only a
                // genuinely translated, non-empty text counts here, so an empty satellite entry cannot blank a label.
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch (CultureNotFoundException) { }
            catch (MissingManifestResourceException) { }
        }
        return English(key);
    }

    /// <summary>The English text of <paramref name="key"/> (what the wire protocol uses), or the key when it has none.</summary>
    public static string English(string key)
    {
        try { return Manager.GetString(key, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : key; }
        catch (MissingManifestResourceException) { return key; }
    }

    /// <summary>
    /// <see cref="Get(string)"/> with <c>{0}</c>, <c>{1}</c> ... filled by <paramref name="args"/>, formatted with the
    /// invariant culture. A text whose placeholders do not fit the arguments is returned as it is, never an exception.
    /// </summary>
    public static string Format(string key, params object?[] args) => Fill(Get(key), args);

    /// <summary>The English counterpart of <see cref="Format"/>.</summary>
    public static string FormatEnglish(string key, params object?[] args) => Fill(English(key), args);

    private static string Fill(string template, object?[] args)
    {
        try { return string.Format(CultureInfo.InvariantCulture, template, args); }
        catch (FormatException) { return template; }
    }
}
