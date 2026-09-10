using System.IO;
using System.Text.Json;
using VoidGrab.Models;

namespace VoidGrab.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in the roaming profile.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Directory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoidGrab");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            // A corrupt or unreadable settings file is not worth blocking start-up
            // over — defaults are always a valid place to carry on from.
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Losing a preference is a far smaller problem than crashing on exit.
        }
    }
}
