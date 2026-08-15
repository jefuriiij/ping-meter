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

    public SettingsWindow(AppConfig current)
    {
        _viewModel = new SettingsViewModel(current);
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
            _ => new PlaceholderPage(),
        };
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
