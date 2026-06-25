using System.Diagnostics;
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
