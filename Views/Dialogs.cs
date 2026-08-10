using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataPortStudio.Models;
using DataPortStudio.Services;

namespace DataPortStudio.Views;

/// <summary>Central helpers for styled modal popups.</summary>
public static class Dialogs
{
    private static string L(string key) => LocalizationManager.Instance[key];

    /// <summary>Success popup after an export, with Open file / Open folder shortcuts.</summary>
    public static void ExportComplete(string path, int rowCount)
    {
        var choice = ModalDialog.Choose("Export complete",
            $"Exported {rowCount:N0} row(s) to:\n{path}",
            DialogKind.Success, "Open file", "Open folder", "Close");
        try
        {
            if (choice == 1)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (choice == 2)
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open", ex.Message);
        }
    }
    public static void ShowMessage(string title, string message)
        => ModalDialog.Show(title, message, DialogKind.Info, "OK", null);

    public static void ShowSuccess(string title, string message)
        => ModalDialog.Show(title, message, DialogKind.Success, "OK", null);

    public static void ShowError(string title, string message)
        => ModalDialog.Show(title, message, DialogKind.Error, "OK", null);

    public static bool Confirm(string title, string message)
        => ModalDialog.Show(title, message, DialogKind.Question, L("Btn_Yes"), L("Btn_Cancel"));

    /// <summary>Shows a scrollable deployment summary before applying schema changes.</summary>
    public static bool ConfirmSummary(string title, string summary, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 820,
            Height = 600,
            MinWidth = 620,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            Owner = owner ?? (Application.Current?.MainWindow is { IsLoaded: true } w ? w : null),
            Background = Application.Current?.Resources["B.Surface"] as Brush ?? Brushes.White,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Application.Current?.Resources["B.Text"] as Brush ?? Brushes.Black,
            Margin = new Thickness(20, 18, 20, 12),
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var summaryBox = new TextBox
        {
            Text = summary,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(10),
            Margin = new Thickness(20, 0, 20, 12),
            Background = Application.Current?.Resources["B.SurfaceAlt"] as Brush ?? Brushes.White,
            Foreground = Application.Current?.Resources["B.Text"] as Brush ?? Brushes.Black,
            BorderBrush = Application.Current?.Resources["B.Border"] as Brush ?? Brushes.Gray,
        };
        Grid.SetRow(summaryBox, 1);
        root.Children.Add(summaryBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 18),
        };
        var cancel = new Button
        {
            Content = L("Btn_Cancel"),
            MinWidth = 100,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true,
        };
        var apply = new Button
        {
            Content = "Apply",
            MinWidth = 100,
            Padding = new Thickness(12, 6, 12, 6),
            IsDefault = true,
            Style = Application.Current?.Resources["AccentButton"] as Style,
        };
        cancel.Click += (_, _) => { dialog.DialogResult = false; };
        apply.Click += (_, _) => { dialog.DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;
        dialog.Loaded += (_, _) => summaryBox.Focus();
        return dialog.ShowDialog() == true;
    }

    /// <summary>A red, destructive confirmation (e.g. dropping a table).</summary>
    public static bool ConfirmDanger(string title, string message, string? confirmText = null)
        => ModalDialog.Show(title, message, DialogKind.Error, confirmText ?? L("Btn_Delete"), L("Btn_Cancel"));

    public enum CopyMode { Cancel, StructureOnly, StructureAndData }

    /// <summary>Asks whether to copy a table's structure only or structure + data.</summary>
    public static CopyMode ChooseCopyMode(string sourceName, string newName)
        => ModalDialog.Choose("Copy table",
               $"Copy “{sourceName}” to a new table “{newName}”.\n\nInclude the data, or copy the structure only?",
               DialogKind.Question, "Structure + data", "Structure only", "Cancel") switch
        {
            1 => CopyMode.StructureAndData,
            2 => CopyMode.StructureOnly,
            _ => CopyMode.Cancel
        };

    /// <summary>Opens the connection editor. Returns true if the user saved.</summary>
    public static bool EditConnection(ConnectionProfile profile)
        => new ConnectionDialog(profile).ShowDialog() == true;
}
