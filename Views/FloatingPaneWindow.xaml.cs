using System.Windows;

namespace DataPortStudio.Views;

public partial class FloatingPaneWindow : Window
{
    public FloatingPaneWindow()
    {
        InitializeComponent();
    }

    public void SetBody(FrameworkElement content) => Body.Content = content;
}
