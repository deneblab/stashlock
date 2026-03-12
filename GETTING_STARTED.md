# Getting Started with StashLock

This guide walks you through the most common StashLock workflows — from deploying the server to encrypting, publishing, and consuming secrets in your .NET applications.

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) or later
- [Docker](https://docs.docker.com/get-docker/) and Docker Compose (for server deployment)

---

## Scenario 1: Deploy the StashLock Server

### 1. Create a `docker-compose.yml`

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

> **Note:** Setting `StashLock__ApiKey` enables authentication. All API requests must include `Authorization: Bearer your-secret-api-key`. Remove the line to run in open access mode (development only). The port `8099` on the host maps to `8080` inside the container.

### 2. Start the server

```bash
docker compose up -d
```

### 3. Verify it's running

```bash
curl http://localhost:8099/
# Expected: OK ; Version: x.y.z
```

### 4. Explore the API

- **Swagger UI:** `http://localhost:8099/swagger`
- **Dashboard:** `http://localhost:8099/dashboard` (public — no authentication required)

### Data persistence

The SQLite database is stored in `/app/work/stashlock.sqlite` inside the container (bind-mounted to `./app/work`). Logs are in `./app/log`. Both persist across container restarts.

---

## Scenario 2: Start a New Project (CLI Workflow)

This scenario covers the full pipeline: initialize a vault, create secrets, encrypt them, and publish to the server.

### 1. Install the CLI

```bash
dotnet tool install -g Deneblab.StashLock.Cli
```

### 2. Initialize a vault directory

```bash
stashlock init ./my-vault -tags production,develop
```

This creates:
- `stashlock.config.json` — vault configuration
- `stashlock.key.production.json` — X25519 key pair for the `production` tag
- `stashlock.key.develop.json` — X25519 key pair for the `develop` tag

### 3. Set the vault name

Open `my-vault/stashlock.config.json` and set the `Name` field:

```json
{
  "Version": "00001",
  "Name": "myapp",
  "Tags": ["production", "develop"],
  "EncryptionMode": "sops"
}
```

The `Name` becomes part of the vault key: `myapp.production.00001`.

### 4. Create a plaintext secrets file

Create `my-vault/secrets.json`:

```json
{
  "Database": {
    "ConnectionString": "Server=db.example.com;Database=prod",
    "Password": "s3cret!"
  },
  "ExternalApi": {
    "Key": "ak_live_abc123"
  }
}
```

### 5. Encrypt the secrets

```bash
stashlock encode ./my-vault/secrets.json
```

Output:
```
Generated: stashlock.enc.production.secrets.json  (vault key: myapp.production.00001, mode: sops)
Generated: stashlock.enc.develop.secrets.json     (vault key: myapp.develop.00001, mode: sops)
```

Each tag gets its own encrypted file, sealed with that tag's public key.

### 6. Publish to the server

```bash
stashlock publish ./my-vault --url http://localhost:8080 --api-key your-secret-api-key
```

Output:
```
  stashlock.enc.production.secrets.json: Published (key: myapp.production.00001)
  stashlock.enc.develop.secrets.json: Published (key: myapp.develop.00001)

Publish complete: 2 published, 0 failed.
```

You can also set environment variables instead of passing flags:

```bash
export STASHLOCK_API_URL=http://localhost:8080
export STASHLOCK_API_KEY=your-secret-api-key
stashlock publish ./my-vault
```

### 7. Verify (optional)

Decrypt a file locally to confirm it round-trips correctly:

```bash
stashlock decode ./my-vault/stashlock.enc.production.secrets.json
```

### What to commit, what to keep secret

| File | Commit to git? |
|------|----------------|
| `stashlock.config.json` | Yes |
| `secrets.json` (plaintext) | **No** — add to `.gitignore` |
| `stashlock.enc.*.json` (encrypted) | Yes — safe to commit |
| `stashlock.key.*.json` (key pairs) | **No** — share securely, never commit |

Add to your `.gitignore`:

```
secrets.json
stashlock.key.*.json
```

---

## Scenario 3: Consume Secrets in Your Application

### 1. Install the client library

```bash
dotnet add package Deneblab.StashLock.Client
```

### 2. Set environment variables

The application needs the private key to decrypt secrets from the server:

```bash
export STASHLOCK_PRIVATE_KEY=<base64-encoded-private-key>
export STASHLOCK_API_URL=http://localhost:8080
export STASHLOCK_API_KEY=your-secret-api-key
```

The private key is the `PrivateKey` value from your `stashlock.key.production.json` file.

### Option A: Standalone API

```csharp
using Deneblab.StashLock.Client;

var store = await SecretsStoreAsync.OpenRemoteSealedAsync("myapp", "production", "00001");

var dbPassword = await store.GetAsync("Database:Password");
var apiKey = await store.GetAsync("ExternalApi:Key");

// Or get an entire section as a dictionary
var dbConfig = await store.GetSectionAsDictionaryAsync("Database");
```

### Option B: IConfiguration integration

```csharp
using Deneblab.StashLock.Client.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddStashLockRemote(
    box: "myapp",
    tag: "production",
    version: "00001");

var app = builder.Build();

// Access secrets via standard IConfiguration
var connString = app.Configuration["Database:ConnectionString"];
```

### Error handling

```csharp
using Deneblab.StashLock.Client;

try
{
    var store = await SecretsStoreAsync.OpenRemoteSealedAsync("myapp", "production", "00001");
    var secret = await store.GetAsync("Database:Password");
}
catch (VaultConfigurationException ex)
{
    // Missing or invalid configuration (env vars, vault key)
}
catch (VaultNotFoundException ex)
{
    // Key not found on the server
}
catch (DecryptionException ex)
{
    // Wrong private key or corrupted data
}
```

### Development mode

For local development, skip the server entirely with a plain JSON file:

```csharp
var store = await SecretsStoreAsync.TryOpenDevFileAsync();
```

This auto-discovers `dev/secrets/secrets.json` relative to the project root when running in Dev or Test mode.

---

## Scenario 4: Share Keys with Collaborators

Key files (`stashlock.key.{tag}.json`) contain both the public and private key. Here's how to share them safely.

### For collaborators who encrypt and publish

They need the full key file to run `encode` and `publish`:

1. Share `stashlock.key.production.json` via a secure channel (password manager, encrypted messaging, etc.)
2. The collaborator places it in their local vault directory alongside `stashlock.config.json`
3. They can now run:

```bash
stashlock encode ./my-vault/secrets.json
stashlock publish ./my-vault --url http://localhost:8080 --api-key your-secret-api-key
```

### For applications that only consume secrets

Applications only need the **private key** to decrypt — not the full key file:

1. Extract the `PrivateKey` value from `stashlock.key.production.json`
2. Set it as an environment variable on the deployment target:

```bash
export STASHLOCK_PRIVATE_KEY=<base64-encoded-private-key>
```

3. The application uses `OpenRemoteSealedAsync()` or `AddStashLockRemote()` to fetch and decrypt at runtime

### Security rules

- **Never** commit key files to version control
- **Never** share keys over unencrypted channels (email, Slack DMs, etc.)
- Use a password manager (e.g., Bitwarden, 1Password) or encrypted file transfer
- Rotate keys by running `stashlock keygen` with new tags and re-encrypting

---

## Scenario 5: Update Secrets in Production

When you need to change a secret value (rotate a password, update an API key, etc.):

### 1. Edit the plaintext secrets file

Update the values in your local `secrets.json`:

```json
{
  "Database": {
    "Password": "new-rotated-password"
  }
}
```

### 2. Re-encrypt and publish

```bash
stashlock encode ./my-vault/secrets.json
stashlock publish ./my-vault
```

This overwrites the previous encrypted values on the server. The server automatically saves the old value to version history.

### 3. Restart or wait for your application

Applications fetch secrets when they call `OpenRemoteSealedAsync()` — typically at startup. To pick up the new values:
- **Restart** the application, or
- Implement periodic refresh in your code

### Rolling back

If you need to revert to a previous version, use the dashboard:
1. Go to **Boxes** tab
2. Click **Details** on the key
3. Find the version in **History** and click **Restore**

Or via the API:
```bash
# Get version history
curl -H "Authorization: Bearer YOUR_KEY" http://localhost:8099/boxes/KEY/history

# Get a specific version's value
curl -H "Authorization: Bearer YOUR_KEY" http://localhost:8099/boxes/KEY/history/1

# Restore by re-publishing the old value
curl -X POST http://localhost:8099/boxes/KEY \
  -H "Authorization: Bearer YOUR_KEY" \
  -H "Content-Type: text/plain" \
  -d "old-value-here"
```

---

## Quick Reference

```
# Full workflow
stashlock init ./vault -tags production,develop    # 1. Create vault + keys
# Edit stashlock.config.json to set Name
# Create secrets.json with your secrets
stashlock encode ./vault/secrets.json              # 2. Encrypt
stashlock publish ./vault                          # 3. Upload to server

# In your app
dotnet add package Deneblab.StashLock.Client       # 4. Install client
# Set STASHLOCK_PRIVATE_KEY, STASHLOCK_API_URL, STASHLOCK_API_KEY
# Use SecretsStoreAsync.OpenRemoteSealedAsync() or AddStashLockRemote()
```

## Links

- [CLI Reference](src/StashLock.Cli/README.md)
- [Client Library Reference](src/StashLock.Client/README.md)
- [Server API Reference](README.md)
- [NuGet: Deneblab.StashLock.Client](https://www.nuget.org/packages/Deneblab.StashLock.Client)
- [NuGet: Deneblab.StashLock.Cli](https://www.nuget.org/packages/Deneblab.StashLock.Cli)
