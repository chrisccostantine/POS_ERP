using Microsoft.EntityFrameworkCore;
using Scalora.Domain;
using Scalora.Infrastructure;
using Xunit;

namespace Scalora.Tests;

public sealed class InventoryServiceTests
{
    [Fact]
    public async Task Adjustment_creates_auditable_movement_and_outbox_job()
    {
        await using var db = CreateDb();
        var branch = new Branch { Name = "Main", Code = "MAIN" };
        var product = new Product { BranchId = branch.Id, Name = "Item", Sku = "SKU", PurchasePrice = 1,
            SellingPrice = 2, StockQuantity = 10, ShopifySyncEnabled = true, ShopifyInventoryItemId = 123 };
        db.AddRange(branch, product); await db.SaveChangesAsync();

        var quantity = await new InventoryService(db).AdjustAsync(product.Id, branch.Id, -2,
            InventoryAction.Sale, "sale-1", "movement-1", null, default);

        Assert.Equal(8, quantity);
        var movement = Assert.Single(db.InventoryMovements);
        Assert.Equal((10, 8, -2), (movement.PreviousStock, movement.NewStock, movement.Quantity));
        Assert.Single(db.SyncJobs);
    }

    [Fact]
    public async Task Replayed_adjustment_is_idempotent()
    {
        await using var db = CreateDb();
        var branch = new Branch { Name = "Main", Code = "MAIN" };
        var product = new Product { BranchId = branch.Id, Name = "Item", Sku = "SKU", PurchasePrice = 1, SellingPrice = 2, StockQuantity = 10 };
        db.AddRange(branch, product); await db.SaveChangesAsync();
        var service = new InventoryService(db);

        await service.AdjustAsync(product.Id, branch.Id, -2, InventoryAction.Sale, "sale-1", "same-key", null, default);
        var quantity = await service.AdjustAsync(product.Id, branch.Id, -2, InventoryAction.Sale, "sale-1", "same-key", null, default);

        Assert.Equal(8, quantity); Assert.Single(db.InventoryMovements);
    }

    private static ScaloraDbContext CreateDb() => new(new DbContextOptionsBuilder<ScaloraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
