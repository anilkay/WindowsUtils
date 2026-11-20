using System.Globalization;
using System.Text.Json;
using FluentResults;
using Microsoft.Extensions.Logging;
using WindowsUtils.Core.Logging;

namespace WindowsUtils.Localization;

/// <summary>
/// The UI language the user picked (or none: follow Windows), stored in
/// <c>%AppData%\WindowsUtils\settings.json</c> as <c>{"language":"tr"}</c>.
/// </summary>
internal static class LanguageSettings
{
    /// <summary>Languages with a translation, by culture name; "" means the Windows display language.</summary>
    public static readonly (string Culture, string NativeName)[] Supported =
    [
        ("en", "English"),
        ("tr", "Türkçe"),
    ];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsUtils", "settings.json");

    /// <summary>The Windows display language, captured before <see cref="Apply"/> changes the UI culture.</summary>
    public static CultureInfo SystemUICulture { get; } = CultureInfo.CurrentUICulture;

    private sealed record SettingsFile(string? Language);

    /// <summary>The saved language, or null when the app follows Windows (also when nothing is saved yet).</summary>
    public static Result<string?> Load()
    {
        if (!File.Exists(FilePath))
            return Result.Ok<string?>(null);
        try
        {
            var settings = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(FilePath), JsonOptions);
            return Result.Ok(Normalize(settings?.Language));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result.Fail(new ExceptionalError($"Could not read the language setting: {ex.Message}", ex));
        }
    }

    /// <summary>Saves the language; null or "" goes back to following Windows.</summary>
    public static Result Save(string? language)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new SettingsFile(Normalize(language)), JsonOptions));
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(new ExceptionalError($"Could not save the language setting: {ex.Message}", ex));
        }
    }

    /// <summary>Sets the UI culture for the whole process from the saved setting. Call once, before any form is created.
    /// Only the UI language changes; dates and numbers keep the Windows regional format.</summary>
    public static void Apply()
    {
        var saved = Load();
        if (saved.IsFailed)
            AppLog.Logger("WindowsUtils").LogWarning("{Message}", saved.Errors[0].Message);
        if (saved.ValueOrDefault is not { } language)
            return;
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    /// <summary>The language that is in effect now (saved choice, or the Windows language if it has a translation, else English).</summary>
    public static string Effective(string? saved) =>
        saved ?? (Supported.Any(s => s.Culture == SystemUICulture.TwoLetterISOLanguageName) ? SystemUICulture.TwoLetterISOLanguageName : "en");

    private static string? Normalize(string? language) =>
        Supported.FirstOrDefault(s => string.Equals(s.Culture, language?.Trim(), StringComparison.OrdinalIgnoreCase)).Culture;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

