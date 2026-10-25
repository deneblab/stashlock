# Changelog

All notable changes to StashLock (server image `ghcr.io/deneblab/stashlock-server`, NuGet packages `Deneblab.StashLock.Cli` and `Deneblab.StashLock.Client`).
Versions follow [Semantic Versioning](https://semver.org/); while the major version is 0, a minor release may contain breaking changes.

## [0.5.0] — 2026-10-25

A security redesign: new access model, hardened server, new dashboard, SOPS v2 file format. **This release breaks compatibility with 0.4.14 and earlier** — read [Upgrading from 0.4](#upgrading-from-04) before deploying.

> Builds published after 0.4.14 (NuGet 0.4.17 and the server images built on 2026-10-25) came automatically from intermediate commits and already contain part of these changes (access model, hardening, CI). Treat them as pre-releases of 0.5.0 and upgrade straight to 0.5.0.

### Upgrading from 0.4

Do these steps in this order.

1. **Back up** `./app/work/db/` (see [docs/docker-compose.md](docs/docker-compose.md#backup)).
2. **Start from a fresh database if you come from 0.4.14 or earlier.** The old `ApiKeys.Scopes` schema cannot be upgraded; the server refuses to start on it with a clear message. Move `./app/work/db/` away and re-publish your secrets after step 6. (Databases created by those intermediate builds are upgraded automatically.)
3. **Give the data folders to the container user**: the image now runs as uid `10001`.
   ```bash
   sudo chown -R 10001:10001 app/work app/log
   ```
4. **Update your compose file**:
   - replace `StashLock__ApiKey` / `STASHLOCK_API_KEY` with a one-time `STASHLOCK_MASTER_KEY` (≥ 32 characters, e.g. `openssl rand -base64 32`);
   - replace a `curl` health check with `["CMD", "/app/stashlock-servergo", "healthcheck"]` or remove it (the image has one; `curl` is no longer installed);
   - behind a reverse proxy that is used for the `/admin` IP allowlist, set `STASHLOCK_TRUSTED_PROXIES`;
   - set `STASHLOCK_ALLOWED_ORIGINS` only if a browser calls the API cross-origin (CORS is now off by default).
   See [docs/docker-compose.md](docs/docker-compose.md) for complete examples (including Traefik).
5. **Sign in once** at `/admin` with the master key and save the admin key it shows (`slk_adm_…`). The master key is then blocked.
6. **Create API keys per `box.tag`** in the dashboard (Read + Write for CI, Read for applications) and set them as `STASHLOCK_API_KEY`. Admin keys cannot read secrets.
7. **Update applications** to `Deneblab.StashLock.Client` 0.5.0 **before** re-encoding: older clients cannot read SOPS v2 files.
8. **Update the CLI** (`dotnet tool update -g Deneblab.StashLock.Cli`; needs the .NET 10 runtime), then re-encode and publish:
   ```bash
   stashlock encode ./vault/secrets.json
   STASHLOCK_API_KEY=slk_… stashlock publish ./vault/stashlock.enc.production.secrets.json --url https://vault.example.com
   ```
   While some applications still run an older client, use `stashlock encode --format 1`.
9. **Remove `ApiKey` from `stashlock.config.json`** if you had one; the CLI now reads the API key only from `--api-key` or `STASHLOCK_API_KEY`.

### Breaking changes

- **Access model**: the server no longer reads `STASHLOCK_API_KEY` and has no open access mode. A one-time `STASHLOCK_MASTER_KEY` is exchanged for admin keys at the first dashboard sign-in; API keys are bound to one `box.tag` (replaces scopes). Admin keys cannot use `/boxes`; the master key is never accepted by the API.
- **`POST /boxes/decode` removed** (it sent the private key to the server). Decrypt with `stashlock decode` or the client library.
- **API**: `POST /keys` takes `box` and `tag` (or `isAdmin: true`) instead of `scopes`; new keys are read-only unless `canWrite`/`canDelete` are set; `expiresAt` must be a future date or RFC 3339 time. History and metadata of deleted or expired entries return 404.
- **SOPS v2** is the default output of `stashlock encode`; clients before 0.5.0 cannot read it.
- **Client cache** format `SLC2`; caches written by older clients are ignored and rebuilt on the next successful server fetch.
- **Client**: 401/403 throw the new `VaultAccessDeniedException` and are never answered from the cache (401/403/404 used to fall back to cached data). `AddStashLock` loads remote sources through `OpenAsync` and honours `STASHLOCK_CONNECTION_STRING`.
- **CLI**: targets .NET 10; exit codes (0 ok, 1 error, 2 partial publish) and errors on stderr; `publish` has no default server URL; `init --api-key` removed and `ApiKey` dropped from `stashlock.config.json`; `init` names the vault after the target directory (or `--name`), not the current directory.
- **Docker image** runs as uid `10001`, without `curl`.
- **.NET Aspire AppHost** removed.

### Added

- Admin dashboard redesign: overview with "Needs attention", boxes grouped by `box.tag` with filters, pagination, deleted entries and restore, revision restore, API and admin key management with expiry and last-used, audit log filters and pagination, light/dark theme, phone layout, accessibility fixes.
- Audit entries for sign-ins, master key exchange, key changes (dashboard and API), deletes, restores and value reveals.
- Versioned database migrations (`SchemaVersion`).
- Retention for deleted entries: `STASHLOCK_DELETED_RETENTION_DAYS` (default 30, `0` = keep forever).
- Request body limit `STASHLOCK_MAX_REQUEST_BODY_KB` (default 110 KB, 413 above it).
- `STASHLOCK_TRUSTED_PROXIES` for `X-Forwarded-For`.
- Built-in `healthcheck` command for the Docker health check.
- CLI: `init --name`, `cstring --api-key`, `encode --format 1|2`; `.gitignore` entry for key files; clipboard copy on Linux/macOS.
- Client: targets net8.0 and net10.0; `VaultAccessDeniedException`; `FromDevFile()` finds `dev/secrets/secrets.json` in the repository root.
- CI on pull requests (Go vet/race tests, .NET tests, contract tests of the CLI and client against the Go server); publishing waits for it. Nuke targets `Test`, `GoBuild`, `GoTest`, `TestAll`.
- [docs/docker-compose.md](docs/docker-compose.md) deployment guide; `CHANGELOG.md`.

### Security

- Server refuses to start without a way to administer it; constant-time key comparison.
- `/admin` IP allowlist can no longer be bypassed with a forged `X-Forwarded-For`.
- CORS off unless configured.
- Brute-force protection: 10 failed authentications from one IP in 5 minutes block it for 15 minutes (API and dashboard).
- Server timeouts, request body limit, graceful shutdown on SIGTERM.
- Dashboard sessions expire server-side, `Secure` cookie behind HTTPS, cross-site POSTs refused.
- Unknown errors no longer leak details to clients; `/healthz` no longer shows database errors.
- SOPS v2: separate encryption/MAC keys, values bound to their keys, MAC covers the vault key; the client checks the file belongs to the requested entry.
- Cache header and file name authenticated.
- CLI writes private keys and decrypted output with mode 0600; `cstring` no longer puts the secret on a command line for the clipboard.

### Fixed

- Root `docker-compose.yml` set a variable the Go server never read, so it ran without authentication.
- `stashlock publish` defaulted to a third-party server URL.
- Cache served data for revoked API keys.
- Concurrent writes of one entry could break history numbering (`Set` now transactional).
- `FromDevFile(path)` returned nothing for the documented flat/nested JSON format.
- Long keys overflowing cards and dialogs in the dashboard.
- Numerous documentation errors (ports, `--tags`, C# usings, paths).

## [0.4.14] and earlier

Not documented here; see the git history.

[0.5.0]: https://github.com/deneblab/stashlock/compare/0025112...v0.5.0
