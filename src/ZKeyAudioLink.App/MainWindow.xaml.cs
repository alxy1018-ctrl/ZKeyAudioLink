using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace ZKeyAudioLink.App;

public partial class MainWindow : Window
{
    private readonly ViewModels.MainViewModel _viewModel;

    public MainWindow(ViewModels.MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public ViewModels.MainViewModel ViewModel => _viewModel;

    private void StartTimeBox_LostFocus(object sender, RoutedEventArgs e) => _viewModel.CommitStartTime();

    private void StartTimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.CommitStartTime();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = this };
        help.ShowDialog();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
            (Application.Current as App)?.HandleWindowHidden(this);
            return;
        }

        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
