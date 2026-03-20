# Deneblab.StashLock.Client

Async-first .NET secrets management client for StashLock vault.

## Installation

```bash
dotnet add package Deneblab.StashLock.Client
```

## Quick Start

```csharp
using Deneblab.StashLock.Client;

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
    .FromDevFile()                    // auto-discovers dev/secrets/secrets.json
    .OpenAsync();

// Explicit plain JSON file
var store = await StashLock.CreateClient()
    .FromFile("./secrets.json")
    .OpenAsync();

// Local encrypted file (SOPS or whole-file mode)
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
| `.FromEncryptedFile(path)` | Local encrypted file (SOPS or whole-file) |
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

Single `AddStashLock` extension method with the same fluent builder:

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
| `STASHLOCK_API_KEY` | API authentication key (Bearer token) | Silent fallback |

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
catch (DecryptionException ex)
{
    // Decryption failed — wrong password or key
}
```

## Development Secrets File

Create a `secrets.json` in `dev/secrets/` under your project root:

```json
{
  "Database:ConnectionString": "Server=localhost;Database=dev",
  "Database:Password": "dev-password",
  "ExternalApi:Key": "dev-api-key"
}
```

`FromDevFile()` (without a path) auto-discovers this file when running in Dev or Test mode.

## License

MIT License - see LICENSE file for details

## Links

- [NuGet Package](https://www.nuget.org/packages/Deneblab.StashLock.Client)
- [Repository](https://github.com/deneblab/stashlock)
- [Issues](https://github.com/deneblab/stashlock/issues)
