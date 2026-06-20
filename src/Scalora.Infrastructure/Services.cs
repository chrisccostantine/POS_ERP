using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalora.Application;
using Scalora.Domain;

namespace Scalora.Infrastructure;

public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Scalora.Shopify.v1");
    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

public sealed class InventoryService(ScaloraDbContext db) : IInventoryService
{
    public async Task<int> AdjustAsync(Guid productId, Guid branchId, int delta, InventoryAction action,
        string source, string idempotencyKey, Guid? userId, CancellationToken cancellationToken)
    {
        var prior = await db.InventoryMovements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (prior is not null) return prior.NewStock;

        var product = await db.Products.SingleAsync(x => x.Id == productId && x.BranchId == branchId, cancellationToken);
        var before = product.StockQuantity;
        var after = checked(before + delta);
        if (after < 0) throw new InvalidOperationException($"Insufficient stock for SKU {product.Sku}.");
        product.StockQuantity = after;
        product.Version++;
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = productId, BranchId = branchId, UserId = userId, Action = action,
            Quantity = delta, PreviousStock = before, NewStock = after, Source = source,
            IdempotencyKey = idempotencyKey
        });
        if (product.ShopifySyncEnabled && product.ShopifyInventoryItemId.HasValue)
            db.SyncJobs.Add(new SyncJob
            {
                Type = "inventory.set", IdempotencyKey = $"shopify:{idempotencyKey}",
                Payload = JsonSerializer.Serialize(new InventorySyncPayload(productId, after))
            });
        await db.SaveChangesAsync(cancellationToken);
        return after;
    }
}

public sealed class SaleService(ScaloraDbContext db) : ISaleService
{
    public async Task<SaleResult> CheckoutAsync(CreateSale request, CancellationToken cancellationToken)
    {
        if (request.Items.Count == 0) throw new ArgumentException("A sale requires at least one item.");
        if (request.Items.Any(x => x.Quantity <= 0)) throw new ArgumentException("Quantities must be positive.");
        if (request.Items.Select(x => x.ProductId).Distinct().Count() != request.Items.Count)
            throw new ArgumentException("Combine duplicate product lines before checkout.");
        var existingMovement = await db.InventoryMovements.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdempotencyKey.StartsWith(request.IdempotencyKey + ":"), cancellationToken);
        if (existingMovement is not null)
        {
            var existingSale = await db.Sales.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(existingMovement.Source), cancellationToken);
            return new(existingSale.Id, existingSale.ReceiptNumber, existingSale.Total);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ids = request.Items.Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Products.Where(x => ids.Contains(x.Id) && x.BranchId == request.BranchId)
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        if (products.Count != ids.Length) throw new KeyNotFoundException("One or more products were not found.");

        var sale = new Sale
        {
            BranchId = request.BranchId, CashierId = request.CashierId, CustomerId = request.CustomerId,
            PaymentMethod = request.PaymentMethod, Discount = request.Discount,
            ReceiptNumber = $"S-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..20]
        };
        foreach (var item in request.Items)
        {
            var product = products[item.ProductId];
            if (product.StockQuantity < item.Quantity) throw new InvalidOperationException($"Insufficient stock for {product.Sku}.");
            var unitPrice = item.UnitPrice ?? product.SellingPrice;
            sale.Items.Add(new SaleItem { SaleId = sale.Id, ProductId = product.Id, ProductName = product.Name,
                Sku = product.Sku, Quantity = item.Quantity, UnitPrice = unitPrice, UnitCost = product.PurchasePrice });
            var before = product.StockQuantity;
            product.StockQuantity -= item.Quantity;
            product.Version++;
            var key = $"{request.IdempotencyKey}:{product.Id}";
            db.InventoryMovements.Add(new InventoryMovement { ProductId = product.Id, BranchId = request.BranchId,
                UserId = request.CashierId, Action = InventoryAction.Sale, Quantity = -item.Quantity,
                PreviousStock = before, NewStock = product.StockQuantity, Source = sale.Id.ToString(), IdempotencyKey = key });
            if (product.ShopifySyncEnabled && product.ShopifyInventoryItemId.HasValue)
                db.SyncJobs.Add(new SyncJob { Type = "inventory.set", IdempotencyKey = $"shopify:{key}",
                    Payload = JsonSerializer.Serialize(new InventorySyncPayload(product.Id, product.StockQuantity)) });
        }
        sale.Subtotal = sale.Items.Sum(x => x.UnitPrice * x.Quantity);
        sale.Tax = Math.Round((sale.Subtotal - sale.Discount) * request.TaxRate, 2, MidpointRounding.AwayFromZero);
        sale.Total = sale.Subtotal - sale.Discount + sale.Tax;
        db.Sales.Add(sale);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(sale.Id, sale.ReceiptNumber, sale.Total);
    }
}

