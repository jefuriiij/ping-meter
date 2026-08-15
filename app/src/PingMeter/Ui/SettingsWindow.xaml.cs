using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PingMeter.Config;
using PingMeter.Ui.Pages;
using Wpf.Ui.Controls;

namespace PingMeter.Ui;

/// <summary>
/// Fluent settings window (prototype). Only the General page is implemented; the other
/// sections still live in the classic dialog until the port completes.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>Raised on Save with the edited config — same contract as the classic dialog.</summary>
    public event Action<AppConfig>? ConfigSaved;

    /// <summary>Raised before/after a network repair, so pinging pauses and the log records it.</summary>
    public event Action? RepairStarted;

    public event Action<string, bool>? RepairCompleted;

    /// <summary>Saved DNS combinations changed — persist immediately, before any Save/Cancel.</summary>
    public event Action<List<DnsPreset>>? DnsPresetsChanged;

    public SettingsWindow(AppConfig current)
    {
        _viewModel = new SettingsViewModel(current);
        _viewModel.DnsPresetsChanged += presets => DnsPresetsChanged?.Invoke(presets);
        InitializeComponent();
        DataContext = _viewModel;
        PageHost.Content = new GeneralPage { DataContext = _viewModel };
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        // The initially-selected item raises this while the XAML is still loading,
        // before PageHost exists; the constructor sets the first page itself.
        if (PageHost is null || NavList.SelectedItem is not ListBoxItem item)
            return;
        PageHost.Content = (item.Tag as string) switch
        {
            "general" => new GeneralPage { DataContext = _viewModel },
            "advanced" => new AdvancedPage { DataContext = _viewModel },
            "network" => BuildNetworkPage(),
            _ => new PlaceholderPage(),
        };
    }

    /// <summary>Open on a specific sidebar entry (0 General, 1 Advanced, 2 Network tools).</summary>
    public void SelectPage(int index)
    {
        if (index >= 0 && index < NavList.Items.Count)
            NavList.SelectedIndex = index;
    }

    private NetworkToolsPage BuildNetworkPage()
    {
        var page = new NetworkToolsPage { DataContext = _viewModel };
        page.RepairStarted += () => RepairStarted?.Invoke();
        page.RepairCompleted += (summary, full) => RepairCompleted?.Invoke(summary, full);
        return page;
    }

    /// <summary>
    /// Scroll the current page from anywhere in the window. Handled at the window (the
    /// root of the tunnelling route) because child controls otherwise swallow the wheel.
    /// </summary>
    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (FindScrollViewer(PageHost) is not { } scroller || scroller.ScrollableHeight <= 0)
            return;
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer found)
            return found;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } scroller)
                return scroller;
        }
        return null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ConfigSaved?.Invoke(_viewModel.BuildConfig());
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
