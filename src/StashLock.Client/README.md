# Deneblab.StashLock.Client

Async-first .NET secrets management client for StashLock vault.

## Installation

```bash
dotnet add package Deneblab.StashLock.Client
```

## Quick Start

```csharp
using Deneblab.StashLock.Client;
using Deneblab.StashLock.Client.Common.Exceptions;

// Auto-reads STASHLOCK_CONNECTION_STRING env var
var store = await StashLock.CreateClient()
    .OpenAsync();

var dbPassword = store["Database:Password"];
```

## Fluent API

All access starts with `StashLock.CreateClient()` which returns a fluent builder.

### Connection string

The simplest way to configure StashLock — a single string with all connection parameters:

```csharp
// Explicit connection string
var store = await StashLock.CreateClient()
    .WithConnectionString("Url=https://vault.example.com;ApiKey=slk_xxx;PrivateKey=base64key;Box=myapp;Tag=prod")
    .OpenAsync();

// From STASHLOCK_CONNECTION_STRING env var
var store = await StashLock.CreateClient()
    .FromConnectionString()
    .OpenAsync();

// Zero-config: auto-reads STASHLOCK_CONNECTION_STRING when no source is configured
var store = await StashLock.CreateClient()
    .OpenAsync();

// Partial override: CS provides defaults, fluent calls override specific values
var store = await StashLock.CreateClient()
    .WithBox("myapp", "staging")   // overrides Box+Tag from CS env var
    .OpenAsync();
```

Connection string keys: `Url`, `ApiKey`, `PrivateKey`, `Box`, `Tag`. Supports optional base64 wrapping for safe transport.

### Remote sealed-box (X25519)

```csharp
var store = await StashLock.CreateClient()
    .WithBox("myapp", "prod")
    .WithPrivateKey(key)              // optional, falls back to env var
    .OpenAsync();
```

### Sealed-box with offline cache

```csharp
var store = await StashLock.CreateClient()
    .WithBox("myapp", "prod")
    .WithPrivateKey(key)
    .WithCache(ttl: TimeSpan.FromHours(2))
    .OpenAsync();

// With custom cache directory
var store = await StashLock.CreateClient()
    .WithBox("myapp", "prod")
    .WithCache(cacheDir: "/app/cache", ttl: TimeSpan.FromHours(4))
    .OpenAsync();
```

Cache files (`stashlock.cache.box.tag.version.bin`, format `SLC2`) are AES-256-GCM encrypted with a key derived from the private key and the machine/user; the header (creation time, TTL) and the file name are authenticated, so editing the TTL or copying a cache file over another entry is detected and the file is ignored. Cache files from earlier versions (`SLC1`) are ignored and rebuilt on the next successful server fetch. For SOPS v2 files the client also checks that the file belongs to the requested `box.tag.version`.

The cache answers only when the server cannot: connection errors, timeouts and 5xx responses. A rejected key (401/403) or a missing entry (404) is reported at once and never answered from the cache, so revoking an API key takes effect immediately.

### Cache-first strategy

By default, the client tries the server first and falls back to cache. Use `CacheFirst` to prioritize cached secrets (useful for low-latency startup or unreliable networks):

```csharp
var store = await StashLock.CreateClient()
    .WithBox("myapp", "prod")
    .WithCache(
        ttl: TimeSpan.FromHours(2),
        strategy: CacheStrategy.CacheFirst,      // use cache if valid, refresh in background
        serverTimeout: TimeSpan.FromSeconds(5))   // timeout for server requests (default: 10s)
    .OpenAsync();
```

| Strategy | Behavior |
|----------|----------|
| `ServerFirst` (default) | Try server, fall back to cache on failure |
| `CacheFirst` | Use cache if valid, fall back to server if expired/missing |

### Request timeout

```csharp
var store = await StashLock.CreateClient()
    .WithBox("myapp", "prod")
    .WithTimeout(TimeSpan.FromSeconds(15))   // default: 30s (or 10s when cache is enabled)
    .OpenAsync();
```

### Local files

```csharp
// Plain JSON file (development)
var store = await StashLock.CreateClient()
    .FromDevFile()                    // finds <repo root>/dev/secrets/secrets.json
    .OpenAsync();

// Explicit plain JSON file
var store = await StashLock.CreateClient()
    .FromFile("./secrets.json")
    .OpenAsync();

// Local encrypted file (stashlock.enc.*.json)
var store = await StashLock.CreateClient()
    .FromEncryptedFile("./stashlock.enc.prod.secrets.json")
    .WithPrivateKey(key)
    .OpenAsync();
```

### Configuration validation

```csharp
var result = await StashLock.CreateClient()
    .WithBox("myapp", "prod")
    .ValidateAsync();

if (!result.IsValid)
    foreach (var issue in result.Issues)
        Console.WriteLine($"ERROR: {issue}");
```

### Builder options

