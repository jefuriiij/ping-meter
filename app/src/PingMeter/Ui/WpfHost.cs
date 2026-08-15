using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace PingMeter.Ui;

/// <summary>
/// Lets WPF windows live inside this WinForms app. WinForms owns the message loop
/// (Application.Run in Program.cs), so we only need a WPF Application instance to exist
/// for resource lookup and theming — we never call its Run().
/// </summary>
internal static class WpfHost
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
            return;
        _initialized = true;

        var app = System.Windows.Application.Current ?? new System.Windows.Application
        {
            // The WinForms message loop decides when the process exits; closing the last
            // WPF window must not shut anything down.
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());

        // Fully qualified: Wpf.Ui.Controls collides with System.Windows.Controls if imported.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccent: true);
    }
}
