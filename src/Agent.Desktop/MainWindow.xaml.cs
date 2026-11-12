using System.ComponentModel;
using System.Windows;
using Agent.Desktop.ViewModels;

namespace Agent.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await viewModel.StartAsync();

    private void OnClosing(object? sender, CancelEventArgs e) => viewModel.Dispose();

    private void OnClosed(object? sender, EventArgs e) => System.Windows.Application.Current.Shutdown();
}
