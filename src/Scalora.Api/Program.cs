using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Scalora.Application;
using Scalora.Domain;
using Scalora.Infrastructure;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
try
{
    var builder = WebApplication.CreateBuilder(args);
    var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var railwayPort) ? railwayPort : 8080;
    builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));
    builder.Host.UseSerilog((context, config) => config.ReadFrom.Configuration(context.Configuration));
    builder.Services.AddScaloraInfrastructure(builder.Configuration);
    var jwtKey = builder.Configuration["Authentication:JwtKey"]
        ?? throw new InvalidOperationException("Authentication:JwtKey is required.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
            ValidIssuer = "Scalora", ValidAudience = "Scalora.Desktop",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)), ClockSkew = TimeSpan.FromMinutes(1)
        });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("Manager", p => p.RequireRole("Admin", "Manager"));
        options.AddPolicy("Admin", p => p.RequireRole("Admin"));
    });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddHealthChecks().AddDbContextCheck<ScaloraDbContext>();

    var app = builder.Build();
    app.UseSerilogRequestLogging();
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapPost("/api/auth/login", async (LoginRequest request, UserManager<AppUser> users, IConfiguration config) =>
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null || !user.IsActive) return Results.Unauthorized();
        if (await users.IsLockedOutAsync(user) || !await users.CheckPasswordAsync(user, request.Password))
        {
            if (user is not null) await users.AccessFailedAsync(user);
            return Results.Unauthorized();
        }
        await users.ResetAccessFailedCountAsync(user);
        var roles = await users.GetRolesAsync(user);
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new(ClaimTypes.Name, user.DisplayName) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Authentication:JwtKey"]!));
        var expires = DateTime.UtcNow.AddHours(8);
        var token = new JwtSecurityToken("Scalora", "Scalora.Desktop", claims, expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), expiresAt = expires, user.DisplayName, roles });
    });

    app.MapGet("/api/products", async (string? search, int page, int pageSize, ScaloraDbContext db, CancellationToken ct) =>
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.Products.AsNoTracking().Where(x => !x.IsArchived);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Name.Contains(search) || x.Sku.Contains(search) || x.Barcode == search);
        return Results.Ok(await query.OrderBy(x => x.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct));
    }).RequireAuthorization();

    app.MapPost("/api/sales", async (CreateSale request, ISaleService sales, CancellationToken ct) =>
        Results.Ok(await sales.CheckoutAsync(request, ct))).RequireAuthorization();

    app.MapPost("/api/inventory/adjust", async (InventoryAdjustment request, IInventoryService inventory, CancellationToken ct) =>
        Results.Ok(new { quantity = await inventory.AdjustAsync(request.ProductId, request.BranchId, request.Delta,
            InventoryAction.Adjustment, request.Source, request.IdempotencyKey, request.UserId, ct) }))
        .RequireAuthorization("Manager");

    app.MapGet("/api/dashboard", async (ScaloraDbContext db, CancellationToken ct) =>
    {
        var today = DateTimeOffset.UtcNow.Date;
        var sales = db.Sales.AsNoTracking().Where(x => x.CreatedAt >= today && x.Status == SaleStatus.Completed);
        return Results.Ok(new
        {
            revenue = await sales.SumAsync(x => x.Total, ct), orders = await sales.CountAsync(ct),
            inventoryValue = await db.Products.SumAsync(x => x.StockQuantity * x.PurchasePrice, ct),
            lowStock = await db.Products.CountAsync(x => x.StockQuantity <= x.ReorderLevel && !x.IsArchived, ct),
            pendingJobs = await db.SyncJobs.CountAsync(x => x.Status == SyncJobStatus.Pending, ct),
            failedSyncs = await db.SyncJobs.CountAsync(x => x.Status == SyncJobStatus.Failed, ct),
            lastSync = await db.SyncJobs.Where(x => x.Status == SyncJobStatus.Synced).MaxAsync(x => (DateTimeOffset?)x.LastSyncAt, ct)
        });
    }).RequireAuthorization();

    foreach (var route in new[] { "orders/create", "orders/update", "products/update", "inventory/update" })
        app.MapPost($"/webhooks/shopify/{route}", ShopifyWebhookHandler.HandleAsync);

    app.MapHealthChecks("/health");
    await DatabaseSeeder.SeedAsync(app.Services);
    await app.RunAsync();
}
catch (Exception ex) { Log.Fatal(ex, "Application terminated unexpectedly"); }
finally { await Log.CloseAndFlushAsync(); }

