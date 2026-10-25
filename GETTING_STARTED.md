# Getting Started with StashLock

This guide walks you through the most common StashLock workflows — from deploying the server to encrypting, publishing, and consuming secrets in your .NET applications.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later for the CLI; your application can target .NET 8 or newer
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
      - STASHLOCK_MASTER_KEY=${STASHLOCK_MASTER_KEY}   # one-time key, set in .env
    volumes:
      - ./app/work:/app/work
      - ./app/log:/app/log
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "/app/stashlock-servergo", "healthcheck"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 10s
```

The container runs as uid `10001`; create the data folders for it first:

```bash
mkdir -p app/work app/log && sudo chown -R 10001:10001 app/work app/log
```

Create a `.env` file next to `docker-compose.yml` with a one-time master key of 32+ characters:

```bash
echo "STASHLOCK_MASTER_KEY=$(openssl rand -base64 32)" > .env
```

> **Note:** The server refuses to start until someone can administer it. The master key works **once**: your first login at `/admin` exchanges it for an admin key (`slk_adm_...`, shown once — save it), then the master key is blocked. Admin keys manage the server; apps and CI use API keys created per `box.tag` (see Scenario 2, step 6). The port `8099` on the host maps to `8080` inside the container.

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

- **Dashboard:** `http://localhost:8099/admin` — first login with the master key from `.env`, then save the admin key it shows you and use that from now on

### Data persistence

The SQLite database is stored in `/app/work/db/stashlock.sqlite` inside the container (bind-mounted to `./app/work`). Logs are in `./app/log`. Both persist across container restarts.

---

## Scenario 2: Start a New Project (CLI Workflow)

This scenario covers the full pipeline: initialize a vault, create secrets, encrypt them, and publish to the server.

### 1. Install the CLI

```bash
dotnet tool install -g Deneblab.StashLock.Cli
```

### 2. Initialize a vault directory

```bash
stashlock init ./my-vault --name myapp --tags production,develop --url http://localhost:8099
```

This creates:
- `stashlock.config.json` — vault configuration (name, tags, server URL; safe to commit)
- `stashlock.key.production.json` — X25519 key pair for the `production` tag
- `stashlock.key.develop.json` — X25519 key pair for the `develop` tag
- `.gitignore` entry `stashlock.key.*.json`, so key files are not committed

Without `--name` the vault is named after its directory (`my-vault` → `my-vault`).

### 3. Check the vault name

`my-vault/stashlock.config.json` now contains:

```json
{
  "Version": "00001",
  "Name": "myapp",
  "Tags": ["production", "develop"],
  "EncryptionMode": "sops",
  "ServerUrl": "http://localhost:8099"
}
```

The `Name` and a tag form the `box.tag` an API key is created for, and the vault key: `myapp.production.00001`.

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

Each API key works on one `box.tag` (here `myapp.production` and `myapp.develop`). An admin creates them in the dashboard (**API Keys → Create API Key**, Box `myapp`, Tag `production`, Read + Write) or via the API:

```bash
curl -X POST http://localhost:8099/keys \
  -H "Authorization: Bearer slk_adm_..." \
  -H "Content-Type: application/json" \
  -d '{"name":"myapp-production-ci","box":"myapp","tag":"production","canRead":true,"canWrite":true}'
```

Then publish the file for that tag:

```bash
stashlock publish ./my-vault/stashlock.enc.production.secrets.json --url http://localhost:8099 --api-key slk_...
```

Output:
```
  stashlock.enc.production.secrets.json: Published (key: myapp.production.00001)

Publish complete: 1 published, 0 failed.
```

You can also use environment variables instead of flags (the URL may also come from `ServerUrl` in `stashlock.config.json`; the API key never does):

```bash
export STASHLOCK_API_URL=http://localhost:8099
export STASHLOCK_API_KEY=slk_...   # API key for myapp.production
stashlock publish ./my-vault/stashlock.enc.production.secrets.json
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

`stashlock init` already added `stashlock.key.*.json` to `.gitignore`. Add the plaintext file yourself:

```
secrets.json
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
export STASHLOCK_API_URL=http://localhost:8099
export STASHLOCK_API_KEY=slk_...   # read-only API key for myapp.production
```

The private key is the `PrivateKey` value from your `stashlock.key.production.json` file.

### Option A: Standalone API (Fluent Builder)

```csharp
using Deneblab.StashLock.Client;

