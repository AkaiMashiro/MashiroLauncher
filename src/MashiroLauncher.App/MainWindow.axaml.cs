using Avalonia.Controls;
using MashiroLauncher.App.ViewModels;

namespace MashiroLauncher.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
