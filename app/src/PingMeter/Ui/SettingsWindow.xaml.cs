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
    /// <summary>
    /// Pages are created by the navigation service via a parameterless constructor, so
    /// they pick the view model up from here rather than through DI.
    /// </summary>
    internal static SettingsViewModel? ActiveViewModel { get; private set; }

    private readonly SettingsViewModel _viewModel;

    /// <summary>Raised on Save with the edited config — same contract as the classic dialog.</summary>
    public event Action<AppConfig>? ConfigSaved;

    public SettingsWindow(AppConfig current)
    {
        _viewModel = new SettingsViewModel(current);
        ActiveViewModel = _viewModel;

        InitializeComponent();
        DataContext = _viewModel;

        Loaded += (_, _) => Navigation.Navigate(typeof(GeneralPage));
    }

    private void OnSave(object sender, System.Windows.RoutedEventArgs e)
    {
        ConfigSaved?.Invoke(_viewModel.BuildConfig());
        Close();
    }

    private void OnCancel(object sender, System.Windows.RoutedEventArgs e) => Close();
}
