# Running StashLock with Docker Compose

## Docker image

```
ghcr.io/deneblab/stashlock-server
```

Built and published by `.github/workflows/docker-build-go.yml` on every push to `main` or `production` that touches `src/StashLock.Server/**`.

| Tag | Meaning |
|-----|---------|
| `latest` | Latest build of the default branch (`production`) |
| `production` / `main` | Latest build of that branch |
| `<version>` | Version computed by the build (e.g. `1.4.2`) |
| `sha-<commit>` | Exact commit (e.g. `sha-790d4c7`) |

```bash
docker pull ghcr.io/deneblab/stashlock-server:latest
```

For production deployments pin a version or `sha-` tag instead of `latest`.

The container listens on port `8080`, keeps data in `/app/work` (SQLite database at `/app/work/db/stashlock.sqlite`) and logs in `/app/log`. It runs as uid `10001` and has a built-in health check (`stashlock-servergo healthcheck`, which calls `/healthz`).

## Before the first start

The container runs as an unprivileged user (uid/gid `10001`). Create the bind-mounted folders and give them to that user:

```bash
mkdir -p app/work app/log
sudo chown -R 10001:10001 app/work app/log
```

Put the one-time master key in a `.env` file next to `docker-compose.yml`:

```bash
echo "STASHLOCK_MASTER_KEY=$(openssl rand -base64 32)" > .env
chmod 600 .env
```

The master key works **once**: the first login at `/admin` exchanges it for an admin key (`slk_adm_...`, shown once) and blocks it. The server refuses to start when there is neither an active admin key nor an unused master key.

## 1. Minimal (local machine / home network)

```yaml
services:
  stashlock-server:
    image: ghcr.io/deneblab/stashlock-server:latest
    container_name: stashlock-server
    ports:
      - "8099:8080"
    environment:
      - STASHLOCK_MASTER_KEY=${STASHLOCK_MASTER_KEY:-}   # one-time, only for the first /admin login
      - STASHLOCK_LOG_DIR=/app/log
    volumes:
      - ./app/work:/app/work      # database: ./app/work/db/stashlock.sqlite
      - ./app/log:/app/log
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "/app/stashlock-servergo", "healthcheck"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 10s
```

## 2. Dashboard restricted to Tailscale

The API stays reachable as usual; `/admin` only accepts connections from Tailscale addresses.

```yaml
services:
  stashlock-server:
    image: ghcr.io/deneblab/stashlock-server:latest
    container_name: stashlock-server
    ports:
      - "8099:8080"
    environment:
      - STASHLOCK_MASTER_KEY=${STASHLOCK_MASTER_KEY:-}
      - STASHLOCK_LOG_DIR=/app/log
      - STASHLOCK_ADMIN_NETWORKS=100.64.0.0/10
    volumes:
      - ./app/work:/app/work
      - ./app/log:/app/log
    restart: unless-stopped
```

> With plain port publishing Docker may replace the client address with the Docker bridge gateway (e.g. `172.x.x.1`), and the allowlist would then reject everyone. After the first start check what address the server sees in the log (`Admin access denied ... ip=`).

## 3. Behind Traefik (HTTPS with Let's Encrypt)

The server publishes no port; traffic goes through Traefik. The network has a fixed subnet so it can be listed in `STASHLOCK_TRUSTED_PROXIES`.

```yaml
services:
  traefik:
    image: traefik:v3.1
    command:
      - --providers.docker=true
      - --providers.docker.exposedbydefault=false
      - --entrypoints.web.address=:80
      - --entrypoints.web.http.redirections.entrypoint.to=websecure
      - --entrypoints.web.http.redirections.entrypoint.scheme=https
      - --entrypoints.websecure.address=:443
      - --certificatesresolvers.le.acme.email=admin@example.com
      - --certificatesresolvers.le.acme.storage=/letsencrypt/acme.json
      - --certificatesresolvers.le.acme.httpchallenge.entrypoint=web
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
      - ./letsencrypt:/letsencrypt
    networks:
      - stashlock
    restart: unless-stopped

  stashlock-server:
    image: ghcr.io/deneblab/stashlock-server:latest
    container_name: stashlock-server
    environment:
      - STASHLOCK_MASTER_KEY=${STASHLOCK_MASTER_KEY:-}
      - STASHLOCK_LOG_DIR=/app/log
      - STASHLOCK_TRUSTED_PROXIES=172.30.0.0/24       # subnet Traefik connects from
      - STASHLOCK_ADMIN_NETWORKS=100.64.0.0/10        # optional: /admin only from Tailscale
      # - STASHLOCK_ALLOWED_ORIGINS=https://app.example.com   # only if a browser calls the API
    volumes:
      - ./app/work:/app/work
      - ./app/log:/app/log
    networks:
      - stashlock
    labels:
      - traefik.enable=true
      - traefik.http.routers.stashlock.rule=Host(`vault.example.com`)
      - traefik.http.routers.stashlock.entrypoints=websecure
      - traefik.http.routers.stashlock.tls.certresolver=le
      - traefik.http.services.stashlock.loadbalancer.server.port=8080
    restart: unless-stopped

networks:
  stashlock:
    ipam:
      config:
        - subnet: 172.30.0.0/24
```

