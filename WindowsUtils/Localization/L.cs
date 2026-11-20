using System.Globalization;
using System.Resources;

namespace WindowsUtils.Localization;

/// <summary>
/// UI text lookup. The key is the English text itself, so code stays readable and a missing
/// translation simply shows English: <c>L.T("Refresh")</c>, <c>L.F("{0} programs", count)</c>.
/// Translations live in <c>Localization/Strings.&lt;culture&gt;.resx</c> (only <c>tr</c> today); the neutral
/// <c>Strings.resx</c> stays empty. The language is <see cref="CultureInfo.CurrentUICulture"/>, set once at
/// startup by <see cref="LanguageSettings.Apply"/> (Windows display language unless the user picked one).
/// Only the UI is translated: AI Chat tool output and the wutils CLI stay English on purpose (the model
/// reads tool text, and scripts parse CLI output).
/// </summary>
internal static class L
{
    private static readonly ResourceManager Resources = new("WindowsUtils.Localization.Strings", typeof(L).Assembly);

    /// <summary>The translation of <paramref name="text"/> for the current UI language, or the text itself.</summary>
    public static string T(string text) => Resources.GetString(text) ?? text;

    /// <summary>Translates a composite format string, then fills it in with the user's regional format.</summary>
    public static string F(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(format), args);

    /// <summary>
    /// Translates a value that comes from Core in English (states, health names, operation names, ...),
    /// looked up under the key <c>value:&lt;text&gt;</c> so it cannot clash with a UI sentence.
    /// Unknown values (e.g. "State 13") are shown as they are.
    /// </summary>
    public static string V(string? value) =>
        string.IsNullOrEmpty(value) ? "" : Resources.GetString("value:" + value) ?? value;
}
