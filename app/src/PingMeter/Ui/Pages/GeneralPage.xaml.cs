using System.Windows;
using System.Windows.Controls;

namespace PingMeter.Ui.Pages;

// public: the XAML-generated half of the partial class is public.
public partial class GeneralPage : Page
{
    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public GeneralPage()
    {
        InitializeComponent();
        // Navigation constructs pages parameterlessly, so the view model comes from the window.
        DataContext = SettingsWindow.ActiveViewModel;
    }

    private void OnAddTarget(object sender, RoutedEventArgs e) => ViewModel?.AddTarget();

    /// <summary>
    /// Scroll the page wherever the pointer is. WPF gives the wheel to the first child
    /// that handles it (the target list), and NavigationView doesn't scroll its frame,
    /// so the page would otherwise only scroll from the scrollbar itself.
    /// </summary>
    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    // Fully qualified: WinForms and WPF both define KeyEventArgs.
    private void OnNewTargetKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
            return;
        ViewModel?.AddTarget();
        e.Handled = true;
    }

    private void OnRemoveTarget(object sender, RoutedEventArgs e) =>
        ViewModel?.RemoveTarget(TargetList.SelectedItem as string);
}
