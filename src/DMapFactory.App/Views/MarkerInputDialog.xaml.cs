using System.Windows;
using System.Windows.Input;
using DMapFactory.Localization;

namespace DMapFactory.App.Views;

public partial class MarkerInputDialog : Window
{
    public string MarkerText { get; private set; } = string.Empty;

    public MarkerInputDialog(string defaultText, bool isEditing = false)
    {
        InitializeComponent();
        if (isEditing)
        {
            Title = Loc.Instance["MarkerDlg_EditTitle"];
            DialogHeaderText.Text = Loc.Instance["MarkerDlg_EditHeader"];
            ConfirmButton.Content = Loc.Instance["MarkerDlg_BtnSave"];
        }
        MarkerTextBox.Text = defaultText;
        Loaded += (_, _) =>
        {
            MarkerTextBox.Focus();
            MarkerTextBox.SelectAll();
        };
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Confirm();
    }

    private void MarkerTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        string text = MarkerTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MarkerTextBox.Focus();
            return;
        }

        MarkerText = text;
        DialogResult = true;
    }
}
