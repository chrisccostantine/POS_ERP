# Scalora POS ERP

Scalora is a .NET 8 WPF and ASP.NET Core foundation for an offline-first retail POS/ERP with Shopify inventory synchronization.

## Implemented foundation

- Clean project boundaries: Domain, Application, Infrastructure, API, WPF Desktop, tests
- PostgreSQL by default; SQL Server selectable with `Database:Provider=SqlServer`
- ASP.NET Core Identity password hashing, account lockout, JWT sessions, and Admin/Manager/Cashier policies
- Branch-scoped catalog, customers, suppliers, sales, expenses, cash sessions, settings, and immutable inventory movements
- Atomic checkout: sale, stock deductions, audit records, and Shopify outbox jobs commit together
- Idempotent inventory operations and webhook delivery logging
- HMAC validation with constant-time comparison for all required Shopify webhook routes
- Encrypted Shopify secrets through ASP.NET Core Data Protection
- Retrying background sync worker with exponential backoff and absolute-quantity Shopify GraphQL updates
- Dashboard, product search, sales, and stock adjustment API endpoints
- Connected WPF login, live dashboard, product search, cart, and checkout workflow
- Windows DPAPI-protected session persistence
- PostgreSQL Docker Compose and focused inventory/idempotency tests

## Prerequisites

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- PostgreSQL 15+ or Docker Desktop
- Visual Studio 2022 17.8+ with .NET desktop development (recommended)

## Run locally

```powershell
docker compose up -d
$env:Authentication__JwtKey = "replace-with-a-random-secret-at-least-32-bytes"
dotnet restore
dotnet run --project src/Scalora.Api
dotnet run --project src/Scalora.Desktop
```

Swagger is available at `http://localhost:5000/swagger` (or the URL printed by ASP.NET Core). The seed account is `admin@scalora.com` / `Admin123`. Change this password immediately; it exists only to meet the requested initial bootstrap contract.

Configuration secrets must be provided through environment variables, Windows Credential Manager, Azure Key Vault, or another production secret provider. Never deploy the development database password or JWT key from `appsettings.json`.

## Shopify setup

Create a Shopify custom app with product and inventory read/write scopes. Store the Admin API token and webhook signing secret through the administrator-only connection workflow. Point Shopify webhooks to:

- `/webhooks/shopify/orders/create`
- `/webhooks/shopify/orders/update`
- `/webhooks/shopify/products/update`
- `/webhooks/shopify/inventory/update`

Inventory sync uses absolute quantities and unique reference URIs, making retries safe. `X-Shopify-Webhook-Id` prevents completed deliveries from executing twice.

## Production work remaining

This repository is a runnable vertical foundation, not the full production system described in the brief. Before release, complete the desktop API binding/login and durable local offline database, product/customer/supplier CRUD screens, returns/exchanges, cash reconciliation, receipt printing, Shopify product import and order payload processors, reports/PDF/Excel exports, backup/restore, migrations, multi-terminal concurrency/lease hardening, integration/load testing, installer signing, monitoring, and disaster-recovery validation.

The desktop client is configured through `src/Scalora.Desktop/desktopsettings.json` and currently targets the deployed Railway API. Shopify product/order webhook deliveries are authenticated and logged, but only inventory-level updates currently alter local inventory.

## Tests

```powershell
dotnet test
```

## Railway deployment

1. Create a Railway project from this GitHub repository.
2. Add a PostgreSQL service to the same Railway project.
3. In the API service, add references for `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, and `PGPASSWORD` from the PostgreSQL service.
4. Set `Authentication__JwtKey` to a random secret of at least 32 bytes.
5. Generate a public domain for the API service and verify `/health` returns a successful response.

Railway builds the root `Dockerfile`; only the ASP.NET API is deployed. PostgreSQL credentials are read from Railway's injected `PG*` variables. Do not deploy the WPF project as a Railway service.

For real deployments, create and review EF Core migrations rather than relying on first-run schema creation:

```powershell
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate --project src/Scalora.Infrastructure --startup-project src/Scalora.Api
dotnet ef database update --project src/Scalora.Infrastructure --startup-project src/Scalora.Api
```