public sealed record LoginRequest(string Email, string Password);
public sealed record InventoryAdjustment(Guid ProductId, Guid BranchId, Guid? UserId, int Delta, string Source, string IdempotencyKey);

public static class ShopifyWebhookHandler
{
    public static async Task<IResult> HandleAsync(HttpRequest request, ScaloraDbContext db, ISecretProtector secrets,
        IInventoryService inventory, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("ShopifyWebhook");
        if (!request.Headers.TryGetValue("X-Shopify-Webhook-Id", out var webhookId) ||
            !request.Headers.TryGetValue("X-Shopify-Hmac-Sha256", out var signature) ||
            !request.Headers.TryGetValue("X-Shopify-Shop-Domain", out var shop) ||
            !request.Headers.TryGetValue("X-Shopify-Topic", out var topic)) return Results.BadRequest();
        var id = webhookId.ToString();
        var existingLog = await db.ShopifyWebhookLogs.SingleOrDefaultAsync(x => x.WebhookId == id, ct);
        if (existingLog?.ProcessedAt is not null) return Results.Ok();
        var connection = await db.ShopifyConnections.SingleOrDefaultAsync(x => x.StoreDomain == shop.ToString() && x.IsActive, ct);
        if (connection is null) return Results.Unauthorized();
        using var buffer = new MemoryStream(); await request.Body.CopyToAsync(buffer, ct); var body = buffer.ToArray();
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secrets.Unprotect(connection.EncryptedWebhookSecret)), body);
        if (!Convert.TryFromBase64String(signature.ToString(), new byte[expected.Length], out var written)) return Results.Unauthorized();
        var supplied = Convert.FromBase64String(signature.ToString());
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return Results.Unauthorized();

        var log = existingLog ?? new ShopifyWebhookLog { WebhookId = id, Topic = topic.ToString(), ShopDomain = shop.ToString() };
        if (existingLog is null) db.ShopifyWebhookLogs.Add(log);
        try
        {
            if (topic.ToString().Equals("inventory_levels/update", StringComparison.OrdinalIgnoreCase))
            {
                using var json = JsonDocument.Parse(body);
                var inventoryItemId = json.RootElement.GetProperty("inventory_item_id").GetInt64();
                var available = json.RootElement.GetProperty("available").GetInt32();
                var product = await db.Products.SingleOrDefaultAsync(x => x.ShopifyInventoryItemId == inventoryItemId, ct);
                if (product is not null)
                    await inventory.AdjustAsync(product.Id, product.BranchId, available - product.StockQuantity,
                        InventoryAction.ShopifyAdjustment, id, $"webhook:{id}", null, ct);
            }
            log.ProcessedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Processed Shopify webhook {WebhookId} topic {Topic}", id, topic.ToString());
            return Results.Ok();
        }
        catch (Exception ex)
        {
            log.Error = ex.Message[..Math.Min(ex.Message.Length, 2000)]; await db.SaveChangesAsync(ct);
            logger.LogError(ex, "Failed Shopify webhook {WebhookId}", id); return Results.StatusCode(500);
        }
    }
}

public static class DatabaseSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ScaloraDbContext>();
        if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in new[] { "Admin", "Manager", "Cashier" })
            if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole<Guid>(role));
        var branch = await db.Branches.OrderBy(x => x.Code).FirstOrDefaultAsync();
        if (branch is null) { branch = new Branch { Name = "Main Store", Code = "MAIN" }; db.Branches.Add(branch); await db.SaveChangesAsync(); }
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admin = await users.FindByEmailAsync("admin@scalora.com");
        if (admin is null)
        {
            admin = new AppUser { UserName = "admin@scalora.com", Email = "admin@scalora.com", EmailConfirmed = true,
                DisplayName = "Administrator", BranchId = branch.Id };
            var result = await users.CreateAsync(admin, "Admin123");
            if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
            await users.AddToRoleAsync(admin, "Admin");
        }
        if (!await db.Products.AnyAsync())
        {
            db.Products.AddRange(
                new Product { BranchId = branch.Id, Name = "Classic T-Shirt", Sku = "TS-001", Barcode = "100000000001", PurchasePrice = 8, SellingPrice = 19.99m, StockQuantity = 50, ReorderLevel = 10 },
                new Product { BranchId = branch.Id, Name = "Canvas Tote", Sku = "BG-001", Barcode = "100000000002", PurchasePrice = 5, SellingPrice = 14.99m, StockQuantity = 30, ReorderLevel = 8 });
            await db.SaveChangesAsync();
        }
    }
}
