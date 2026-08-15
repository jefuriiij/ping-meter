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
