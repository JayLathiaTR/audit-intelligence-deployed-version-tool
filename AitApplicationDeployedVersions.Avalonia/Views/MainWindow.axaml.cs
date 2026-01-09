using Avalonia.Controls;
using Avalonia.Input;
using AitApplicationDeployedVersions.Avalonia.ViewModels;

namespace AitApplicationDeployedVersions.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Closing += OnClosing;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.CancelFetch();

            Close();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.CancelFetch();
    }
}