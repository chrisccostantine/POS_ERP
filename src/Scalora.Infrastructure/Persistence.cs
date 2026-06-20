using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scalora.Application;
using Scalora.Domain;

namespace Scalora.Infrastructure;

public sealed class AppUser : IdentityUser<Guid>
{
    public required string DisplayName { get; set; }
    public Guid? BranchId { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ScaloraDbContext(DbContextOptions<ScaloraDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<ShopifyConnection> ShopifyConnections => Set<ShopifyConnection>();
    public DbSet<ShopifyWebhookLog> ShopifyWebhookLogs => Set<ShopifyWebhookLog>();
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Product>().HasIndex(x => new { x.BranchId, x.Sku }).IsUnique();
        modelBuilder.Entity<Product>().HasIndex(x => new { x.BranchId, x.Barcode });
        modelBuilder.Entity<Product>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<Sale>().HasIndex(x => x.ReceiptNumber).IsUnique();
        modelBuilder.Entity<Sale>().HasMany(x => x.Items).WithOne().HasForeignKey(x => x.SaleId);
        modelBuilder.Entity<InventoryMovement>().HasIndex(x => x.IdempotencyKey).IsUnique();
        modelBuilder.Entity<InventoryMovement>().HasIndex(x => new { x.ProductId, x.CreatedAt });
        modelBuilder.Entity<ShopifyWebhookLog>().HasIndex(x => x.WebhookId).IsUnique();
        modelBuilder.Entity<SyncJob>().HasIndex(x => x.IdempotencyKey).IsUnique();
        modelBuilder.Entity<SyncJob>().HasIndex(x => new { x.Status, x.NextAttemptAt });
        modelBuilder.Entity<AppSetting>().HasIndex(x => x.Key).IsUnique();

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(x => x.GetProperties())
                     .Where(x => x.ClrType == typeof(decimal)))
        {
            property.SetPrecision(18);
            property.SetScale(2);
        }
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddScaloraInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Database:Provider"] ?? "PostgreSQL";
        var connection = BuildConnectionString(config);
        services.AddDbContext<ScaloraDbContext>(options =>
        {
            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)) options.UseSqlServer(connection);
            else options.UseNpgsql(connection);
        });
        services.AddIdentityCore<AppUser>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireUppercase = true;
            options.User.RequireUniqueEmail = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
        }).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<ScaloraDbContext>();
        services.AddDataProtection();
        services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<ISaleService, SaleService>();
        services.AddHttpClient<IShopifyClient, ShopifyClient>();
        services.AddHostedService<SyncJobWorker>();
        return services;
    }

    private static string BuildConnectionString(IConfiguration config)
    {
        var configured = config.GetConnectionString("Scalora");
        var host = config["PGHOST"];
        if (string.IsNullOrWhiteSpace(host))
            return configured ?? throw new InvalidOperationException("ConnectionStrings:Scalora is required.");

        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = int.TryParse(config["PGPORT"], out var port) ? port : 5432,
            Database = config["PGDATABASE"] ?? "railway",
            Username = config["PGUSER"] ?? "postgres",
            Password = config["PGPASSWORD"] ?? throw new InvalidOperationException("PGPASSWORD is required."),
            SslMode = Npgsql.SslMode.Prefer,
            Pooling = true,
            MaxPoolSize = 100
        };
        return builder.ConnectionString;
    }
}
