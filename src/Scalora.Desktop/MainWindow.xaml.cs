using System.Windows;

namespace Scalora.Desktop;
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
}
