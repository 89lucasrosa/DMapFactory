using System.Windows;
using DMapFactory.App.ViewModels;

namespace DMapFactory.App.Views;

public partial class ConnectionDialog : Window
{
    private readonly ConnectionDialogViewModel _viewModel;

    public ConnectionDialog(ConnectionDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            UserPasswordBox.Password = _viewModel.Password;
        };
    }

    private void UserPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Password = UserPasswordBox.Password;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