public sealed record InventorySyncPayload(Guid ProductId, int Quantity);

public sealed class ShopifyClient(HttpClient http, ScaloraDbContext db, ISecretProtector secrets) : IShopifyClient
{
    private const string ApiVersion = "2025-10";
    public async Task<bool> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var result = await GraphQlAsync(connectionId, "query { shop { name } }", null, cancellationToken);
        return result.RootElement.TryGetProperty("data", out _);
    }

    public async Task SetInventoryAsync(Guid connectionId, long inventoryItemId, long locationId, int quantity,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        const string mutation = "mutation Set($input: InventorySetQuantitiesInput!) { inventorySetQuantities(input: $input) { userErrors { field message } } }";
        var variables = new { input = new { name = "available", reason = "correction", ignoreCompareQuantity = true,
            referenceDocumentUri = $"scalora://sync/{idempotencyKey}", quantities = new[] { new { inventoryItemId = $"gid://shopify/InventoryItem/{inventoryItemId}", locationId = $"gid://shopify/Location/{locationId}", quantity } } } };
        var result = await GraphQlAsync(connectionId, mutation, variables, cancellationToken);
        if (result.RootElement.TryGetProperty("errors", out var errors)) throw new InvalidOperationException(errors.ToString());
    }

    private async Task<JsonDocument> GraphQlAsync(Guid connectionId, string query, object? variables, CancellationToken ct)
    {
        var connection = await db.ShopifyConnections.AsNoTracking().SingleAsync(x => x.Id == connectionId && x.IsActive, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{connection.StoreDomain}/admin/api/{ApiVersion}/graphql.json");
        request.Headers.Add("X-Shopify-Access-Token", secrets.Unprotect(connection.EncryptedAccessToken));
        request.Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}

public sealed class SyncJobWorker(IServiceScopeFactory scopes, ILogger<SyncJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatch(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Sync worker iteration failed"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task ProcessBatch(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ScaloraDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<IShopifyClient>();
        var jobs = await db.SyncJobs.Where(x => (x.Status == SyncJobStatus.Pending || x.Status == SyncJobStatus.Failed)
                && x.NextAttemptAt <= DateTimeOffset.UtcNow && x.Attempts < 10)
            .OrderBy(x => x.CreatedAt).Take(25).ToListAsync(ct);
        foreach (var job in jobs)
        {
            job.Status = SyncJobStatus.Processing; job.Attempts++;
            await db.SaveChangesAsync(ct);
            try
            {
                var payload = JsonSerializer.Deserialize<InventorySyncPayload>(job.Payload)!;
                var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == payload.ProductId, ct);
                var connection = await db.ShopifyConnections.AsNoTracking().SingleAsync(x => x.BranchId == product.BranchId && x.IsActive, ct);
                await client.SetInventoryAsync(connection.Id, product.ShopifyInventoryItemId!.Value,
                    product.ShopifyLocationId ?? long.Parse(connection.LocationId), payload.Quantity, job.IdempotencyKey, ct);
                job.Status = SyncJobStatus.Synced; job.LastSyncAt = DateTimeOffset.UtcNow; job.ErrorMessage = null;
            }
            catch (Exception ex)
            {
                job.Status = SyncJobStatus.Failed; job.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 2000)];
                job.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, Math.Min(job.Attempts, 8)));
                logger.LogWarning(ex, "Sync job {JobId} failed on attempt {Attempt}", job.Id, job.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
