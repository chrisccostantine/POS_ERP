using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scalora.Desktop;

public sealed record UserSession(string AccessToken, DateTime ExpiresAt, Guid Id, Guid BranchId, string DisplayName, string[] Roles);
public sealed record DashboardDto(decimal Revenue, int Orders, decimal InventoryValue, int LowStock, int PendingJobs, int FailedSyncs, DateTime? LastSync);
public sealed record ProductDto(Guid Id, Guid BranchId, string Name, string Sku, string? Barcode, decimal SellingPrice, int StockQuantity);
public sealed record SaleResultDto(Guid Id, string ReceiptNumber, decimal Total);

public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public ApiClient(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Host.Contains("YOUR-RAILWAY", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Set ApiBaseUrl in desktopsettings.json to the Railway public domain.");
        _http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(20) };
    }

    public void SetSession(UserSession session) => _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

    public async Task<UserSession> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/login", new { email, password }, JsonOptions, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Invalid email or password.");
        await EnsureSuccessAsync(response, ct);
        var session = await response.Content.ReadFromJsonAsync<UserSession>(JsonOptions, ct)
            ?? throw new InvalidOperationException("The login response was empty.");
        SetSession(session);
        return session;
    }

    public Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default) => GetAsync<DashboardDto>("api/dashboard", ct);
    public Task<List<ProductDto>> GetProductsAsync(string search, CancellationToken ct = default) =>
        GetAsync<List<ProductDto>>($"api/products?search={Uri.EscapeDataString(search)}&page=1&pageSize=100", ct);

    public async Task<SaleResultDto> CheckoutAsync(UserSession session, IReadOnlyCollection<CartLine> lines, CancellationToken ct = default)
    {
        var request = new
        {
            branchId = session.BranchId, cashierId = session.Id, customerId = (Guid?)null, paymentMethod = "Cash",
            discount = 0m, taxRate = 0m,
            items = lines.Select(x => new { productId = x.Product.Id, quantity = x.Quantity, unitPrice = (decimal?)x.Product.Price }),
            idempotencyKey = $"desktop:{session.Id}:{Guid.NewGuid():N}"
        };
        using var response = await _http.PostAsJsonAsync("api/sales", request, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<SaleResultDto>(JsonOptions, ct)
            ?? throw new InvalidOperationException("The checkout response was empty.");
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new InvalidOperationException("The API response was empty.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"API request failed ({(int)response.StatusCode}): {detail[..Math.Min(detail.Length, 300)]}");
    }

    public void Dispose() => _http.Dispose();
}

public sealed class SessionStore
{
    private static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scalora", "session.dat");

    public void Save(UserSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var json = JsonSerializer.Serialize(session);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathName, encrypted);
    }

    public UserSession? Load()
    {
        if (!File.Exists(PathName)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(PathName), null, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<UserSession>(bytes);
            return session?.ExpiresAt > DateTime.UtcNow.AddMinutes(1) ? session : null;
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }

    public void Clear() { if (File.Exists(PathName)) File.Delete(PathName); }
}
