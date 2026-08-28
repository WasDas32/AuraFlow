using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using AuraFlow.App.Models;
using AuraFlow.App.Services;
using AuraFlow.OpenRgb;

namespace AuraFlow.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly OpenRgbClient _client;
    private readonly LightingEngine _engine;
    private readonly ProfileDocument _profile;
    private readonly System.Timers.Timer _saveDebounce;
    private readonly DispatcherTimerWrapper _previewTimer;
    private readonly DispatcherTimerWrapper _watchdog;

    public AppSettings Settings { get; }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    private DeviceViewModel? _selectedDevice;
    public DeviceViewModel? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }

    private bool _settingsPageVisible;
    public bool SettingsPageVisible
    {
        get => _settingsPageVisible;
        set
        {
            if (Set(ref _settingsPageVisible, value) && value)
            {
                RefreshTaskStatus();
            }
        }
    }

    private string _statusText = "Starting…";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private bool _connected;
    public bool Connected { get => _connected; set => Set(ref _connected, value); }

    private bool _blackout;
    public bool Blackout
    {
        get => _blackout;
        set
        {
            if (Set(ref _blackout, value))
            {
                _engine.SetBlackout(value);
            }
        }
    }

    // ---- settings page state
    private string _openRgbPath = "";
    public string OpenRgbPath { get => _openRgbPath; set => Set(ref _openRgbPath, value); }

    private string _resolvedExe = "";
    public string ResolvedExe { get => _resolvedExe; set => Set(ref _resolvedExe, value); }

    private bool _exeFound;
    public bool ExeFound { get => _exeFound; set => Set(ref _exeFound, value); }

    private double _fps = 30;
    public double Fps { get => _fps; set { if (Set(ref _fps, value)) { Settings.Fps = value; SaveSettings(); } } }

    private bool _startMinimized = true;
    public bool StartMinimized { get => _startMinimized; set { if (Set(ref _startMinimized, value)) { Settings.StartMinimized = value; SaveSettings(); } } }

    private bool _autostart = true;
    public bool Autostart { get => _autostart; set { if (Set(ref _autostart, value)) { Settings.AutostartWithWindows = value; SaveSettings(); UpdateAutostart(); } } }

    private string _installStatus = "";
    public string InstallStatus { get => _installStatus; set => Set(ref _installStatus, value); }

    private double _installProgress;
    public double InstallProgress { get => _installProgress; set => Set(ref _installProgress, value); }

    private bool _taskRegistered;
    public bool TaskRegistered { get => _taskRegistered; set => Set(ref _taskRegistered, value); }

    private bool _installing;
    public bool Installing { get => _installing; set => Set(ref _installing, value); }

    // ---- diagnostics
    private bool _serverUp;
    public bool ServerUp { get => _serverUp; set => Set(ref _serverUp, value); }

    private int _deviceCount;
    public int DeviceCount { get => _deviceCount; set => Set(ref _deviceCount, value); }

    private string _diagnosticHint = "";
    public string DiagnosticHint { get => _diagnosticHint; set => Set(ref _diagnosticHint, value); }

    public RelayCommand AddStaticCommand { get; }
    public RelayCommand AddRainbowCycleCommand { get; }
    public RelayCommand AddRainbowWaveCommand { get; }
    public RelayCommand AddBreathingCommand { get; }
    public RelayCommand AddBlinkCommand { get; }
    public RelayCommand RemoveLayerCommand { get; }
    public RelayCommand MoveLayerUpCommand { get; }
    public RelayCommand MoveLayerDownCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand BackFromSettingsCommand { get; }
    public RelayCommand InstallOpenRgbCommand { get; }
    public RelayCommand RegisterTaskCommand { get; }
    public RelayCommand BrowseOpenRgbCommand { get; }
    public RelayCommand RestartServerCommand { get; }

    public MainViewModel()
    {
        Settings = SettingsService.LoadOrDefault();
        _profile = ProfileService.Load();

        OpenRgbPath = Settings.OpenRgbExePath;
        Fps = Settings.Fps;
        StartMinimized = Settings.StartMinimized;
        Autostart = Settings.AutostartWithWindows;

        _client = new OpenRgbClient("127.0.0.1", Settings.Port);
        _engine = new LightingEngine(_client, GetConfigsForEngine);

        Engine = _engine;

        _engine.Connected += () => OnUi(() =>
        {
            Connected = true;
            StatusText = "Connected to OpenRGB";
            Log.Info($"Engine connected ({_client.Devices.Count} device(s))");
            RefreshDiagnostics();
        });
        _engine.Disconnected += () => OnUi(() =>
        {
            Connected = false;
            StatusText = "Waiting for OpenRGB server…";
            Log.Info("Engine disconnected");
            RefreshDiagnostics();
        });
        _engine.DevicesChanged += () => OnUi(() =>
        {
            try
            {
                RebuildDevices();
                Log.Info($"Device list rebuilt: {Devices.Count} device(s)");
            }
            catch (Exception ex)
            {
                Log.Error("RebuildDevices failed", ex);
            }
            RefreshDiagnostics();
        });
        _client.LogMessage += msg => Log.Info("[OpenRGB] " + msg);

        // Watchdog: if the SDK server disappears (crash, manual kill), bring it back.
        _watchdog = new DispatcherTimerWrapper(
            () =>
            {
                try
                {
                    if (!Installing && !OpenRgbProcessManager.IsServerUp(Settings.Port))
                    {
                        Log.Info("Watchdog: server is down - starting it");
                        Task.Run((Action)EnsureServerRunning).ContinueWith(_ => OnUi(RefreshDiagnostics));
                    }
                }
                catch
                {
                }
            },
            5_000);

        AddStaticCommand = new RelayCommand(_ => AddLayer(EffectType.Static));
        AddRainbowCycleCommand = new RelayCommand(_ => AddLayer(EffectType.RainbowCycle));
        AddRainbowWaveCommand = new RelayCommand(_ => AddLayer(EffectType.RainbowWave));
        AddBreathingCommand = new RelayCommand(_ => AddLayer(EffectType.Breathing));
        AddBlinkCommand = new RelayCommand(_ => AddLayer(EffectType.Blink));
        RemoveLayerCommand = new RelayCommand(_ => RemoveSelectedLayer(), _ => SelectedDevice?.SelectedLayer is not null);
        MoveLayerUpCommand = new RelayCommand(_ => MoveSelectedLayer(+1), _ => CanMove(+1));
        MoveLayerDownCommand = new RelayCommand(_ => MoveSelectedLayer(-1), _ => CanMove(-1));
        ShowSettingsCommand = new RelayCommand(_ => SettingsPageVisible = true);
        BackFromSettingsCommand = new RelayCommand(_ => SettingsPageVisible = false);
        InstallOpenRgbCommand = new RelayCommand(async _ => await InstallOpenRgbAsync(), _ => !Installing);
        RegisterTaskCommand = new RelayCommand(_ => RegisterTask());
        BrowseOpenRgbCommand = new RelayCommand(_ => BrowseExe());
        RestartServerCommand = new RelayCommand(_ => RestartServer());

        _saveDebounce = new System.Timers.Timer(600) { AutoReset = false };
        _saveDebounce.Elapsed += (_, _) => OnUi(SaveProfileNow);

        _previewTimer = new DispatcherTimerWrapper(UpdatePreviews, 66);

        HookCollectionEvents(_profile.Devices);
    }

    public LightingEngine Engine { get; }

    private readonly object _profileLock = new();

    /// <summary>Snapshot provider for the engine (called on engine thread).</summary>
    private IReadOnlyList<DeviceConfig> GetConfigsForEngine()
    {
        lock (_profileLock)
        {
            return _profile.Devices.ToList();
        }
    }

    public void Initialize(bool showWindow)
    {
        RefreshResolvedPath();
        RefreshTaskStatus();
        RefreshDiagnostics();
        Task.Run((Action)EnsureServerRunning);
        _engine.Start();
        _watchdog.Start();

        if (!showWindow)
        {
            _previewTimer.Start(); // keep previews fresh for when window opens
        }
    }

    private int _serverStartInProgress;

    /// <summary>Non-blocking: brings the SDK server up if it is not already running.</summary>
    private void EnsureServerRunning()
    {
        if (Interlocked.Exchange(ref _serverStartInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            if (OpenRgbProcessManager.IsServerUp(Settings.Port))
            {
                return;
            }

            // An existing process may still be initializing - give it time instead of killing it.
            if (Process.GetProcessesByName("OpenRGB").Length > 0 &&
                OpenRgbProcessManager.WaitForServer(Settings.Port, 20_000))
            {
                return;
            }

            // Preferred: plain start. Works unelevated on most systems (NVIDIA via NVAPI,
            // ASUS boards via WMI/SMBus once the driver service exists).
            string exe = OpenRgbProcessManager.ExePath(Settings.OpenRgbExePath);
            if (!string.IsNullOrEmpty(exe))
            {
                OpenRgbProcessManager.StartServer(exe, Settings.Port);
                if (OpenRgbProcessManager.WaitForServer(Settings.Port, 20_000))
                {
                    return;
                }
            }

            // Fallback: launch the registered elevated logon task (no UAC prompt).
            if (OpenRgbProcessManager.IsTaskRegistered())
            {
                OpenRgbProcessManager.TryStartViaTask(Settings.Port);
            }
        }
        catch (Exception ex)
        {
            Log.Error("EnsureServerRunning failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _serverStartInProgress, 0);
        }
    }

    /// <summary>Full restart: kill, then bring back up. Runs off the UI thread.</summary>
    private void RestartServer()
    {
        SaveSettings();
        InstallStatus = "Restarting OpenRGB server…";

        Task.Run(() =>
        {
            OpenRgbProcessManager.KillRunningServers();
            Thread.Sleep(800);
            EnsureServerRunning();
            OnUi(() =>
            {
                RefreshDiagnostics();
                InstallStatus = ServerUp
                    ? "Server restarted."
                    : "Server did not come up - check the STATUS section below.";
            });
        });
    }

    /// <summary>Re-reads live server/connection state for the Settings page.</summary>
    public void RefreshDiagnostics()
    {
        ServerUp = OpenRgbProcessManager.IsServerUp(Settings.Port);
        DeviceCount = _client.Devices.Count;

        string hint;
        if (!ExeFound)
        {
            hint = "OpenRGB is not installed yet - use the button above.";
        }
        else if (!ServerUp)
        {
            hint = "The SDK server is not running - click 'Restart server'. If that fails, re-register the logon task.";
        }
        else if (!Connected)
        {
            hint = "Server is up but AuraFlow is not connected yet - should connect within seconds. Try 'Restart server'.";
        }
        else if (DeviceCount == 0)
        {
            hint = "Connected, but OpenRGB detected no devices. It must run WITH admin rights: register the logon task, then 'Restart server'. Also remove Armoury Crate / RGB Fusion / iCUE motherboard plugins if present.";
        }
        else
        {
            hint = $"All good - {DeviceCount} device(s) detected. Go back and pick a device to start layering effects.";
        }

        DiagnosticHint = hint;
    }

    // ------------------------------------------------------------- devices UI

    private void RebuildDevices()
    {
        var serverDevices = _client.Devices.Where(d => d.LedCount > 0).ToList();

        // add configs for unknown devices
        foreach (var dev in serverDevices)
        {
            lock (_profileLock)
            {
                if (_profile.Devices.Any(c => c.StableKey == dev.StableKey))
                {
                    continue;
                }

                var cfg = new DeviceConfig
                {
                    StableKey = dev.StableKey,
                    DisplayName = dev.Name,
                    Enabled = true,
                };
                cfg.Layers.Add(new Layer
                {
                    Name = "Base",
                    Type = EffectType.RainbowCycle,
                    Speed = 35,
                    Brightness = 100,
                });
                _profile.Devices.Add(cfg);
                HookCollectionEvents(new[] { cfg });
            }
        }

        Devices.Clear();
        foreach (var dev in serverDevices)
        {
            var cfg = _profile.Devices.First(c => c.StableKey == dev.StableKey);
            var vm = new DeviceViewModel(_engine, dev, cfg);
            vm.SelectedLayer = cfg.Layers.FirstOrDefault(l => l.Enabled);
            Devices.Add(vm);
        }

        if (SelectedDevice is null || !Devices.Contains(SelectedDevice))
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Controllable && d.Enabled)
                             ?? Devices.FirstOrDefault(d => d.Controllable)
                             ?? Devices.FirstOrDefault();
        }

        if (!SettingsPageVisible)
        {
            Raise(nameof(HasDevices));
        }

        MarkDirtyAndSave();
    }

    public bool HasDevices => Devices.Count > 0;

    private void HookCollectionEvents(IEnumerable<DeviceConfig> configs)
    {
        foreach (var cfg in configs)
        {
            cfg.Layers.CollectionChanged += Layers_CollectionChanged;
            foreach (var layer in cfg.Layers)
            {
                layer.PropertyChanged += Layer_PropertyChanged;
                layer.Colors.CollectionChanged += Colors_CollectionChanged;
            }
        }
    }

    private void Layers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is ObservableCollection<Layer> col)
        {
            if (e.OldItems is not null)
            {
                foreach (Layer l in e.OldItems)
                {
                    l.PropertyChanged -= Layer_PropertyChanged;
                    l.Colors.CollectionChanged -= Colors_CollectionChanged;
                }
            }

            if (e.NewItems is not null)
            {
                foreach (Layer l in e.NewItems)
                {
                    l.PropertyChanged += Layer_PropertyChanged;
                    l.Colors.CollectionChanged += Colors_CollectionChanged;
                }
            }
        }

        MarkDirtyAndSave();
    }

    private void Colors_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => MarkDirtyAndSave();

    private void Layer_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        _engine.MarkConfigDirty();
        DebouncedSave();
    }

    private void MarkDirtyAndSave()
    {
        _engine.MarkConfigDirty();
        DebouncedSave();
    }

    private void DebouncedSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void SaveProfileNow() => ProfileService.Save(_profile);

    private void SaveSettings()
    {
        Settings.OpenRgbExePath = OpenRgbPath;
        SettingsService.Save(Settings);
        RefreshResolvedPath();
    }

    private void RefreshResolvedPath()
    {
        string resolved = OpenRgbProcessManager.ExePath(OpenRgbPath);
        ResolvedExe = string.IsNullOrEmpty(resolved) ? "OpenRGB.exe not found" : resolved;
        ExeFound = !string.IsNullOrEmpty(resolved);
    }

    private void UpdateAutostart()
    {
        try
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true);
            if (key is null)
            {
                return;
            }

            if (Autostart)
            {
                key.SetValue("AuraFlow", $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                if (key.GetValue("AuraFlow") is not null)
                {
                    key.DeleteValue("AuraFlow");
                }
            }
        }
        catch
        {
        }
    }

    public void ApplyAutostartOnStartup()
    {
        if (Autostart)
        {
            UpdateAutostart();
        }
    }

    // ---------------------------------------------------------------- layers

    private void AddLayer(EffectType type)
    {
        var dev = SelectedDevice;
        if (dev is null)
        {
            return;
        }

        var layer = new Layer
        {
            Name = EffectTypeInfo.DisplayName(type),
            Type = type,
            Speed = 50,
            Brightness = 100,
            Colors =
            {
                type switch
                {
                    EffectType.Static or EffectType.Breathing or EffectType.Blink => new SerializableColor(0, 120, 255),
                    _ => new SerializableColor(255, 255, 255),
                },
            },
        };

        int insertAt = 0; // new layers go on top
        dev.Config.Layers.Insert(insertAt, layer);
        dev.SelectedLayer = layer;
        _engine.MarkConfigDirty();
    }

    private void RemoveSelectedLayer()
    {
        var dev = SelectedDevice;
        var layer = dev?.SelectedLayer;
        if (dev is null || layer is null)
        {
            return;
        }

        dev.Config.Layers.Remove(layer);
        dev.SelectedLayer = dev.Config.Layers.FirstOrDefault();
        _engine.MarkConfigDirty();
    }

    private bool CanMove(int dir)
    {
        var dev = SelectedDevice;
        var layer = dev?.SelectedLayer;
        if (dev is null || layer is null)
        {
            return false;
        }

        int idx = dev.Config.Layers.IndexOf(layer);
        int target = idx - dir; // up = towards index 0 = top of stack
        return target >= 0 && target < dev.Config.Layers.Count;
    }

    private void MoveSelectedLayer(int dir)
    {
        var dev = SelectedDevice;
        var layer = dev?.SelectedLayer;
        if (dev is null || layer is null || !CanMove(dir))
        {
            return;
        }

        int idx = dev.Config.Layers.IndexOf(layer);
        dev.Config.Layers.Remove(layer);
        dev.Config.Layers.Insert(idx - dir, layer);
        _engine.MarkConfigDirty();
    }

    // -------------------------------------------------------------- settings

    private async Task InstallOpenRgbAsync()
    {
        if (Installing)
        {
            return;
        }

        Installing = true;
        InstallProgress = 0;
        InstallStatus = "Downloading OpenRGB…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                InstallProgress = p;
                InstallStatus = $"Downloading OpenRGB… {p:F0}%";
            });
            await OpenRgbProcessManager.InstallAsync(progress, System.Threading.CancellationToken.None);
            InstallStatus = "Installed.";
            OpenRgbPath = Path.Combine(OpenRgbProcessManager.DefaultInstallDir, "OpenRGB.exe");
            SaveSettings();
            RestartServer();
        }
        catch (Exception ex)
        {
            InstallStatus = "Download failed: " + ex.Message;
        }
        finally
        {
            Installing = false;
        }
    }

    private void RegisterTask()
    {
        string exe = OpenRgbProcessManager.ExePath(OpenRgbPath);
        if (string.IsNullOrEmpty(exe))
        {
            InstallStatus = "Install OpenRGB first.";
            return;
        }

        bool ok = OpenRgbProcessManager.RegisterLogonTask(exe, Settings.Port);
        if (ok)
        {
            // Bring the server up right away (direct start first, task as fallback).
            InstallStatus = "Logon task registered - starting server…";
            Task.Run(() =>
            {
                EnsureServerRunning();
                OnUi(() =>
                {
                    RefreshDiagnostics();
                    InstallStatus = ServerUp
                        ? "Logon task registered and server is running."
                        : "Logon task registered, but the server did not come up.";
                });
            });
        }
        else
        {
            InstallStatus = "Registration cancelled or failed.";
        }
        RefreshTaskStatus();
    }

    private void RefreshTaskStatus()
    {
        TaskRegistered = OpenRgbProcessManager.IsTaskRegistered();
        RefreshDiagnostics();
    }

    private void BrowseExe()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "OpenRGB executable|OpenRGB.exe|Executable|*.exe",
            Title = "Locate OpenRGB.exe",
        };
        if (dlg.ShowDialog() == true)
        {
            OpenRgbPath = dlg.FileName;
            SaveSettings();
        }
    }

    // --------------------------------------------------------------- preview

    private void UpdatePreviews()
    {
        foreach (var d in Devices)
        {
            d.UpdatePreview();
        }
    }

    public void StartPreviewTimer() => _previewTimer.Start();

    private static void OnUi(Action a)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var disp = app.Dispatcher;
        if (disp.CheckAccess())
        {
            a();
        }
        else
        {
            disp.BeginInvoke(a);
        }
    }

    /// <summary>Small wrapper so the VM does not need a direct using of Windows.Threading.</summary>
    private sealed class DispatcherTimerWrapper
    {
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        public DispatcherTimerWrapper(Action tick, int intervalMs)
        {
            _timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(intervalMs),
            };
            _timer.Tick += (_, _) => tick();
        }

        public void Start() => _timer.Start();
    }
}
