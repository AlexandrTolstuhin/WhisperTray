using System.IO;
using System.Text.Json;

namespace WhisperTray;

internal sealed class UserSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperTray",
        "settings.json");

    public string ModelId { get; set; } = "large-v3";

    public string? InputDeviceId { get; set; }

    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings()
                : new UserSettings();
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
        catch (IOException)
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
