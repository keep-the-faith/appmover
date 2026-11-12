using System.Windows;
using System.Windows.Controls;
using AppMover.Gui.ViewModels;

namespace AppMover.Gui.Views;

public partial class ExportView
{
    public ExportView() => InitializeComponent();

    // PasswordBox.Password is not bindable (on purpose): it is pushed to the view model here.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExportViewModel viewModel)
            viewModel.Password = ((PasswordBox)sender).Password;
    }

    private void OnPasswordConfirmationChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExportViewModel viewModel)
            viewModel.PasswordConfirmation = ((PasswordBox)sender).Password;
    }
}
