using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Scalora.Desktop;

public sealed record ProductRow(Guid Id, Guid BranchId, string Name, string Sku, decimal Price, int StockQuantity);
public sealed record SaleRow(string Receipt, string Cashier, decimal Total, string Time);

public partial class CartLine(ProductRow product) : ObservableObject
{
    public ProductRow Product { get; } = product;
    [ObservableProperty] private int quantity = 1;
    public decimal LineTotal => Product.Price * Quantity;
    partial void OnQuantityChanged(int value) { if (value < 1) Quantity = 1; OnPropertyChanged(nameof(LineTotal)); }
}

public partial class MainViewModel(ApiClient api, UserSession session) : ObservableObject
{
    private CancellationTokenSource? _searchCancellation;
    [ObservableProperty] private string pageTitle = "Dashboard";
    [ObservableProperty] private string userName = session.DisplayName;
    [ObservableProperty] private string connectionStatus = "Connecting...";
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private decimal revenue;
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
    public ObservableCollection<CartLine> Cart { get; } = [];
    public decimal CartTotal => Cart.Sum(x => x.LineTotal);
    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(ErrorVisibility));

    public async Task InitializeAsync() { await RefreshAsync(); await LoadProductsAsync(""); }

    [RelayCommand] private async Task ShowDashboardAsync()
    {
        PageTitle = "Dashboard"; DashboardVisibility = Visibility.Visible; PosVisibility = Visibility.Collapsed;
        await RefreshAsync();
    }

    [RelayCommand] private async Task ShowPosAsync()
    {
        PageTitle = "Point of Sale"; DashboardVisibility = Visibility.Collapsed; PosVisibility = Visibility.Visible;
        if (Products.Count == 0) await LoadProductsAsync(SearchText);
    }

    [RelayCommand] private async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            var dashboard = await api.GetDashboardAsync();
            Revenue = dashboard.Revenue; Orders = dashboard.Orders; InventoryValue = dashboard.InventoryValue;
            LowStock = dashboard.LowStock; PendingJobs = dashboard.PendingJobs; FailedSyncs = dashboard.FailedSyncs;
            LastSync = dashboard.LastSync; ConnectionStatus = $"Cloud online - {DateTime.Now:t}";
        });
    }

    [RelayCommand] private void AddProduct()
    {
        if (SelectedProduct is null || SelectedProduct.StockQuantity <= 0) return;
        var existing = Cart.FirstOrDefault(x => x.Product.Id == SelectedProduct.Id);
        if (existing is null) { existing = new CartLine(SelectedProduct); existing.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CartTotal)); Cart.Add(existing); }
        else if (existing.Quantity < SelectedProduct.StockQuantity) existing.Quantity++;
        OnPropertyChanged(nameof(CartTotal));
    }

    [RelayCommand] private void RemoveLine(CartLine? line) { if (line is null) return; Cart.Remove(line); OnPropertyChanged(nameof(CartTotal)); }

    [RelayCommand] private async Task CheckoutAsync()
    {
        if (Cart.Count == 0) return;
        await RunAsync(async () =>
        {
            var result = await api.CheckoutAsync(session, Cart.ToArray());
            MessageBox.Show($"Payment accepted. Receipt {result.ReceiptNumber}\nTotal: {result.Total:C}", "Sale complete", MessageBoxButton.OK, MessageBoxImage.Information);
            Cart.Clear(); OnPropertyChanged(nameof(CartTotal)); await LoadProductsAsync(SearchText);
        });
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCancellation?.Cancel(); _searchCancellation = new CancellationTokenSource();
        _ = SearchAfterDelayAsync(value, _searchCancellation.Token);
    }

    private async Task SearchAfterDelayAsync(string value, CancellationToken ct)
    {
        try { await Task.Delay(300, ct); await LoadProductsAsync(value, ct); }
        catch (OperationCanceledException) { }
    }

    private async Task LoadProductsAsync(string search, CancellationToken ct = default)
    {
        try
        {
            var products = await api.GetProductsAsync(search, ct);
            Products.Clear();
            foreach (var item in products) Products.Add(new ProductRow(item.Id, item.BranchId, item.Name, item.Sku, item.SellingPrice, item.StockQuantity));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorMessage = ex.Message; ConnectionStatus = "Cloud unavailable"; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true; ErrorMessage = null;
        try { await action(); }
        catch (Exception ex) { ErrorMessage = ex.Message; ConnectionStatus = "Cloud unavailable"; }
        finally { IsBusy = false; }
    }
}
