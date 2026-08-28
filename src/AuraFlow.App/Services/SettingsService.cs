using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuraFlow.App.Services;

public class AppSettings
{
    public string OpenRgbExePath { get; set; } = "";
    public int Port { get; set; } = 6742;
    public double Fps { get; set; } = 30;
    public bool StartMinimized { get; set; } = true;
    public bool AutostartWithWindows { get; set; } = true;
    public bool BlackoutOnStart { get; set; }
}

/// <summary>Loads/saves %APPDATA%\AuraFlow\settings.json.</summary>
public static class SettingsService
{
    private static readonly object Lock = new();
    private static AppSettings? _cached;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuraFlow");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings LoadOrDefault()
    {
        lock (Lock)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    _cached = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings();
                    return _cached;
                }
            }
            catch
            {
            }

            _cached = new AppSettings();
            return _cached;
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Lock)
        {
            _cached = settings;
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
            }
            catch
            {
            }
        }
    }
}
