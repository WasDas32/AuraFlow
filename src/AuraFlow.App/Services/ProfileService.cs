using System.IO;
using System.Text.Json;
using AuraFlow.App.Models;

namespace AuraFlow.App.Services;

/// <summary>Loads/saves %APPDATA%\AuraFlow\profile.json (per-device layer stacks).</summary>
public static class ProfileService
{
    private static readonly object Lock = new();

    private static string FilePath => Path.Combine(SettingsService.Folder, "profile.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        IncludeFields = false,
    };

    public static ProfileDocument Load()
    {
        lock (Lock)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(FilePath), JsonOpts) ?? new ProfileDocument();
                }
            }
            catch
            {
            }

            return new ProfileDocument();
        }
    }

    public static void Save(ProfileDocument profile)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(SettingsService.Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(profile, JsonOpts));
            }
            catch
            {
            }
        }
    }
}
