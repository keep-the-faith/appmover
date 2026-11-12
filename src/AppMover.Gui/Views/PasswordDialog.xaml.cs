using System.Windows;

namespace AppMover.Gui.Views;

/// <summary>Asks for the password of an encrypted package.</summary>
public partial class PasswordDialog
{
    public PasswordDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        Loaded += (_, _) => PasswordBox.Focus();
    }

    public string Password => PasswordBox.Password;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
