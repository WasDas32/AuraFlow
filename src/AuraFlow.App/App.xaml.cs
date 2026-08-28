using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using AuraFlow.App.Services;
using AuraFlow.App.ViewModels;

namespace AuraFlow.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    private MainWindow? _mainWindow;
    private MainViewModel? _vm;
    private TaskbarIcon? _tray;

    public new static App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "AuraFlow_SingleInstance_9F3A2C", out bool createdNew);
        if (!createdNew)
        {
            Log.Info("Another instance already running - exiting.");
            Shutdown();
            return;
        }

        Log.Info("---- AuraFlow starting ----");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(
                "AuraFlow hit an error and will try to continue.\n\n" + args.Exception.Message,
                "AuraFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Fatal unhandled exception", args.ExceptionObject as Exception);

        try
        {
            base.OnStartup(e);

            bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
            Log.Info($"Args: {(e.Args.Length == 0 ? "(none)" : string.Join(" ", e.Args))}");

            _vm = new MainViewModel();
            Log.Info("MainViewModel created");

            _mainWindow = new MainWindow { DataContext = _vm };
            MainWindow = _mainWindow;
            Log.Info("MainWindow created");

            SetupTray();
            Log.Info("Tray icon created");

            _vm.Initialize(showWindow: !minimized);
            _vm.ApplyAutostartOnStartup();
            _vm.StartPreviewTimer();

            if (!minimized)
            {
                _mainWindow.Show();
                Log.Info("Window shown");
            }
            else
            {
                Log.Info("Starting minimized to tray");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fatal startup failure", ex);
            MessageBox.Show(
                "AuraFlow failed to start:\n\n" + ex.Message + "\n\nDetails: " + ex.StackTrace,
                "AuraFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void SetupTray()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "AuraFlow - RGB Control",
            IconSource = MakeTrayIconBitmap(),
        };

        var menu = new ContextMenu();

        var show = new MenuItem { Header = "Show AuraFlow" };
        show.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(show);

        var blackout = new MenuItem { Header = "Lights off", IsCheckable = true };
        blackout.SetBinding(MenuItem.IsCheckedProperty, new System.Windows.Data.Binding("Blackout") { Source = _vm });
        menu.Items.Add(blackout);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        _tray.ContextMenu = menu;
        _tray.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>Renders the logo to a bitmap - TaskbarIcon needs a BitmapSource, not a DrawingImage.</summary>
    private static BitmapSource MakeTrayIconBitmap()
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new RectangleGeometry(new Rect(0, 0, size, size), 7, 7);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1F)), null, bg);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D)), null,
                new Point(11, 12), 6.5, 6.5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)), null,
                new Point(21, 12), 6.5, 6.5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x5A, 0x8C, 0xFF)), null,
                new Point(16, 21), 6.5, 6.5);
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private void ShowMainWindow() => _mainWindow?.ShowFromTray();

    public void ExitApplication()
    {
        Log.Info("Exit requested");
        try
        {
            _tray?.Dispose();
        }
        catch
        {
        }

        try
        {
            _vm?.Engine.Dispose();
        }
        catch
        {
        }

        _mainWindow?.RealClose();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ExitApplication();
        base.OnSessionEnding(e);
    }
}