var store = await StashLock.CreateClient()
    .WithBox("myapp", "production")
    .OpenAsync();

var dbPassword = store["Database:Password"];
var apiKey = store["ExternalApi:Key"];

// Or get a typed section
var dbConfig = store.GetSection<Dictionary<string, string>>("Database");
```

### Option B: IConfiguration integration

```csharp
using Deneblab.StashLock.Client.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddStashLock(cfg => cfg
    .WithBox("myapp", "production"));

var app = builder.Build();

// Access secrets via standard IConfiguration
var connString = app.Configuration["Database:ConnectionString"];
```

### Error handling

```csharp
using Deneblab.StashLock.Client;
using Deneblab.StashLock.Client.Common.Exceptions;

try
{
    var store = await StashLock.CreateClient()
        .WithBox("myapp", "production")
        .OpenAsync();
    var secret = store["Database:Password"];
}
catch (VaultConfigurationException ex)
{
    // Missing or invalid configuration (env vars, vault key)
}
catch (VaultNotFoundException ex)
{
    // Key not found on the server
}
catch (VaultAccessDeniedException ex)
{
    // 401: API key wrong, revoked or expired; 403: key not valid for myapp.production
}
catch (DecryptionException ex)
{
    // Wrong private key or corrupted data
}
```

### Development mode

For local development, skip the server entirely with a plain JSON file:

```csharp
var store = await StashLock.CreateClient()
    .FromDevFile()
    .OpenAsync();
```

In Dev or Test mode this finds `dev/secrets/secrets.json` in the root of your git repository (plain or nested JSON, e.g. `{"Database": {"Password": "dev"}}`). Outside a git checkout, pass the path: `FromDevFile("path/to/secrets.json")`.

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
stashlock publish ./my-vault/stashlock.enc.production.secrets.json --url http://localhost:8099 --api-key slk_...
```

### For applications that only consume secrets

Applications only need the **private key** to decrypt — not the full key file:

1. Extract the `PrivateKey` value from `stashlock.key.production.json`
2. Set it as an environment variable on the deployment target:

```bash
export STASHLOCK_PRIVATE_KEY=<base64-encoded-private-key>
```

3. The application uses `StashLock.CreateClient().WithBox().OpenAsync()` or `AddStashLock()` to fetch and decrypt at runtime

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
STASHLOCK_API_KEY=slk_... stashlock publish ./my-vault/stashlock.enc.production.secrets.json
```

This overwrites the previous encrypted values on the server. The server automatically saves the old value to version history.

### 3. Restart or wait for your application

Applications fetch secrets when they call `OpenAsync()` — typically at startup. To pick up the new values:
- **Restart** the application, or
- Implement periodic refresh in your code

### Rolling back

To revert to an earlier value, use the dashboard:
1. Open the **Boxes** tab
2. Click the entry (e.g. `myapp.production.00001`)
3. In **History**, click **Restore** on the revision you want; the current value is kept in history

To inspect history through the API (keys in the URL are base64url-encoded):
```bash
KEY=$(printf 'myapp.production.00001' | base64 | tr '+/' '-_' | tr -d '=')
curl -H "Authorization: Bearer $STASHLOCK_API_KEY" http://localhost:8099/boxes/$KEY/history
curl -H "Authorization: Bearer $STASHLOCK_API_KEY" http://localhost:8099/boxes/$KEY/history/1   # encrypted value of revision 1
```

---

## Quick Reference

```
# Full workflow
stashlock init ./vault --name myapp --tags production,develop --url http://localhost:8099   # 1. Vault + keys
# Create secrets.json with your secrets
stashlock encode ./vault/secrets.json                                      # 2. Encrypt
STASHLOCK_API_KEY=slk_... stashlock publish ./vault/stashlock.enc.production.secrets.json   # 3. Upload (key for myapp.production)

# In your app
dotnet add package Deneblab.StashLock.Client       # 4. Install client
# Set STASHLOCK_CONNECTION_STRING (or individual env vars)
# Use StashLock.CreateClient().WithBox().OpenAsync() or AddStashLock()
```

## Links

- [CLI Reference](src/StashLock.Cli/README.md)
- [Client Library Reference](src/StashLock.Client/README.md)
- [Server API Reference](README.md)
- [NuGet: Deneblab.StashLock.Client](https://www.nuget.org/packages/Deneblab.StashLock.Client)
- [NuGet: Deneblab.StashLock.Cli](https://www.nuget.org/packages/Deneblab.StashLock.Cli)
