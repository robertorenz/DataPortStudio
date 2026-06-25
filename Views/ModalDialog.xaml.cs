using System.Windows;
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
}
