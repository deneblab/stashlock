# StashLock

[![NuGet CLI](https://img.shields.io/nuget/v/Deneblab.StashLock.Cli?label=Deneblab.StashLock.Cli&logo=nuget)](https://www.nuget.org/packages/Deneblab.StashLock.Cli)
[![NuGet Client](https://img.shields.io/nuget/v/Deneblab.StashLock.Client?label=Deneblab.StashLock.Client&logo=nuget)](https://www.nuget.org/packages/Deneblab.StashLock.Client)
[![Server image](https://img.shields.io/badge/ghcr.io-stashlock--server-blue?logo=docker)](https://github.com/deneblab/stashlock/pkgs/container/stashlock-server)
[![Server build](https://github.com/deneblab/stashlock/actions/workflows/docker-build-go.yml/badge.svg?branch=production)](https://github.com/deneblab/stashlock/actions/workflows/docker-build-go.yml)
[![NuGet build](https://github.com/deneblab/stashlock/actions/workflows/nuget-publish.yml/badge.svg?branch=production)](https://github.com/deneblab/stashlock/actions/workflows/nuget-publish.yml)

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

- **End-to-end encryption** — secrets are encrypted with X25519 ECIES (AES-256-GCM) by the CLI and decrypted by the client library; the server stores only ciphertext
- **REST API** — store, read, list, delete and version secrets addressed as `box.tag.version`
- **Access control** — one-time master key → admin keys; API keys bound to one `box.tag` with read/write/delete permissions, expiry and rate limits
- **Versioning** — history of earlier values with restore
- **TTL and soft delete** — optional expiry; deleted entries stay restorable for a retention period
- **Audit log** — reads, writes, sign-ins and key changes, kept for a configurable period (default 60 days)
- **Admin dashboard** — built-in web UI at `/admin`
- **SQLite** — single-file database with versioned migrations
- **Docker** — small image, compose examples including Traefik
- **CLI and .NET client** — `stashlock` global tool and `Deneblab.StashLock.Client` (depends on `Microsoft.Extensions.*` and BouncyCastle)

## Getting Started

| You want to… | Read |
|--------------|------|
| Go from zero to a working secret (server, vault, publish, read in an app) | [GETTING_STARTED.md](GETTING_STARTED.md) |
| Deploy the server (compose, Traefik, upgrades, backup) | [docs/docker-compose.md](docs/docker-compose.md) |
| Use the CLI | [src/StashLock.Cli/README.md](src/StashLock.Cli/README.md) |
| Read secrets from .NET | [src/StashLock.Client/README.md](src/StashLock.Client/README.md) |
| Call the REST API | [API Documentation](#api-documentation) below |

## Components

| Component | What it is | Install |
|-----------|------------|---------|
| `StashLock.Server` | Go web server: REST API, admin dashboard, SQLite | `ghcr.io/deneblab/stashlock-server` |
| `StashLock.Cli` | Creates vaults and keys, encrypts, decrypts and publishes (needs the .NET 10 runtime or newer) | `dotnet tool install -g Deneblab.StashLock.Cli` |
| `StashLock.Client` | .NET library that fetches and decrypts secrets, with cache and `IConfiguration` support (net8.0 and net10.0) | `dotnet add package Deneblab.StashLock.Client` |

## Quick API check

Keys in the URL are `box.tag.version`, base64url-encoded (no padding):

```bash
KEY=$(printf 'exampleapp.develop.00001' | base64 | tr '+/' '-_' | tr -d '=')

curl http://localhost:8099/healthz                                   # no auth
curl -H "Authorization: Bearer $STASHLOCK_API_KEY" \
     http://localhost:8099/boxes/$KEY                                # read (API key for exampleapp.develop)
```

Values are ciphertext produced by `stashlock encode`; publish them with `stashlock publish` rather than raw `curl`.

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
| DELETE | `/boxes/{key}` | Yes | Soft-delete a key (restorable in the dashboard until purged, see `deletedRetentionDays`) |
| GET | `/boxes/{key}/meta` | Yes | Get key metadata (size, timestamps, version count); 404 once deleted or expired |
| GET | `/boxes/{key}/history` | Yes | Get version history; 404 once deleted or expired |
| GET | `/boxes/{key}/history/{ver}` | Yes | Retrieve a specific historical version; 404 once deleted or expired |

> The server never decrypts: it stores ciphertext only. Decrypt with `stashlock decode` or the client library.

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

### Key Format

Keys are URL-safe base64 encoded strings (max `maxKeyLength`, 120 characters by default). Decoded keys use a `box.tag.version` segment format.

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
- **Overview** — box, storage and key counts, live server health, "Needs attention" (keys expiring within 7 days, never-used keys, failed sign-ins) and recent activity
- **Boxes** — entries grouped by `box.tag`, filters, pagination, "Show deleted" with restore; details show metadata, the encrypted value (never plaintext) and history with restore of earlier revisions. Secrets themselves are published encrypted with `stashlock publish`
- **Keys** — API keys per `box.tag` (access, expiry, last used, status) and admin keys; create, revoke, delete. The last active admin key cannot be removed
- **Audit** — every read, write, sign-in and key change, filtered by action, box key, key and date range

Light and dark themes follow the system setting; the layout works down to phone width.

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
| `maxRequestBodySizeKB` | `STASHLOCK_MAX_REQUEST_BODY_KB` | 110 | Largest accepted request body; bigger requests get 413. Must be ≥ `maxValueSizeKB` |
| `deletedRetentionDays` | `STASHLOCK_DELETED_RETENTION_DAYS` | 30 | Days a deleted entry stays restorable before it and its history are removed for good. `0` = keep forever |
| `auditRetentionDays` | `STASHLOCK_AUDIT_RETENTION_DAYS` | 60 | Days audit entries are kept before they are removed. `0` = keep forever |
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

Compose examples (minimal, Tailscale-only dashboard, behind Traefik), image tags, upgrades, shutdown and backup: [docs/docker-compose.md](docs/docker-compose.md). All settings are listed under [Configuration](#configuration).

To build the image yourself:

```bash
cd src/StashLock.Server
docker build -t stashlock-server:local .
```

## Development

### Prerequisites

- [Go](https://go.dev/dl/) matching `src/StashLock.Server/go.mod` (server)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`global.json` pins it). The CLI targets .NET 10; the client targets .NET 8 and .NET 10.

### Building

```bash
cd src/StashLock.Server && go build -o stashlock-server .     # server
dotnet build src/StashLock.Cli && dotnet build src/StashLock.Client
```

### Build tasks (Nuke)

```bash
./build.sh TestAll        # dotnet test + go vet + go test -race
./build.sh GoBuild        # server binary in artifacts/server/
./build.sh DockerBuild    # stashlock-server:<version> image
./build.sh PackCliTool    # CLI NuGet package in artifacts/
```

### Project Structure

```
StashLock/
├── src/
│   ├── StashLock.Server/      # Go server (API, dashboard, SQLite)
│   ├── StashLock.Cli/         # stashlock CLI (.NET tool, net10.0)
│   ├── StashLock.Client/      # Client library (net8.0, net10.0)
│   ├── StashLock.Shared/      # Crypto and file formats compiled into CLI and client
│   ├── StashLock.Tests/       # xUnit tests, incl. contract tests against the Go server
│   └── build/                 # Nuke build automation
├── docs/                      # Deployment guide (docker-compose.md)
├── docker-compose.yml         # Minimal compose file
├── GETTING_STARTED.md         # Step-by-step walkthrough
└── global.json                # .NET SDK version for contributors
```

### Running Tests

```bash
# Go server
cd src/StashLock.Server && go vet ./... && go test -race ./...

# .NET client and CLI (requires the .NET 10 SDK)
dotnet test src/StashLock.Tests
```

**Contract tests** run the .NET CLI and client against a real Go server. They need a fresh server, because the master key works only once:

```bash
cd src/StashLock.Server && go build -o /tmp/stashlock-server .
STASHLOCK_MASTER_KEY=local-contract-master-key-0123456789 STASHLOCK_PORT=18080 \
  STASHLOCK_WORK_DIR=$(mktemp -d) /tmp/stashlock-server &
cd ../.. && STASHLOCK_TEST_SERVER_URL=http://127.0.0.1:18080 \
  STASHLOCK_TEST_MASTER_KEY=local-contract-master-key-0123456789 \
  dotnet test src/StashLock.Tests --filter "FullyQualifiedName~Contract"
```

Without those two variables the contract tests return immediately.

## CI/CD

GitHub Actions workflows:
- **CI** (`ci.yml`): on every pull request — `go vet` + `go test -race`, `dotnet test`, and the contract tests against a freshly started server.
- **Docker** (`docker-build-go.yml`): runs CI first, then builds and publishes the server image to GitHub Container Registry.
- **NuGet** (`nuget-publish.yml`): runs CI first, then publishes the CLI tool and Client library to NuGet.org.
- **Versioning**: automatic version management with `Deneblab.AbcVersionCmd` (pinned version).

Third-party actions are pinned to commit SHAs.

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
| **413 Payload Too Large** | Request body bigger than `maxRequestBodySizeKB` | Publish a smaller file or raise `STASHLOCK_MAX_REQUEST_BODY_KB` (and `STASHLOCK_MAX_VALUE_SIZE_KB`). |
| **429 Too Many Requests** | The key's rate limit was exceeded, or 10 failed authentications came from your address within 5 minutes (blocked for 15 minutes) | Wait for the `Retry-After` header value (seconds). For rate limits, raise the key's limit in the dashboard; for blocks, fix the key that keeps failing. |
| **Decryption failed** | Wrong private key, modified file, or a file for another entry | Verify the private key matches the public key used for encryption and that the file was encoded for this `box.tag.version`. |
| **No key files found** (CLI) | Missing `stashlock.key.*.json` in vault directory | Run `stashlock keygen <dir>` to generate key files, or `stashlock init <dir> --tags production,develop` to create both config and keys. |
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
