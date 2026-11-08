using System.Text.Json;
using FluentResults;

namespace WindowsUtils.AI;

/// <summary>Persisted chat settings (endpoint, model, reasoning effort). Same JSON shape as before.</summary>
public sealed record ChatSettings(string Endpoint, string Model, string? ReasoningEffort);

/// <summary>Loads/saves chat settings as JSON. Defaults to %AppData%\WindowsUtils\chat.json.
/// I/O and JSON errors come back as a failed <see cref="Result"/> (an <see cref="ExceptionalError"/>), not as exceptions.</summary>
public static class ChatSettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsUtils", "chat.json");

    /// <summary>The saved settings, or a null value when nothing was saved yet.</summary>
    public static Result<ChatSettings?> Load(string? path = null)
    {
        path ??= DefaultPath;
        return Result.Try(() => File.Exists(path)
            ? JsonSerializer.Deserialize<ChatSettings>(File.ReadAllText(path))
            : null);
    }

    public static Result Save(ChatSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        return Result.Try(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    public static Result Delete(string? path = null)
    {
        path ??= DefaultPath;
        return Result.Try(() =>
        {
            if (File.Exists(path))
                File.Delete(path);
        });
    }
}
