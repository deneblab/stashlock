# Deneblab.StashLock.Cli

Command-line interface for StashLock secrets management vault. Uses X25519 ECIES (Elliptic Curve Integrated Encryption Scheme) with AES-256-GCM for keypair-based secrets encryption.

## Installation

```bash
dotnet tool install -g Deneblab.StashLock.Cli
```

## Usage

### 1. Initialize a vault directory

```bash
stashlock init ./my-vault -tags production,develop
```

Creates a `stashlock.config.json` with the specified environment tags and auto-generates key files for each tag.

### 2. Generate key pairs

```bash
stashlock keygen ./my-vault
```

Generates X25519 key pairs (`stashlock.key.{tag}.json`) for each tag defined in the config. Skips existing key files.

### 3. Encrypt secrets

```bash
stashlock encode ./my-vault/secrets.json
```

Reads a plaintext JSON secrets file, encrypts it with each tag's public key using sealed box encryption, and outputs encrypted files (`stashlock.enc.{tag}.secrets.json`).

Two encryption modes are supported:
- **SOPS mode** (default) — field-level encryption with MAC verification
- **Whole-file mode** — encrypts entire JSON as a single blob

### 4. Decrypt secrets

```bash
stashlock decode ./my-vault/stashlock.enc.production.secrets.json
```

Decrypts an encrypted `stashlock.enc.*.json` file back to plaintext JSON. Auto-detects encryption mode and key file.

**Options:**
- `-key <path>` — Specify key file path (auto-detected if omitted)
- `-output <path>` — Write to file instead of stdout

### 5. Publish to vault server

```bash
stashlock publish ./my-vault/stashlock.enc.production.secrets.json
```

Uploads encrypted `stashlock.enc.*.json` files to the StashLock vault API. Extracts the vault key from the file metadata and POSTs the content to the server.

Accepts a single file or a directory (publishes all `stashlock.enc.*.json` files found).

```bash
# Publish all encrypted files in a directory
stashlock publish ./my-vault

# Publish with explicit API URL and key
stashlock publish ./my-vault --url https://my-vault.example.com/api --api-key my-secret-key

# Publish with TTL (auto-expires after 3600 seconds)
stashlock publish ./my-vault/stashlock.enc.production.secrets.json --expires-in 3600
```

**Options:**
- `--url <url>` — API base URL (falls back to `STASHLOCK_API_URL` env var, then default)
- `--api-key <key>` — API authentication key (falls back to `STASHLOCK_API_KEY` env var)
- `--expires-in <seconds>` — TTL in seconds for the published secret

### 6. Generate connection string

```bash
# Interactive mode — lists tags, lets you pick
stashlock cstring ./my-vault

# Quick mode — skip prompts
stashlock cstring ./my-vault --tag production

# Base64 output only
stashlock cstring ./my-vault --tag production --base64

# Output as environment variable command
stashlock cstring ./my-vault --tag production --env
```

Reads `stashlock.config.json` (ServerUrl, ApiKey, Name) and `stashlock.key.{tag}.json` (PrivateKey) from the vault directory, generates a connection string in `Key=Value;Key=Value` format.

Output includes plain text, base64-encoded, and a copy-paste `set`/`export` command.

## Commands

| Command | Description |
|---------|-------------|
| `stashlock init <dir> [-tags <tags>]` | Initialize a vault directory with config and tags |
| `stashlock keygen <dir>` | Generate X25519 key pairs for each configured tag |
| `stashlock encode <file>` | Encrypt a secrets file for all configured tags |
| `stashlock decode <file> [-key <path>] [-output <path>]` | Decrypt a secrets file |
| `stashlock publish <path> [--url <url>] [--api-key <key>] [--expires-in <sec>]` | Publish encrypted files to vault server |
| `stashlock cstring <dir> [--tag <tag>] [--base64] [--env]` | Generate connection string from vault directory |

## Workflow

```
init  -->  keygen  -->  encode  -->  publish  -->  decode
 |           |            |           |             |
 v           v            v           v             v
config     keypairs    encrypted   uploaded      plaintext
 file       files       files     to server       output

         cstring (cs)
              |
              v
        connection string
       (for Client library)
```

## Encryption Details

- **Key Agreement:** X25519 ECIES (Elliptic Curve Integrated Encryption Scheme)
- **Key Derivation:** HKDF-SHA256
- **Symmetric Encryption:** AES-256-GCM
- **Key Size:** 32 bytes (X25519 public/private keys)
- **Nonce:** 12 bytes (random per encryption)
- **Auth Tag:** 16 bytes
- **Output format:** ephemeral public key (32B) || nonce (12B) || ciphertext || tag (16B)

## Environment Variables

| Variable | Purpose |
|----------|---------|
| `STASHLOCK_CONNECTION_STRING` | Full connection string (`Url=...;ApiKey=...;Box=...;Tag=...`) |
| `STASHLOCK_API_URL` | Vault API base URL |
| `STASHLOCK_API_KEY` | API authentication key |

## Links

- [StashLock Client Library](https://www.nuget.org/packages/Deneblab.StashLock.Client)
- [Repository](https://github.com/deneblab/stashlock)
- [Issues](https://github.com/deneblab/stashlock/issues)

## License

MIT
