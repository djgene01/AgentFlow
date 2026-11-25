using System.Windows;
using Microsoft.Win32;

namespace AgentFlow.App.Services;

public interface IDialogService
{
    string? PickFolder();
    string? OpenFile(string title, string filter);
    string? SaveFile(string title, string defaultName, string filter);
    bool Confirm(string title, string message);
    void Inform(string title, string message);
}

public sealed class DialogService : IDialogService
{
    public string? PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a project folder", Multiselect = false };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FolderName : null;
    }

    public string? OpenFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public string? SaveFile(string title, string defaultName, string filter)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void Inform(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
