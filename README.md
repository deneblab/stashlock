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
- **Authentication** — Bearer token auth: admin master keys, and API keys bound to one `box.tag`
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
stashlock publish ./my-vault/stashlock.enc.production.secrets.json --url http://localhost:8099 --api-key slk_...
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

- [Go 1.25+](https://go.dev/dl/) (for building the server from source)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for CLI and Client library)
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
      - STASHLOCK_MASTER_KEY=${STASHLOCK_MASTER_KEY}   # one-time key for the first /admin login (>= 32 chars)
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
2. Build the server (Go):
```bash
cd src/StashLock.Server
go build -o stashlock-server .
./stashlock-server
```

3. Build CLI and Client (.NET):
```bash
cd src
dotnet build StashLock.sln
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

Secrets are addressed as `<box>.<tag>.<version>` (e.g. `exampleapp.develop.00001`). Requests use `Authorization: Bearer <key>`:

| Key | Where it comes from | Can do |
|-----|---------------------|--------|
| **Master key** | Server env `STASHLOCK_MASTER_KEY` | **One time only**: the first `/admin` login exchanges it for an admin key, then it is blocked. Never accepted by the API. |
| **Admin key** (`slk_adm_...`) | Shown once after the master key exchange, or created by another admin | Administer: dashboard, `/keys`, `/audit`, `/stats`. **Cannot** read or write secrets (`/boxes` → 403). |
| **API key** (`slk_...`) | Created by an admin; apps / CI use it as `STASHLOCK_API_KEY` | Read/write/delete secrets of exactly one `box.tag` (all versions), per its permissions. |

### Master key (one-time)
Set `STASHLOCK_MASTER_KEY` (at least 32 characters, e.g. `openssl rand -base64 32`) and open `/admin`. Logging in with it creates an admin key, shows it **once**, and blocks the master key for good (its hash is recorded in the database). From then on log in with the admin key. Further admins are created in the dashboard (**API Keys → Create Admin Key**); the last active admin key cannot be revoked.

The server **refuses to start** when nobody could administer it: no active admin key and no unused master key. There is no open access mode.

**Lost every admin key?** Set a new value for `STASHLOCK_MASTER_KEY` and restart — a never-used master key works once again.

### API keys
Created by an admin for one `box.tag`:
- **Box / Tag** — the key works only on `<box>.<tag>.*`; other secrets return 403
- **Permissions** — Read, Write, Delete (default: read only)
- **Rate Limiting** — Per-key request limits
- **Expiration** — Optional key expiry

```bash
# Create an API key for exampleapp.develop (admin key required)
curl -X POST http://localhost:8099/keys \
  -H "Authorization: Bearer slk_adm_..." \
  -H "Content-Type: application/json" \
  -d '{"name":"exampleapp-ci","box":"exampleapp","tag":"develop","canRead":true,"canWrite":true}'
```

The plaintext key (`slk_...`) is returned once. Give it to the app or CI as `STASHLOCK_API_KEY`.

Typical flow: set `STASHLOCK_MASTER_KEY` → first login at `/admin` → save the admin key → create an API key per `box.tag` → app/CI uses it to publish and read.

## API Documentation

### Endpoints

#### Boxes

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/` | No | Health check — returns `OK ; Version: {version}` |
| GET | `/boxes` | API key | List keys of the API key's `box.tag` (optional `?prefix=` filter) |
| POST | `/boxes/{key}` | Yes | Store content (optional `?expiresIn=` TTL in seconds) |
| GET | `/boxes/{key}` | Yes | Retrieve content |
| DELETE | `/boxes/{key}` | Yes | Soft-delete a key |
| GET | `/boxes/{key}/meta` | Yes | Get key metadata (size, timestamps, version count) |
| GET | `/boxes/{key}/history` | Yes | Get version history |
| GET | `/boxes/{key}/history/{ver}` | Yes | Retrieve a specific historical version |
| POST | `/boxes/decode` | Yes | Decode and decrypt a stored value (JSON body) |

#### API Key Management (admin key only)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/keys` | Create an API key (`name`, `box`, `tag`, `canRead`, `canWrite`, `canDelete`, `expiresAt`, `rateLimitPerMinute`) or an admin key (`name`, `isAdmin: true`) |
| GET | `/keys` | List API keys (optional `?box=`, `?tag=`) |
| GET | `/keys/{id}` | Get API key details |
| DELETE | `/keys/{id}` | Revoke an API key (soft) |
| DELETE | `/keys/{id}/permanent` | Permanently delete an API key |

#### Admin (admin key only)

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

A built-in web dashboard is available at `/admin` for managing the vault through a browser.

**Features:**
- Overview with key counts, storage usage, and health status
- Browse, search, and delete secrets (secrets are published encrypted with `stashlock publish`)
- View key metadata and version history
- Manage API keys per `box.tag` (create, revoke, delete)
- Query audit logs with filtering

Login with an admin key at `/admin` (first time: the one-time master key). The session is an HttpOnly cookie valid for 8 hours and ends when its admin key is revoked.

## Configuration

### Server Configuration

The Go server reads configuration from `config.json` (optional) and environment variables. Environment variables take precedence.

**`config.json` example:**

```json
{
  "port": 8080,
  "workDir": "./work",
  "masterKey": "one-time-key-at-least-32-characters-long",
  "maxValueSizeKB": 100,
  "maxKeyLength": 120,
  "maxRequestBodySizeKB": 110,
  "defaultRateLimitPerMinute": 60,
  "allowedOrigins": "https://app.example.com",
  "adminAllowedNetworks": "",
  "trustedProxies": "",
  "logDir": "./logs",
  "logLevel": "info"
}
```

| Setting | Env Variable | Default | Description |
|---------|-------------|---------|-------------|
| `port` | `STASHLOCK_PORT` | 8080 | Server listen port |
| `workDir` | `STASHLOCK_WORK_DIR` | `./work` | Working directory (SQLite DB stored in `{workDir}/db/`) |
| `masterKey` | `STASHLOCK_MASTER_KEY` | *(empty)* | One-time key (≥ 32 chars) exchanged for an admin key at the first `/admin` login. Required until an admin key exists |
| `maxValueSizeKB` | `STASHLOCK_MAX_VALUE_SIZE_KB` | 100 | Maximum value size in KB |
| `maxKeyLength` | — | 120 | Maximum key length in characters |
| `defaultRateLimitPerMinute` | `STASHLOCK_DEFAULT_RATE_LIMIT` | 60 | Default rate limit for API keys |
| `allowedOrigins` | `STASHLOCK_ALLOWED_ORIGINS` | *(empty)* | Comma-separated CORS origins. Empty = CORS disabled (browsers cannot call the API cross-origin) |
| `adminAllowedNetworks` | `STASHLOCK_ADMIN_NETWORKS` | *(empty)* | CIDR whitelist for `/admin`. Empty = any address (login still required) |
| `trustedProxies` | `STASHLOCK_TRUSTED_PROXIES` | *(empty)* | CIDRs of reverse proxies whose `X-Forwarded-For` is trusted. Empty = always use the connection address |
| `logDir` | `STASHLOCK_LOG_DIR` | `./logs` | Log file directory |
| `logLevel` | `STASHLOCK_LOG_LEVEL` | `info` | Log level (debug, info, warn, error) |

### Logging

Application logs are written using zerolog with:
- Console output (human-readable)
- File-based logging to the log directory with rotation

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
| `STASHLOCK_PORT` | Server listen port (default: 8080) |
| `STASHLOCK_WORK_DIR` | Working directory for data (default: `./work`) |
| `STASHLOCK_MASTER_KEY` | One-time master key (≥ 32 chars) for the first `/admin` login |
| `STASHLOCK_DEFAULT_RATE_LIMIT` | Rate limit for API keys (per minute) |
| `STASHLOCK_ALLOWED_ORIGINS` | Comma-separated CORS origins (empty = CORS disabled) |
| `STASHLOCK_ADMIN_NETWORKS` | CIDR whitelist for `/admin` |
| `STASHLOCK_TRUSTED_PROXIES` | CIDRs of trusted reverse proxies (for `X-Forwarded-For`) |
| `STASHLOCK_LOG_DIR` | Log file directory (default: `./logs`) |
| `STASHLOCK_LOG_LEVEL` | Log level: debug, info, warn, error |

### Building the Docker Image

```bash
cd src/StashLock.Server
docker build -t stashlock-server:latest .
```

### Running with Docker

```bash
docker run -d \
  -p 8099:8080 \
  -e STASHLOCK_MASTER_KEY="$(openssl rand -base64 32)" \
  -v ./app/work:/app/work \
  -v ./app/log:/app/log \
  --name stashlock-server \
  ghcr.io/deneblab/stashlock-server:latest
```

## Development

### Local Development with .NET Aspire

The project includes an Aspire AppHost for local development orchestration (CLI + Client testing):

```bash
dotnet run --project src/AppHost
```

This will start the Aspire dashboard for monitoring and orchestration.

### Project Structure

```
StashLock/
├── src/
│   ├── StashLock.Server/      # Web API server (Go)
│   ├── StashLock.Cli/         # Command-line tool
│   ├── StashLock.Client/      # Client library (.NET 8.0)
│   ├── StashLock.Tests/       # xUnit integration & unit tests
│   ├── AppHost/                # .NET Aspire orchestration
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
| **401 Unauthorized** | Missing, invalid, revoked or expired key (the master key is never accepted by the API) | Check the `Authorization: Bearer <key>` header and the key's status in the dashboard. |
| **403 Forbidden** | Admin key used on `/boxes`, API key used on another `box.tag`, or missing permission | Use an API key created for the secret's `box.tag` with the needed read/write/delete permission. |
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
