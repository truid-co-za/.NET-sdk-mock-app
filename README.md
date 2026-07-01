# truID Connect .NET — Example Application

End-to-end sample that exercises the truID Connect .NET SDK against a live environment.

## Flow

1. **Connect** — fetch company, brands, and providers
2. **Upload** — `POST /extracter-api/uploads` with a PDF bank statement
3. **Wait** — poll `GET /consultant-api/.../statuses/0` until terminal
4. **Products** — list, download each product, ZIP bundle, and typed summary

This maps to the Postman **delivery-api** product endpoints:

| Postman | SDK method |
|---------|------------|
| `GET .../products` | `GetProductsAsync()` |
| `GET .../products/{productId}` | `GetDataAsync(productId)` |
| `GET .../products/all` | `GetAllProductsAsync()` |
| `GET .../products/summary` | `GetProductsSummaryAsync()` |

Product types in Postman (`$product-address`, `$product-bank-statement`, etc.) are **dynamic product IDs** returned by the list call — not separate API routes.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- truID API key with access to the test (`tst`) environment
- A PDF bank statement file

## Configuration

Copy `.env.example` to `.env` and set your API key, or export variables:

```bash
export TRUID_CLIENT_ID=your_api_key_here
export TRUID_ENVIRONMENT=tst
```

No credentials are stored in source code.

## Run

From the repository root:

```bash
dotnet run --project TruID.Connect.Example -- --file /path/to/statement.pdf
```

Outputs are written to `./output/` by default:

- One file per product (`GetDataAsync`)
- `products-all.zip` (`GetAllProductsAsync`)
- `products-summary.json` (`GetProductsSummaryAsync`)

### Options

```
--file PATH          PDF to upload (required, repeatable)
--output-dir DIR     Output directory (default: ./output)
--name NAME          Consumer name
--id-number ID       Consumer ID number
--wait SECONDS       Status timeout (default: 300)
--interval SECONDS   Poll interval (default: 2)
-h, --help           Show help
```

## Local stub server

To test against the in-repo stub instead of the real API:

```bash
# Terminal 1
cd src/stub && pip install -r requirements.txt
STUB_HTTP=1 uvicorn server:app --port 8765

# Terminal 2 — create .truid.properties in cwd with {"tst":"localhost:8765"}
export TRUID_CLIENT_ID=test-api-key-stub
export TRUID_ENVIRONMENT=tst
dotnet run --project TruID.Connect.Example -- --file statement.pdf --no-verify-ssl
```
