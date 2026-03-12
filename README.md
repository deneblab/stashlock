# StashLock

A secure, lightweight key-value storage system (secrets vault) with a REST API, .NET client library, and command-line interface.

## Documentation

| Document | Description |
|----------|-------------|
| [Client Library](src/StashLock.Client/README.md) | Fluent API, connection strings, caching, IConfiguration integration |
| [CLI Tool](src/StashLock.Cli/README.md) | Commands: init, keygen, encode, decode, publish, cstring |
| [API Reference](#api-documentation) | REST endpoints, authentication, admin dashboard |
| [NuGet: Client](https://www.nuget.org/packages/Deneblab.StashLock.Client) | `dotnet add package Deneblab.StashLock.Client` |
| [NuGet: CLI](https://www.nuget.org/packages/Deneblab.StashLock.Cli) | `dotnet tool install -g Deneblab.StashLock.Cli` |
| [Docker Image](https://github.com/deneblab/stashlock/pkgs/container/stashlock-server) | `ghcr.io/deneblab/stashlock-server:latest` |

## Features

- **Secure Storage** — URL-safe base64 encoded keys, X25519 ECIES sealed-box encryption
- **REST API** — 17 endpoints for full secrets lifecycle management
- **Authentication** — Bearer token auth with master key and scoped API keys
- **RBAC** — Per-key read/write/delete permissions with box-level access control
- **Rate Limiting** — Configurable per-API-key request throttling
- **Versioning** — Automatic secret version history with rollback
- **TTL / Expiration** — Optional time-to-live with automatic cleanup
- **Soft Delete** — Safe deletion with retention, preventing accidental data loss
- **Audit Logging** — Full audit trail of all operations
- **Admin Dashboard** — Built-in web UI for managing keys, API keys, and viewing audit logs
- **SQLite Backend** — Reliable, file-based database storage
- **Docker Support** — Containerized deployment with docker-compose
- **CLI Tool** — Command-line interface for encrypting and managing secrets
- **Client Library** — Zero-dependency async-first .NET library

## Components

### StashLock.Server
Web API server (Go) for hosting the secrets vault service. Includes server-side decode endpoint with X25519 ECIES sealed-box decryption.

### StashLock.Cli
Command-line interface tool for encrypting and managing secrets. Available as a global .NET tool.

**Install:**
```bash
dotnet tool install -g Deneblab.StashLock.Cli
```

**Usage:**
```bash
stashlock init ./my-vault -tags production,develop
stashlock keygen ./my-vault
stashlock encode ./my-vault/secrets.json
stashlock decode ./my-vault/stashlock.enc.production.secrets.json
stashlock publish ./my-vault --url http://localhost:8099 --api-key YOUR_KEY
stashlock cstring ./my-vault --tag production          # generate connection string
stashlock cstring ./my-vault --tag production --base64 # base64 output only
```

[CLI Documentation](src/StashLock.Cli/README.md)

### StashLock.Client
Client library for programmatic access to secrets. Available on NuGet.

**Install:**
```bash
dotnet add package Deneblab.StashLock.Client
```

**Usage:**
```csharp
// Connection string (plain or base64)
var store = await StashLock.CreateClient()
    .WithConnectionString("Url=https://vault.example.com;ApiKey=slk_xxx;PrivateKey=base64key;Box=myapp;Tag=prod")
    .OpenAsync();

// Or via STASHLOCK_CONNECTION_STRING env var (auto-detected)
var store = await StashLock.CreateClient()
    .WithBox("myapp", "prod")
    .OpenAsync();

var secret = store["Database:Password"];
```

[Client Documentation](src/StashLock.Client/README.md)

## Installation

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for server and CLI)
- [Docker](https://www.docker.com/get-started) (for containerized deployment)

### Using Docker (Recommended)

1. Create a `docker-compose.yml`:
```yaml
services:
  stashlock-server:
    image: ghcr.io/deneblab/stashlock-server:latest
    container_name: stashlock-server
    ports:
      - "8099:8080"
    environment:
      - StashLock__ApiKey=your-secret-api-key   # remove this line for open access
    volumes:
      - ./app/work:/app/work
      - ./app/log:/app/log
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 10s
```

2. Start the server:
```bash
docker-compose up -d
```

The server will be available at `http://localhost:8099`

### Building from Source

1. Clone the repository
2. Build the solution:
```bash
cd src
dotnet build StashLock.sln
```

3. Run the server:
```bash
dotnet run --project StashLock.Server
```

## Quick Start

### Storing Content

```bash
# Store content with a key (with authentication)
curl -X POST http://localhost:8080/boxes/YOUR_KEY_HERE \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "Content-Type: text/plain" \
  -d "Your content here"
```

### Retrieving Content

```bash
# Retrieve content by key
curl http://localhost:8080/boxes/YOUR_KEY_HERE \
  -H "Authorization: Bearer YOUR_API_KEY"
```

### Health Check

```bash
# Check if server is running (no auth required)
curl http://localhost:8080/
```

## Authentication

StashLock supports Bearer token authentication with two key types:

### Master Key
Set via configuration (`StashLock:ApiKey`). Has full access to all endpoints including admin operations (API key management, audit logs, stats).

```bash
curl -H "Authorization: Bearer YOUR_MASTER_KEY" http://localhost:8080/boxes
```

### Scoped API Keys
Created via the master key. Support granular permissions:
- **Scopes** — Restrict access to specific boxes (e.g., `app1`, `app2`) or all (`*`)
- **Permissions** — Read, Write, Delete (independently togglable)
- **Rate Limiting** — Per-key request limits
- **Expiration** — Optional key expiry

```bash
# Create a scoped API key (master key required)
curl -X POST http://localhost:8080/keys \
  -H "Authorization: Bearer YOUR_MASTER_KEY" \
  -H "Content-Type: application/json" \
  -d '{"name":"my-service","scopes":["app1"],"canRead":true,"canWrite":true,"canDelete":false}'
```

When no master key is configured, the server runs in **open access mode** (no authentication required).

## API Documentation

Interactive Swagger UI is available at `/swagger` when the server is running.

### Endpoints

#### Boxes

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/` | No | Health check — returns `OK ; Version: {version}` |
| GET | `/boxes` | Yes | List all keys (optional `?prefix=` filter) |
| POST | `/boxes/{key}` | Yes | Store content (optional `?expiresIn=` TTL in seconds) |
| GET | `/boxes/{key}` | Yes | Retrieve content |
| DELETE | `/boxes/{key}` | Yes | Soft-delete a key |
| GET | `/boxes/{key}/meta` | Yes | Get key metadata (size, timestamps, version count) |
| GET | `/boxes/{key}/history` | Yes | Get version history |
| GET | `/boxes/{key}/history/{ver}` | Yes | Retrieve a specific historical version |
| POST | `/boxes/decode` | Yes | Decode and decrypt a stored value (JSON body) |

#### API Key Management (master key only)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/keys` | Create a scoped API key |
| GET | `/keys` | List all API keys |
| GET | `/keys/{id}` | Get API key details |
| DELETE | `/keys/{id}` | Revoke an API key (soft) |
| DELETE | `/keys/{id}/permanent` | Permanently delete an API key |

#### Admin (master key only)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/audit` | Query audit logs (`?boxKey=`, `?action=`, `?limit=`) |
| GET | `/stats` | Server statistics (key counts, storage size, API key counts) |
| GET | `/healthz` | Health check endpoint |

#### Decode Endpoint

`POST /boxes/decode` accepts a JSON body to fetch and decrypt a stored value server-side:

```json
{
  "box": "myapp",
  "tag": "prod",
  "version": "00001",
  "privateKey": "<base64-encoded X25519 private key>"
}
```

- `box` and `tag` are required
- `version` defaults to `"00001"` if omitted
- `privateKey` is the base64-encoded X25519 private key matching the public key used for encryption
- Returns the decrypted plaintext as `text/plain`

### Key Format

Keys are URL-safe base64 encoded strings (max 120 characters). Decoded keys use a `box.tag.version` segment format.

### Response Format

All mutation endpoints return structured JSON:
```json
{
  "status": "ok",
  "key": "myapp.config.v1",
  "message": "Deleted"
}
```

Errors return:
```json
{
  "status": 404,
  "title": "Not Found",
  "detail": "Key not found: myapp.config.v1",
  "errorCode": "KEY_NOT_FOUND",
  "traceId": "..."
}
```

## Admin Dashboard

A built-in web dashboard is available at `/dashboard` for managing the vault through a browser.

**Features:**
- Overview with key counts, storage usage, and health status
- Browse, search, create, and delete secrets
- View key metadata and version history
- Manage scoped API keys (create, revoke, delete)
- Query audit logs with filtering

Login with the master API key. The dashboard uses `sessionStorage` — credentials clear when the tab is closed.

## Configuration

### Server Configuration (`appsettings.json`)

```json
{
  "StashLock": {
    "ApiKey": "your-master-api-key",
    "MaxValueSizeKB": 100,
    "MaxKeyLength": 120,
    "MaxRequestBodySizeKB": 110,
    "DefaultRateLimitPerMinute": 60,
    "AllowedOrigins": "https://app.example.com"
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `ApiKey` | *(empty)* | Master API key. Empty = open access mode |
| `MaxValueSizeKB` | 100 | Maximum value size in KB |
| `MaxKeyLength` | 120 | Maximum key length in characters |
| `DefaultRateLimitPerMinute` | 60 | Default rate limit for scoped keys (0 = unlimited) |
| `AllowedOrigins` | *(empty)* | Comma-separated CORS origins. Empty = allow all |

Settings can also be set via environment variables using double-underscore notation:

```bash
StashLock__ApiKey=your-master-api-key
StashLock__DefaultRateLimitPerMinute=120
```

### Logging

Application logs are written using NLog with:
- Console output
- File-based logging to the logs directory (10 MB rotation, 3 archive files)

## Docker Deployment

### Using docker-compose

The provided `docker-compose.yml` pulls the image from GitHub Container Registry and includes:
- Automatic container restart
- Bind-mount volumes for data and logs
- Health checks via `/healthz`
- Port mapping (8099:8080)

```bash
# Start the service
docker-compose up -d

# View logs
docker-compose logs -f

# Stop the service
docker-compose down
```

### Data Persistence

Data is stored in bind-mounted directories:
- `./app/work` — SQLite database and working files
- `./app/log` — Application logs

### Environment Variables

| Variable | Description |
|----------|-------------|
| `ASPNETCORE_ENVIRONMENT` | Environment (Development/Production) |
| `ASPNETCORE_URLS` | Server binding URL (default: `http://+:8080`) |
| `StashLock__ApiKey` | Master API key for Bearer token auth |
| `StashLock__DefaultRateLimitPerMinute` | Rate limit for scoped API keys |
| `StashLock__AllowedOrigins` | Comma-separated CORS origins |

### Building the Docker Image

```bash
cd src/StashLock.Server
docker build -t stashlock-server:latest .
```

### Running with Docker

```bash
docker run -d \
  -p 8099:8080 \
  -e StashLock__ApiKey=your-secret-key \
  -v ./app/work:/app/work \
  -v ./app/log:/app/log \
  --name stashlock-server \
  ghcr.io/deneblab/stashlock-server:latest
```

## Development

### Local Development with .NET Aspire

The project includes an Aspire AppHost for local development orchestration:

```bash
dotnet run --project src/StashLock.AspireApp/StashLock.AspireApp.AppHost
```

This will start the Aspire dashboard and StashLock.Server with proper configuration.

### Project Structure

```
StashLock/
├── src/
│   ├── StashLock.Server/      # Web API server (Go)
│   ├── StashLock.Cli/         # Command-line tool
│   ├── StashLock.Client/      # Client library (.NET 8.0)
│   ├── StashLock.Tests/       # xUnit integration & unit tests
│   ├── StashLock.AspireApp/    # .NET Aspire orchestration
│   └── build/                 # Nuke build automation
├── docker-compose.yml         # Docker Compose configuration
└── README.md
```

### Building the Solution

```bash
cd src
dotnet restore
dotnet build
```

### Running Tests

```bash
dotnet test src/StashLock.Tests
```

## CI/CD

The project includes GitHub Actions workflows for:
- **Docker**: Building and publishing server images to GitHub Container Registry
- **NuGet**: Publishing CLI tool and Client library to NuGet.org
- **Versioning**: Automatic version management with `Deneblab.AbcVersion`

### Published Packages

- **NuGet.org**:
  - [Deneblab.StashLock.Cli](https://www.nuget.org/packages/Deneblab.StashLock.Cli) — CLI tool
  - [Deneblab.StashLock.Client](https://www.nuget.org/packages/Deneblab.StashLock.Client) — Client library

- **GitHub Container Registry**:
  - `ghcr.io/deneblab/stashlock-server` — Server image

Images and packages are tagged with:
- Version numbers (from `abcversion`)
- Git SHA
- `latest` tag for default branch

## Troubleshooting

### Common Errors

| Error | Cause | Fix |
|-------|-------|-----|
| **401 Unauthorized** | Missing or invalid API key | Check `Authorization: Bearer <key>` header. Verify the key matches `StashLock__ApiKey` or a valid scoped key. |
| **403 Forbidden** | Key lacks required permission or scope | Update the API key's permissions (read/write/delete) or scopes in the dashboard. Use `*` scope for unrestricted access. |
| **404 Key Not Found** | Key doesn't exist or has been deleted | Use `GET /boxes?prefix=<box>` to list available keys. Check for typos in the `box.tag.version` format. |
| **429 Too Many Requests** | Rate limit exceeded | Wait and retry after the `Retry-After` header value (seconds). Increase the key's rate limit in the dashboard. |
| **Decryption failed** | Wrong private key, corrupted data, or mode mismatch | Verify the private key matches the public key used for encryption. Confirm encryption mode (SOPS vs whole-file) is consistent. |
| **No key files found** (CLI) | Missing `stashlock.key.*.json` in vault directory | Run `stashlock keygen` to generate key files, or use `stashlock init` with `-tags` to create both config and keys. |
| **Config not found** (CLI) | Missing `stashlock.config.json` | Run `stashlock init <dir>` to create the vault configuration. |

### Checking Server Health

```bash
# Basic health check (no auth)
curl http://localhost:8099/

# Detailed stats (requires master key)
curl -H "Authorization: Bearer YOUR_KEY" http://localhost:8099/stats
```

### Viewing Logs

Server logs are written to the `./app/log` directory (or `/app/log` inside the container). Check these for detailed error information including trace IDs that match API error responses.

## License

See [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.
