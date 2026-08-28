using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;

namespace AuraFlow.App.Services;

/// <summary>
/// Locates, installs and launches the OpenRGB server, and registers the elevated
/// logon task so no UAC prompt is needed at every boot.
/// </summary>
public static class OpenRgbProcessManager
{
    public const string DefaultInstallDirName = "OpenRGB";
    public const string DownloadUrl =
        "https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_candidate_1.0rc3/OpenRGB_1.0rc3_Windows_64_6fbcf62.zip";
    public const string TaskName = "AuraFlowOpenRGBServer";

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraFlow", DefaultInstallDirName);

    public static string ExePath(string? configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        candidates.Add(Path.Combine(DefaultInstallDir, "OpenRGB.exe"));
        candidates.Add(@"C:\Program Files\OpenRGB\OpenRGB.exe");
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe"));

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    public static bool IsServerUp(int port)
    {
        try
        {
            using var c = new TcpClient();
            var ar = c.BeginConnect("127.0.0.1", port, null, null);
            if (ar.AsyncWaitHandle.WaitOne(400))
            {
                c.EndConnect(ar);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts OpenRGB headless with the SDK server. Returns process or null.</summary>
    public static Process? StartServer(string exePath, int port)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--server --server-host 127.0.0.1 --server-port {port}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
        };
        return Process.Start(psi);
    }

    /// <summary>Kills any running OpenRGB instances.</summary>
    public static void KillRunningServers()
    {
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Starts the registered logon task on demand (schtasks /Run). Because the task is
    /// registered with highest privileges, this launches OpenRGB elevated WITHOUT a UAC prompt.
    /// Waits for the SDK port to come up.
    /// </summary>
    public static bool TryStartViaTask(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/Run /TN {TaskName}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                p?.WaitForExit(8000); // don't check exit code - a slow /Run still starts the task
            }

            return WaitForServer(port, 25_000);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Waits for the SDK port to accept connections.</summary>
    public static bool WaitForServer(int port, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (IsServerUp(port))
            {
                return true;
            }

            Thread.Sleep(400);
        }

        return false;
    }

    /// <summary>Downloads and extracts OpenRGB into %LOCALAPPDATA%\AuraFlow\OpenRGB.</summary>
    public static async Task InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        // Stop any running instance so we can replace files.
        KillRunningServers();

        if (Directory.Exists(DefaultInstallDir))
        {
            try
            {
                Directory.Delete(DefaultInstallDir, true);
            }
            catch
            {
            }
        }

        Directory.CreateDirectory(DefaultInstallDir);
        string zip = Path.Combine(Path.GetTempPath(), "AuraFlow_OpenRGB.zip");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(zip);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                progress?.Report(total is > 0 ? read * 100.0 / total.Value : 0);
            }
        }

        string extractDir = DefaultInstallDir + "_extract";
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, true);
        }

        ZipFile.ExtractToDirectory(zip, extractDir);

        // The zip wraps everything in a single top-level folder ("OpenRGB Windows 64-bit") -
        // detect that and strip it so OpenRGB.exe lands directly in DefaultInstallDir.
        string sourceRoot = extractDir;
        var topLevelDirs = Directory.EnumerateDirectories(extractDir).ToList();
        var topLevelFiles = Directory.EnumerateFiles(extractDir).ToList();
        if (topLevelFiles.Count == 0 && topLevelDirs.Count == 1)
        {
            sourceRoot = topLevelDirs[0];
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceRoot, file);
            string target = Path.Combine(DefaultInstallDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, true);
        }

        Directory.Delete(extractDir, true);
        try
        {
            File.Delete(zip);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Registers a scheduled task that starts the OpenRGB server at logon with highest
    /// privileges (no UAC prompt at logon). Triggers one UAC elevation now.
    /// </summary>
    public static bool RegisterLogonTask(string exePath, int port)
    {
        string args = $"/Create /F /TN {TaskName} /TR \"\\\"{exePath}\\\" --server --server-host 127.0.0.1 --server-port {port}\" /SC ONLOGON /RL HIGHEST";
        return RunElevated("schtasks", args);
    }

    public static bool UnregisterLogonTask()
    {
        return RunElevated("schtasks", $"/Delete /F /TN {TaskName}");
    }

    public static bool IsTaskRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/Query /TN {TaskName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static bool RunElevated(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas", // triggers UAC once
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p is { ExitCode: 0 };
        }
        catch
        {
            return false; // user cancelled UAC
        }
    }
}
