using Scalora.Domain;

namespace Scalora.Application;

public interface IInventoryService
{
    Task<int> AdjustAsync(Guid productId, Guid branchId, int delta, InventoryAction action,
        string source, string idempotencyKey, Guid? userId, CancellationToken cancellationToken);
}

public interface IShopifyClient
{
    Task<bool> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken);
    Task SetInventoryAsync(Guid connectionId, long inventoryItemId, long locationId, int quantity,
        string idempotencyKey, CancellationToken cancellationToken);
}

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string protectedValue);
}

public sealed record CreateSaleItem(Guid ProductId, int Quantity, decimal? UnitPrice = null);
public sealed record CreateSale(Guid BranchId, Guid CashierId, Guid? CustomerId, string PaymentMethod,
    decimal Discount, decimal TaxRate, IReadOnlyList<CreateSaleItem> Items, string IdempotencyKey);
public sealed record SaleResult(Guid Id, string ReceiptNumber, decimal Total);

public interface ISaleService
{
    Task<SaleResult> CheckoutAsync(CreateSale request, CancellationToken cancellationToken);
}
