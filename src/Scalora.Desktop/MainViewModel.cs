using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Scalora.Desktop;

public sealed record ProductRow(Guid Id, string Name, string Sku, decimal Price);
public sealed record SaleRow(string Receipt, string Cashier, decimal Total, string Time);

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private string pageTitle = "Dashboard";
    [ObservableProperty] private string userName = "Administrator";
    [ObservableProperty] private string connectionStatus = "Local database ready";
    [ObservableProperty] private decimal revenue = 0;
    [ObservableProperty] private int orders;
    [ObservableProperty] private decimal inventoryValue;
    [ObservableProperty] private int lowStock;
    [ObservableProperty] private int pendingJobs;
    [ObservableProperty] private int failedSyncs;
    [ObservableProperty] private DateTime? lastSync;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private ProductRow? selectedProduct;
    [ObservableProperty] private Visibility dashboardVisibility = Visibility.Visible;
    [ObservableProperty] private Visibility posVisibility = Visibility.Collapsed;
    public ObservableCollection<SaleRow> RecentSales { get; } = [];
    public ObservableCollection<ProductRow> Products { get; } = [];
    public ObservableCollection<ProductRow> Cart { get; } = [];
    public decimal CartTotal => Cart.Sum(x => x.Price);

    [RelayCommand] private void ShowDashboard() { PageTitle = "Dashboard"; DashboardVisibility = Visibility.Visible; PosVisibility = Visibility.Collapsed; }
    [RelayCommand] private void ShowPos() { PageTitle = "Point of Sale"; DashboardVisibility = Visibility.Collapsed; PosVisibility = Visibility.Visible; }
    [RelayCommand] private void Refresh() { ConnectionStatus = $"Updated {DateTime.Now:t}"; }
    [RelayCommand] private void AddProduct() { if (SelectedProduct is null) return; Cart.Add(SelectedProduct); OnPropertyChanged(nameof(CartTotal)); }
    [RelayCommand] private void Checkout() { if (Cart.Count == 0) return; Cart.Clear(); OnPropertyChanged(nameof(CartTotal)); }
}
