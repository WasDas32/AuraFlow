using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AuraFlow.App.Controls;
using AuraFlow.App.Models;
using AuraFlow.App.ViewModels;

namespace AuraFlow.App;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    private int _activeColorIndex = -1;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => { };
    }

    // ------------------------------------------------------------- chrome

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            HideToTray();
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    internal void RealClose()
    {
        _allowClose = true;
        Close();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray() => Hide();

    // ------------------------------------------------------------- layers

    private void AddLayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn)
        {
            return;
        }

        var menu = new ContextMenu
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = btn,
        };

        foreach (var opt in EffectOption.All)
        {
            var item = new MenuItem { Header = opt.Label, Tag = opt.Value };
            item.Click += AddMenuItem_Click;
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void AddMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is EffectType type)
        {
            switch (type)
            {
                case EffectType.Static: Vm.AddStaticCommand.Execute(null); break;
                case EffectType.RainbowCycle: Vm.AddRainbowCycleCommand.Execute(null); break;
                case EffectType.RainbowWave: Vm.AddRainbowWaveCommand.Execute(null); break;
                case EffectType.Breathing: Vm.AddBreathingCommand.Execute(null); break;
                case EffectType.Blink: Vm.AddBlinkCommand.Execute(null); break;
            }
        }
    }

    private Layer? LayerFromSender(object sender) => (sender as FrameworkElement)?.DataContext as Layer;

    private void MoveLayerUp_Click(object sender, RoutedEventArgs e)
    {
        SelectLayer(LayerFromSender(sender));
        Vm.MoveLayerUpCommand.Execute(null);
    }

    private void MoveLayerDown_Click(object sender, RoutedEventArgs e)
    {
        SelectLayer(LayerFromSender(sender));
        Vm.MoveLayerDownCommand.Execute(null);
    }

    private void DeleteLayer_Click(object sender, RoutedEventArgs e)
    {
        SelectLayer(LayerFromSender(sender));
        Vm.RemoveLayerCommand.Execute(null);
    }

    private void SelectLayer(Layer? layer)
    {
        if (layer is not null && Vm.SelectedDevice is not null)
        {
            Vm.SelectedDevice.SelectedLayer = layer;
        }
    }

    // ------------------------------------------------------------- colors

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedDevice?.SelectedLayer is null || sender is not Button btn)
        {
            return;
        }

        int index = IndexOfContainer(SwatchesList, btn);
        if (index < 0)
        {
            return;
        }

        _activeColorIndex = index;
        LayerColorPicker.SetColor(Vm.SelectedDevice.SelectedLayer.Colors[index]);
        ColorEditorPanel.Visibility = Visibility.Visible;
    }

    private static int IndexOfContainer(ItemsControl list, object element)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject d
                && IsDescendantOrSelf(d, element as DependencyObject))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsDescendantOrSelf(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private void Picker_ColorChanged(object sender, RoutedEventArgs e)
    {
        var layer = Vm.SelectedDevice?.SelectedLayer;
        if (layer is null || _activeColorIndex < 0 || _activeColorIndex >= layer.Colors.Count)
        {
            return;
        }

        if (sender is ColorPicker picker)
        {
            layer.Colors[_activeColorIndex] = picker.CurrentColor;
        }
    }

    private void AddColor_Click(object sender, RoutedEventArgs e)
    {
        var layer = Vm.SelectedDevice?.SelectedLayer;
        if (layer is null || layer.Colors.Count >= 8)
        {
            return;
        }

        var last = layer.Colors.Count > 0 ? layer.Colors[^1] : new SerializableColor(255, 255, 255);
        layer.Colors.Add(last);
    }

    private void RemoveColor_Click(object sender, RoutedEventArgs e)
    {
        var layer = Vm.SelectedDevice?.SelectedLayer;
        if (layer is null || layer.Colors.Count <= 1)
        {
            return;
        }

        if (_activeColorIndex >= 0 && _activeColorIndex < layer.Colors.Count)
        {
            layer.Colors.RemoveAt(_activeColorIndex);
            _activeColorIndex = -1;
            ColorEditorPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            layer.Colors.RemoveAt(layer.Colors.Count - 1);
        }
    }

    private void CloseColorEditor_Click(object sender, RoutedEventArgs e)
    {
        ColorEditorPanel.Visibility = Visibility.Collapsed;
        _activeColorIndex = -1;
    }

    // ----------------------------------------------------------- settings

    private void BrowseExe_Click(object sender, RoutedEventArgs e) => Vm.BrowseOpenRgbCommand.Execute(null);

    private void RegisterTask_Click(object sender, RoutedEventArgs e) => Vm.RegisterTaskCommand.Execute(null);
}
