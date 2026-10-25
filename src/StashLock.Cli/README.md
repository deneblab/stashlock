# Deneblab.StashLock.Cli

Command-line interface for StashLock secrets management vault. Uses X25519 ECIES (Elliptic Curve Integrated Encryption Scheme) with AES-256-GCM for keypair-based secrets encryption.

## Installation

```bash
dotnet tool install -g Deneblab.StashLock.Cli
```

## Usage

### 1. Initialize a vault directory

```bash
stashlock init ./my-vault --tags production,develop
stashlock init ./my-vault --name myapp --tags production,develop --url https://vault.example.com
```

Creates a `stashlock.config.json` with the specified environment tags, generates key files for each tag (file mode 0600) and adds `stashlock.key.*.json` to `.gitignore`. The vault name (the `box` in `box.tag.version`) is `--name`, or the target directory's name. Options: `--tags`/`-t`, `--name`, `--url`.

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

Two encryption modes are supported (`EncryptionMode` in `stashlock.config.json`):
- **SOPS mode** (default) — field-level encryption with MAC verification
- **Whole-file mode** — encrypts entire JSON as a single blob

SOPS files are written in format **v2** (`"mode": "sops-v2"`). Clients from before v2 cannot read them: update your applications (`Deneblab.StashLock.Client`) first, then re-encode and publish. During the transition, `--format 1` writes the older format. `decode` and the client read both formats.

```bash
stashlock encode ./my-vault/secrets.json --format 1   # only while some apps still use an older client
```

### 4. Decrypt secrets

```bash
stashlock decode ./my-vault/stashlock.enc.production.secrets.json
```

Decrypts an encrypted `stashlock.enc.*.json` file back to plaintext JSON. Auto-detects encryption mode and key file.

**Options:**
- `--key <path>` — Specify key file path (auto-detected if omitted)
- `-output <path>` — Write to file instead of stdout

### 5. Publish to vault server

```bash
stashlock publish ./my-vault/stashlock.enc.production.secrets.json
```

Uploads encrypted `stashlock.enc.*.json` files to the StashLock vault API. Extracts the vault key from the file metadata and POSTs the content to the server.

Accepts a single file or a directory (publishes all `stashlock.enc.*.json` files found).

```bash
# Publish one tag with an API key created for myapp.production
stashlock publish ./my-vault/stashlock.enc.production.secrets.json --url https://vault.example.com --api-key slk_...

# Publish all encrypted files in a directory (each file needs a key valid for its box.tag)
stashlock publish ./my-vault

# Publish with TTL (auto-expires after 3600 seconds)
stashlock publish ./my-vault/stashlock.enc.production.secrets.json --expires-in 3600
```

**Options:**
- `--url <url>` — API base URL (falls back to `STASHLOCK_API_URL`, then `ServerUrl` in `stashlock.config.json`; required — there is no default server)
- `--api-key <key>` — API key for the file's `box.tag` (falls back to `STASHLOCK_API_KEY`; never read from `stashlock.config.json`)
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

Reads `stashlock.config.json` (ServerUrl, Name), `stashlock.key.{tag}.json` (PrivateKey) and the API key (`--api-key` or `STASHLOCK_API_KEY`), generates a connection string in `Key=Value;Key=Value` format.

Output includes plain text, base64-encoded, and a copy-paste command (`export NAME="…"` on Linux/macOS, `set "NAME=…"` in Windows cmd). The plain string is also copied to the clipboard when `clip`, `pbcopy`, `wl-copy`, `xclip` or `xsel` is available.

## Commands

| Command | Description |
|---------|-------------|
| `stashlock init <dir> [--tags <tags>] [--name <name>] [--url <url>]` | Initialize a vault directory with config, keys and `.gitignore` |
| `stashlock keygen <dir>` | Generate X25519 key pairs for each configured tag |
| `stashlock encode <file>` | Encrypt a secrets file for all configured tags |
| `stashlock decode <file> [--key <path>] [--output <path>]` | Decrypt a secrets file |
| `stashlock publish <path> [--url <url>] [--api-key <key>] [--expires-in <sec>]` | Publish encrypted files to vault server |
| `stashlock cstring <dir> [--tag <tag>] [--api-key <key>] [--base64] [--env]` | Generate connection string from vault directory |

## Exit Codes and Output

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | Error (missing file or setting, rejected request, unknown command, missing argument) |
| `2` | Partial success: `publish` sent some files but others failed |

Errors, warnings and hints go to **stderr**; results go to **stdout**. `stashlock cstring … --base64` and `--env` print only the value on stdout, so they work in `$(…)`:

```bash
export STASHLOCK_CONNECTION_STRING=$(stashlock cstring ./my-vault --tag production --base64)
stashlock publish ./my-vault/stashlock.enc.production.secrets.json || echo "publish failed"
```

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

SOPS mode: a random 32-byte data key encrypts the values and is itself sealed with ECIES for the tag's public key.

| | v1 (`sops`) | v2 (`sops-v2`, default) |
|---|---|---|
| Value encryption | AES-256-GCM with the data key | AES-256-GCM with an HKDF-derived encryption key; the key path (e.g. `Db:Password`) is authenticated data, so a value cannot be moved to another key |
| MAC | HMAC-SHA256 with the data key over concatenated keys and values | HMAC-SHA256 with a separate HKDF-derived key over length-prefixed fields, including the vault key (`box.tag.version`) |

## Environment Variables

| Variable | Purpose |
|----------|---------|
| `STASHLOCK_CONNECTION_STRING` | Full connection string (`Url=...;ApiKey=...;Box=...;Tag=...`) |
| `STASHLOCK_API_URL` | Vault API base URL |
| `STASHLOCK_API_KEY` | API key for one `box.tag` (created by a StashLock admin) |

## Links

- [StashLock Client Library](https://www.nuget.org/packages/Deneblab.StashLock.Client)
- [Repository](https://github.com/deneblab/stashlock)
- [Issues](https://github.com/deneblab/stashlock/issues)

## License

MIT
