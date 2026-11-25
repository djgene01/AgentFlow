using System.ComponentModel;
using System.Windows;
using AgentFlow.App.ViewModels;

namespace AgentFlow.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += async (_, _) => await _vm.InitializeAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.AnyRunning)
        {
            var answer = MessageBox.Show(this, "A turn is still running. Stop it and exit?", "Exit AgentFlow",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
            _vm.StopAll();
        }
        base.OnClosing(e);
    }
}
