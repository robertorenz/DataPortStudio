using System.Windows;
using DataPortStudio.Models;
using DataPortStudio.ViewModels;

namespace DataPortStudio.Views;

public partial class UserManagerWindow : Window
{
    private readonly UserManagerViewModel _vm;

    public UserManagerWindow(ConnectionProfile connection)
    {
        InitializeComponent();
        _vm = new UserManagerViewModel(connection);
        DataContext = _vm;
        Owner = Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
    }

    private async void CreateConfirm_Click(object sender, RoutedEventArgs e)
    {
        var pwd = CreatePassBox.Password;
        CreatePassBox.Clear();
        await _vm.ConfirmCreateAsync(string.IsNullOrEmpty(pwd) ? null : pwd);
    }

    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        var pwd = SetPassBox.Password;
        SetPassBox.Clear();
        await _vm.SetPasswordAsync(pwd);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
