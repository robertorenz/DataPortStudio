using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DataPortStudio.Views;

public enum DialogKind { Info, Success, Error, Question }

public partial class ModalDialog : Window
{
    private int _choice;

    public ModalDialog()
    {
        InitializeComponent();
    }

    /// <summary>Three-way choice. Returns 1 (primary), 2 (middle), or 0 (cancel/closed).</summary>
    public static int Choose(string title, string message, DialogKind kind,
        string primaryText, string middleText, string cancelText)
    {
        var dlg = new ModalDialog
        {
            Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null
        };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.PrimaryButton.Content = primaryText;
        dlg.MiddleButton.Content = middleText;
        dlg.MiddleButton.Visibility = Visibility.Visible;
        dlg.SecondaryButton.Content = cancelText;
        dlg.SecondaryButton.Visibility = Visibility.Visible;
        dlg.ApplyKind(kind);
        dlg.ShowDialog();
        return dlg._choice;
    }

    private void ApplyKind(DialogKind kind)
    {
        var (color, glyph) = kind switch
        {
            DialogKind.Success => ("#FF2E9E5B", "✓"),
            DialogKind.Error => ("#FFD64545", "!"),
            DialogKind.Question => ("#FF2D7FE0", "?"),
            _ => ("#FF2D7FE0", "i"),
        };
        IconBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        IconGlyph.Text = glyph;
    }

    public static bool Show(string title, string message, DialogKind kind,
        string primaryText, string? secondaryText)
    {
        var dlg = new ModalDialog
        {
            Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null
        };
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.PrimaryButton.Content = primaryText;

        if (secondaryText is not null)
        {
            dlg.SecondaryButton.Content = secondaryText;
            dlg.SecondaryButton.Visibility = Visibility.Visible;
        }

        var (color, glyph) = kind switch
        {
            DialogKind.Success => ("#FF2E9E5B", "✓"),
            DialogKind.Error => ("#FFD64545", "!"),
            DialogKind.Question => ("#FF2D7FE0", "?"),
            _ => ("#FF2D7FE0", "i"),
        };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        dlg.IconBadge.Background = brush;
        dlg.IconGlyph.Text = glyph;

        // Destructive confirmations get a red primary button.
        if (kind == DialogKind.Error && secondaryText is not null
            && dlg.TryFindResource("DangerButton") is Style danger)
            dlg.PrimaryButton.Style = danger;

        return dlg.ShowDialog() == true;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _choice = 1;
        DialogResult = true;
        Close();
    }

    private void Middle_Click(object sender, RoutedEventArgs e)
    {
        _choice = 2;
        DialogResult = true;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _choice = 0;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Shows a text-input prompt. Returns the entered string, or null if cancelled.
    /// </summary>
    public static string? PromptText(string title, string message, string defaultValue = "")
    {
        string? result = null;
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        if (Application.Current?.Resources["B.Bg"] is Brush bg) dlg.Background = bg;

        var msgBlock = new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 8)
        };
        if (Application.Current?.Resources["B.Text"] is Brush fg) msgBlock.Foreground = fg;

        var textBox = new TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(20, 0, 20, 0),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 13
        };
        dlg.Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };

        var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 6, 0, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Padding = new Thickness(0, 6, 0, 6) };
        ok.Click += (_, _) => { result = textBox.Text; dlg.DialogResult = true; };
        cancel.Click += (_, _) => { dlg.DialogResult = false; };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 14, 20, 18)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(msgBlock);
        root.Children.Add(textBox);
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.ShowDialog();
        return result;
    }

    /// <summary>
    /// Shows a simple dropdown picker. Returns the chosen string, or null if cancelled.
    /// </summary>
    public static string? PickItem(string title, string message, IReadOnlyList<string> items)
    {
        string? result = null;
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        if (Application.Current?.Resources["B.Bg"] is Brush bg) dlg.Background = bg;

        var msgBlock = new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 8)
        };
        if (Application.Current?.Resources["B.Text"] is Brush fg) msgBlock.Foreground = fg;

        var combo = new ComboBox
        {
            Margin = new Thickness(20, 0, 20, 0),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 13
        };
        foreach (var item in items) combo.Items.Add(item);
        if (items.Count > 0) combo.SelectedIndex = 0;

        var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 6, 0, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Padding = new Thickness(0, 6, 0, 6) };
        ok.Click += (_, _) => { result = combo.SelectedItem?.ToString(); dlg.DialogResult = true; };
        cancel.Click += (_, _) => { dlg.DialogResult = false; };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 14, 20, 18)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(msgBlock);
        root.Children.Add(combo);
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.ShowDialog();
        return result;
    }
}
