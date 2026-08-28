using System.IO;

namespace AuraFlow.App.Services;

/// <summary>Tiny file logger - %APPDATA%\AuraFlow\logs\auraflow.log</summary>
public static class Log
{
    private static readonly object Lock = new();

    public static string Folder => Path.Combine(SettingsService.Folder, "logs");
    public static string FilePath => Path.Combine(Folder, "auraflow.log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex is not null)
                {
                    line += Environment.NewLine + ex;
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);

                // keep the file bounded
                var fi = new FileInfo(FilePath);
                if (fi.Length > 512 * 1024)
                {
                    File.WriteAllText(FilePath, string.Empty);
                }
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
