namespace Scalora.Domain;

public abstract class Entity { public Guid Id { get; init; } = Guid.NewGuid(); }
public enum InventoryAction { Sale, Return, Exchange, Purchase, Adjustment, ShopifyOrder, ShopifyAdjustment }
public enum SyncJobStatus { Pending, Processing, Synced, Failed }
public enum SaleStatus { Completed, Refunded, Voided }

public sealed class Branch : Entity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
}

public sealed class Category : Entity { public required string Name { get; set; } public bool IsArchived { get; set; } }
public sealed class Supplier : Entity
{
    public required string Name { get; set; }
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
}

public sealed class Product : Entity
{
    public Guid BranchId { get; set; }
    public required string Name { get; set; }
    public required string Sku { get; set; }
    public string? Barcode { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? SupplierId { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal SellingPrice { get; set; }
    public int StockQuantity { get; set; }
    public int ReorderLevel { get; set; }
    public string? ImageUrl { get; set; }
    public bool IsArchived { get; set; }
    public long? ShopifyProductId { get; set; }
    public long? ShopifyVariantId { get; set; }
    public long? ShopifyInventoryItemId { get; set; }
    public long? ShopifyLocationId { get; set; }
    public bool ShopifySyncEnabled { get; set; }
    public DateTimeOffset? LastShopifySyncAt { get; set; }
    public uint Version { get; set; }
}

public sealed class Customer : Entity
{
    public required string Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
}

public sealed class Sale : Entity
{
    public required string ReceiptNumber { get; set; }
    public Guid BranchId { get; set; }
    public Guid CashierId { get; set; }
    public Guid? CustomerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public required string PaymentMethod { get; set; }
    public SaleStatus Status { get; set; } = SaleStatus.Completed;
    public List<SaleItem> Items { get; set; } = [];
}

public sealed class SaleItem : Entity
{
    public Guid SaleId { get; set; }
    public Guid ProductId { get; set; }
    public required string ProductName { get; set; }
    public required string Sku { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
}

public sealed class InventoryMovement : Entity
{
    public Guid ProductId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? UserId { get; set; }
    public InventoryAction Action { get; set; }
    public int Quantity { get; set; }
    public int PreviousStock { get; set; }
    public int NewStock { get; set; }
    public required string Source { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Expense : Entity
{
    public Guid BranchId { get; set; }
    public required string Category { get; set; }
    public required string Description { get; set; }
    public decimal Amount { get; set; }
    public DateOnly Date { get; set; }
}

public sealed class CashSession : Entity
{
    public Guid BranchId { get; set; }
    public Guid CashierId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal OpeningCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? ActualCash { get; set; }
}

public sealed class ShopifyConnection : Entity
{
    public Guid BranchId { get; set; }
    public required string StoreDomain { get; set; }
    public required string EncryptedAccessToken { get; set; }
    public required string EncryptedWebhookSecret { get; set; }
    public required string LocationId { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
}

public sealed class ShopifyWebhookLog : Entity
{
    public required string WebhookId { get; set; }
    public required string Topic { get; set; }
    public required string ShopDomain { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class SyncJob : Entity
{
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public required string IdempotencyKey { get; set; }
    public SyncJobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class AppSetting : Entity { public required string Key { get; set; } public required string Value { get; set; } }