| Method | Description |
|--------|-------------|
| `.WithConnectionString(string)` | Parse an ADO.NET-style connection string (`Key=Value;Key=Value`) |
| `.FromConnectionString()` | Read connection string from `STASHLOCK_CONNECTION_STRING` env var |
| `.WithBox(box, tag, version?)` | Sealed-box mode with box/tag/version |
| `.FromDevFile(path?)` | Plain JSON file (auto-discovers if no path) |
| `.FromFile(path)` | Explicit plain JSON file |
| `.FromEncryptedFile(path)` | Local encrypted file (`stashlock.enc.*.json`) |
| `.WithApiUrl(url)` | Override API URL |
| `.WithApiKey(key)` | Override API key |
| `.WithPrivateKey(base64)` | X25519 private key for sealed-box decryption |
| `.WithCache(cacheDir?, ttl?, machineId?, strategy?, serverTimeout?)` | Enable encrypted offline cache |
| `.WithTimeout(TimeSpan)` | Set request timeout (default: 30s, or 10s with cache) |
| `.OpenAsync()` | Open the secrets store |
| `.ValidateAsync()` | Validate configuration without opening |

## Reading Secrets

```csharp
// Indexer (secrets are pre-loaded in memory)
string value = store["Database:Password"];

// TryGet (no exception on missing key)
if (store.TryGet("OptionalKey", out var value))
{
    // use value
}

// Section as dictionary
var dbConfig = store.GetSectionAsDictionary("Database");
// Returns: { "Host": "localhost", "Password": "secret", ... }

// Typed section (deserializes into a POCO)
var dbConfig = store.GetSection<DatabaseConfig>("Database");
```

## IConfiguration Integration

Single `AddStashLock` extension method with the same fluent builder. Remote sources load through the same code path as `OpenAsync()`, so timeouts, logging, cache strategies and `STASHLOCK_CONNECTION_STRING` behave identically (`AddStashLock(_ => { })` uses the connection string alone):

```csharp
using Deneblab.StashLock.Client.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Remote sealed-box vault with cache
builder.Configuration.AddStashLock(cfg => cfg
    .WithBox("myapp", "prod")
    .WithCache(cacheDir: "/app/cache", ttl: TimeSpan.FromHours(4))
);

var app = builder.Build();

// Access secrets via standard IConfiguration
var connString = app.Configuration["Database:ConnectionString"];
var apiKey = app.Configuration["ExternalService:ApiKey"];
```

### Dev/prod branching

```csharp
builder.ConfigureAppConfiguration((ctx, config) =>
{
    if (ctx.HostingEnvironment.IsDevelopment())
    {
        config.AddStashLock(cfg => cfg
            .FromDevFile("dev/secrets/secrets.json")
        );
    }
    else
    {
        config.AddStashLock(cfg => cfg
            .WithBox("myapp", ctx.HostingEnvironment.EnvironmentName.ToLower())
            .WithCache(ttl: TimeSpan.FromHours(2))
        );
    }
});
```

### Encrypted local file

```csharp
builder.Configuration.AddStashLock(cfg => cfg
    .FromEncryptedFile("secrets.enc.json")
    .WithPrivateKey(key)
);
```

## Dependency Injection

Register `ISecretsStore` as a singleton in your DI container:

```csharp
using Deneblab.StashLock.Client.Configuration;

builder.Services.AddStashLock(cfg => cfg
    .WithBox("myapp", "prod")
    .WithCache(ttl: TimeSpan.FromHours(2))
);

// Inject anywhere
public class MyService(ISecretsStore secrets)
{
    public string GetDbPassword() => secrets["Database:Password"];
}
```

## Environment Variables

| Variable | Purpose | Behavior |
|----------|---------|----------|
| `STASHLOCK_CONNECTION_STRING` | Full connection string (`Url=...;ApiKey=...;Box=...;Tag=...`) | Auto-read as defaults in `OpenAsync()` if set; explicit via `.FromConnectionString()` |
| `STASHLOCK_PRIVATE_KEY` | Base64-encoded X25519 private key | Silent fallback for `.WithBox()`, `.FromEncryptedFile()` |
| `STASHLOCK_API_URL` | Vault API base URL | Silent fallback |
| `STASHLOCK_API_KEY` | API key for the app's `box.tag` (Bearer token; read-only is enough) | Silent fallback |

## Error Handling

```csharp
try
{
    var store = await StashLock.CreateClient()
        .OpenAsync();
}
catch (VaultConfigurationException ex)
{
    // Missing or invalid configuration (env var, key format)
}
catch (VaultNotFoundException ex)
{
    // Vault entry not found on server
}
catch (VaultAccessDeniedException ex)
{
    // 401: API key wrong, revoked or expired; 403: key not valid for this box.tag
    // ex.StatusCode tells which. Never answered from the cache.
}
catch (DecryptionException ex)
{
    // Decryption failed — wrong password or key
}
```

## Development Secrets File

Create `dev/secrets/secrets.json` in the root of your git repository (flat keys or nested JSON):

```json
{
  "Database:ConnectionString": "Server=localhost;Database=dev",
  "Database:Password": "dev-password",
  "ExternalApi:Key": "dev-api-key"
}
```

`FromDevFile()` (without a path) finds this file when running in Dev or Test mode.

## License

MIT License - see LICENSE file for details

## Links

- [NuGet Package](https://www.nuget.org/packages/Deneblab.StashLock.Client)
- [Repository](https://github.com/deneblab/stashlock)
- [Issues](https://github.com/deneblab/stashlock/issues)