Traefik passes the real client address in `X-Forwarded-For` (by default it drops `X-Forwarded-*` headers sent by untrusted clients and sets its own). The server trusts that header only for connections coming from `172.30.0.0/24`; the same header sent directly by an outside client is ignored.

**Traefik already running in another compose project?** Drop the `traefik` service, attach StashLock to Traefik's network and trust that network's subnet:

```yaml
services:
  stashlock-server:
    # ... as above, with the same labels ...
    environment:
      - STASHLOCK_TRUSTED_PROXIES=<subnet of the traefik network>
    networks:
      - traefik

networks:
  traefik:
    external: true
```

Find the subnet with `docker network inspect traefik --format '{{(index .IPAM.Config 0).Subnet}}'`. If the server sits on several networks, add `traefik.docker.network=traefik` to the labels so Traefik connects through that one.

## Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `STASHLOCK_MASTER_KEY` | *(empty)* | One-time key (≥ 32 chars) for the first `/admin` login. Required until an admin key exists |
| `STASHLOCK_ADMIN_NETWORKS` | *(empty = any)* | CIDR allowlist for `/admin` |
| `STASHLOCK_TRUSTED_PROXIES` | *(empty)* | CIDRs of reverse proxies whose `X-Forwarded-For` is trusted |
| `STASHLOCK_ALLOWED_ORIGINS` | *(empty = CORS off)* | Comma-separated CORS origins |
| `STASHLOCK_LOG_DIR` | `./logs` | Log directory; set to `/app/log` to match the volume |
| `STASHLOCK_LOG_LEVEL` | `info` | `debug`, `info`, `warn`, `error` |
| `STASHLOCK_DEFAULT_RATE_LIMIT` | `60` | Requests per minute for API keys without their own limit |
| `STASHLOCK_MAX_REQUEST_BODY_KB` | `110` | Largest accepted request body |
| `STASHLOCK_DELETED_RETENTION_DAYS` | `30` | Days deleted entries stay restorable (`0` = forever) |
| `STASHLOCK_MAX_VALUE_SIZE_KB` | `100` | Maximum stored value size |
| `STASHLOCK_PORT` | `8080` | Listen port inside the container |
| `STASHLOCK_WORK_DIR` | `/app/work` (image) | Data directory |

## After the first start

1. Open `http(s)://<host>/admin`, log in with the key from `.env` and save the admin key shown (`slk_adm_...`).
2. Optionally remove `STASHLOCK_MASTER_KEY` from `.env` — it is blocked anyway. The server starts as long as an active admin key exists.
3. In the dashboard (**API Keys**) create a key per `box.tag`: Read + Write for CI (`stashlock publish`), Read only for applications. They use it as `STASHLOCK_API_KEY`.

**Lost every admin key?** Set a new `STASHLOCK_MASTER_KEY` and restart; a never-used master key works once again.

## Upgrades and shutdown

The database schema is versioned: a new image migrates the database on start (no manual steps, no data loss). Databases from 0.4 are not supported; 0.5.0 starts with an empty database.

`docker stop` lets in-flight requests finish (up to 15 s) before the server exits.

## Backup

The database runs in WAL mode (`stashlock.sqlite`, `-wal`, `-shm`). Either stop the container and copy `./app/work/db/`, or run:

```bash
sqlite3 ./app/work/db/stashlock.sqlite ".backup backup-$(date +%F).sqlite"
```

Stored values are ciphertext (encrypted client-side by `stashlock publish`); API and admin keys are stored as SHA-256 hashes.
