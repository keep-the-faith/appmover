using System.Windows;
using Microsoft.Win32;

namespace AppMover.Gui.Services;

/// <summary>Dialogs used by the view models (kept out of them so they stay free of window code).</summary>
public interface IDialogService
{
    string? SaveFile(string title, string filter, string fileName);
    string? OpenFile(string title, string filter);
    string? PickFolder(string title, string? initialFolder);
    /// <summary>Asks for a password; null when cancelled.</summary>
    string? AskPassword(string title, string message);
    bool Confirm(string title, string message);
    void Info(string title, string message);
    void Error(string title, string message);
}

public sealed class WpfDialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } window ? window : null;

    public string? SaveFile(string title, string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initialFolder ?? "" };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? AskPassword(string title, string message)
    {
        var dialog = new Views.PasswordDialog(title, message) { Owner = Owner };
        return dialog.ShowDialog() == true && dialog.Password.Length > 0 ? dialog.Password : null;
    }

    public bool Confirm(string title, string message) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string title, string message) => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string title, string message) => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner ? MessageBox.Show(owner, message, title, buttons, image) : MessageBox.Show(message, title, buttons, image);
}
