using System.Text.Json;

namespace WindowsUtils.AI;

/// <summary>Persisted chat settings (endpoint, model, reasoning effort). Same JSON shape as before.</summary>
public sealed record ChatSettings(string Endpoint, string Model, string? ReasoningEffort);

/// <summary>Loads/saves chat settings as JSON. Defaults to %AppData%\WindowsUtils\chat.json.</summary>
public static class ChatSettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsUtils", "chat.json");

    public static ChatSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<ChatSettings>(File.ReadAllText(path));
    }

    public static void Save(ChatSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Delete(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
            File.Delete(path);
    }
}
