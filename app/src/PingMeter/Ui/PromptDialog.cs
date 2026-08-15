using System.Windows;
using Wpf.Ui.Controls;
// WinForms and WPF both define most of these names; alias them once so the intent is clear.
using Grid = System.Windows.Controls.Grid;
using RowDefinition = System.Windows.Controls.RowDefinition;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using WpfButton = Wpf.Ui.Controls.Button;

namespace PingMeter.Ui;

/// <summary>Small modal text prompt — WPF has no built-in InputBox.</summary>
internal sealed class PromptDialog : FluentWindow
{
    private readonly TextBox _input = new();
    private bool _accepted;

    private PromptDialog(string title, string question, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowBackdropType = WindowBackdropType.Mica;
        ExtendsContentIntoTitleBar = true;

        _input.Text = initial;

        var ok = new WpfButton { Content = "Save", Appearance = ControlAppearance.Primary, MinWidth = 88, IsDefault = true };
        ok.Click += (_, _) => { _accepted = true; Close(); };
        var cancel = new WpfButton { Content = "Cancel", MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var prompt = new TextBlock { Text = question, Margin = new Thickness(0, 0, 0, 8) };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var body = new StackPanel { Margin = new Thickness(20, 8, 20, 20) };
        body.Children.Add(prompt);
        body.Children.Add(_input);
        body.Children.Add(buttons);

        var bar = new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(bar, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    /// <summary>Returns the entered text, or null if cancelled or left empty.</summary>
    public static string? Show(Window? owner, string title, string question, string initial)
    {
        var dialog = new PromptDialog(title, question, initial);
        if (owner != null)
            dialog.Owner = owner;
        dialog.ShowDialog();
        string text = dialog._input.Text.Trim();
        return dialog._accepted && text.Length > 0 ? text : null;
    }
}
