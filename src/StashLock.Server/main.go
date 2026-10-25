package main

import (
	"context"
	"embed"
	"errors"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"github.com/deneblab/stashlock-servergo/config"
	"github.com/deneblab/stashlock-servergo/handlers"
	"github.com/deneblab/stashlock-servergo/logging"
	mw "github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/services"
	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
	"github.com/rs/cors"
	_ "modernc.org/sqlite"

	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"
)

//go:embed templates
var templatesFS embed.FS

//go:embed static
var staticFS embed.FS

func main() {
	// "stashlock-servergo healthcheck": used by the Docker HEALTHCHECK (the image has no curl)
	if len(os.Args) > 1 && os.Args[1] == "healthcheck" {
		os.Exit(healthcheck())
	}

	// Set embedded filesystems for handlers
	handlers.TemplateFS = templatesFS
	handlers.StaticEmbedFS = staticFS

	// Load configuration
	cfg := config.Load()

	// Setup logging (console + plain text file with rotation)
	logging.Setup(cfg.LogDir, cfg.LogLevel)

	if err := cfg.Validate(); err != nil {
		log.Fatal().Err(err).Msg("Invalid configuration")
	}

	// SIGINT/SIGTERM (e.g. docker stop) cancel ctx and trigger a graceful shutdown
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	ln, err := net.Listen("tcp", fmt.Sprintf(":%d", cfg.Port))
	if err != nil {
		log.Fatal().Err(err).Msg("Failed to listen")
	}
	if err := run(ctx, cfg, ln); err != nil {
		log.Fatal().Err(err).Msg("Server failed")
	}
	log.Info().Msg("Server stopped")
}

// healthcheck calls /healthz on the local server and returns the process exit code.
func healthcheck() int {
	port := os.Getenv("STASHLOCK_PORT")
	if port == "" {
		port = "8080"
	}
	client := &http.Client{Timeout: 3 * time.Second}
	resp, err := client.Get("http://127.0.0.1:" + port + "/healthz")
	if err != nil {
		fmt.Fprintln(os.Stderr, "unhealthy:", err)
		return 1
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		fmt.Fprintln(os.Stderr, "unhealthy: status", resp.StatusCode)
		return 1
	}
	return 0
}

// Server timeouts protect against slow or stalled clients.
const (
	readHeaderTimeout = 10 * time.Second
	readTimeout       = 30 * time.Second
	writeTimeout      = 30 * time.Second
	idleTimeout       = 120 * time.Second
	shutdownTimeout   = 15 * time.Second
)

// run serves on ln until ctx is cancelled, then drains in-flight requests and stops background work.
func run(ctx context.Context, cfg *config.Config, ln net.Listener) error {
	// Ensure work directory exists
	dbDir := filepath.Join(cfg.WorkDir, "db")
	if err := os.MkdirAll(dbDir, 0750); err != nil {
		return fmt.Errorf("create work directory: %w", err)
	}

	// Open SQLite database
	dbPath := filepath.Join(dbDir, "stashlock.sqlite")
	db, err := sqlx.Open("sqlite", dbPath+"?_journal_mode=WAL&_busy_timeout=5000")
	if err != nil {
		return fmt.Errorf("open database: %w", err)
	}
	defer db.Close()

	if err := store.RunMigrations(db); err != nil {
		return fmt.Errorf("migrations: %w", err)
	}

	// Create stores
	storage := store.NewStorageService(db, cfg.MaxValueSizeBytes())
	apiKeyStore := store.NewApiKeyStore(db)
	auditStore := store.NewAuditStore(db)

	// Fail closed: someone must be able to administer the server
	adminOk, err := apiKeyStore.AdminAccessAvailable(cfg.MasterKey)
	if err != nil {
		return fmt.Errorf("check admin keys: %w", err)
	}
	if !adminOk {
		return errors.New("no way to administer the server: no active admin key, and STASHLOCK_MASTER_KEY is empty or already used. Set a new STASHLOCK_MASTER_KEY (at least 32 characters) and restart")
	}
	masterKeyUnused := false
	if cfg.MasterKey != "" {
		consumed, err := apiKeyStore.IsMasterKeyConsumed(cfg.MasterKey)
		if err != nil {
			return fmt.Errorf("check master key: %w", err)
		}
		masterKeyUnused = !consumed
	}

	// Background cleanup stops with ctx
	cleanupCtx, cancelCleanup := context.WithCancel(context.Background())
	cleanupDone := services.StartExpiredKeyCleanup(cleanupCtx, db, storage, cfg.DeletedRetentionDays)
	defer func() { cancelCleanup(); <-cleanupDone }()

	srv := &http.Server{
		Handler:           newRouter(cfg, db, storage, apiKeyStore, auditStore),
		ReadHeaderTimeout: readHeaderTimeout,
		ReadTimeout:       readTimeout,
		WriteTimeout:      writeTimeout,
		IdleTimeout:       idleTimeout,
	}

	log.Info().
		Str("version", cfg.Version).
		Str("addr", ln.Addr().String()).
		Str("workDir", cfg.WorkDir).
		Str("logDir", cfg.LogDir).
		Str("logLevel", cfg.LogLevel).
		Bool("masterKeyUnused", masterKeyUnused).
		Int("deletedRetentionDays", cfg.DeletedRetentionDays).
		Strs("adminNetworks", cfg.AdminNetworks()).
		Strs("trustedProxies", cfg.TrustedProxyNetworks()).
		Strs("allowedOrigins", cfg.AllowedOriginsList()).
		Msg("StashLock.ServerGo starting")

	serveErr := make(chan error, 1)
	go func() { serveErr <- srv.Serve(ln) }()

	select {
	case err := <-serveErr:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	case <-ctx.Done():
	}

	log.Info().Dur("timeout", shutdownTimeout).Msg("Shutting down: draining in-flight requests")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), shutdownTimeout)
	defer cancel()
	if err := srv.Shutdown(shutdownCtx); err != nil {
		return fmt.Errorf("graceful shutdown: %w", err)
	}
	return nil
}

// newRouter wires middleware and routes. Order matters: Recovery first so it catches
// panics anywhere below; the auth guard sits before auth so it sees every 401.
func newRouter(cfg *config.Config, db *sqlx.DB, storage *store.StorageService, apiKeyStore *store.ApiKeyStore, auditStore *store.AuditStore) http.Handler {
	boxesHandler := handlers.NewBoxesHandler(storage, auditStore, cfg.Version, cfg.MaxKeyLength)
	healthHandler := handlers.NewHealthHandler(db)
	keysHandler := handlers.NewKeysHandler(apiKeyStore, auditStore)
	auditHandler := handlers.NewAuditHandler(auditStore)
	statsHandler := handlers.NewStatsHandler(storage, cfg.Version)
	authGuard := mw.NewAuthGuard(cfg.TrustedProxyNetworks())

	r := chi.NewRouter()
	r.Use(mw.Recovery)
	r.Use(mw.Correlation)
	r.Use(mw.RequestLog)
	r.Use(mw.SecurityHeaders)
	r.Use(mw.BodyLimit(int64(cfg.MaxRequestBodySizeBytes())))

	// CORS — opt-in only; without STASHLOCK_ALLOWED_ORIGINS browsers get no cross-origin access
	if origins := cfg.AllowedOriginsList(); len(origins) > 0 {
		corsHandler := cors.New(cors.Options{
			AllowedOrigins: origins,
			AllowedMethods: []string{"GET", "POST", "PUT", "DELETE", "OPTIONS"},
			AllowedHeaders: []string{"Authorization", "Content-Type", "X-Correlation-Id"},
		})
		r.Use(corsHandler.Handler)
	}

	r.Use(mw.NetworkAccess(cfg.AdminNetworks(), cfg.TrustedProxyNetworks()))
	r.Use(mw.SameOriginAdminPosts)
	r.Use(authGuard.Middleware)
	r.Use(mw.ApiKeyAuth(apiKeyStore))
	r.Use(mw.RateLimit(cfg.DefaultRateLimitPerMin))

	// Routes
	r.Get("/", boxesHandler.GetRoot)
	r.Get("/healthz", healthHandler.Healthz)

	r.Route("/boxes", func(r chi.Router) {
		r.Get("/", boxesHandler.ListBoxes)
		r.Post("/{keyTextUrl}", boxesHandler.SetBox)
		r.Get("/{keyTextUrl}", boxesHandler.GetBox)
		r.Delete("/{keyTextUrl}", boxesHandler.DeleteBox)
		r.Get("/{keyTextUrl}/history", boxesHandler.GetHistory)
		r.Get("/{keyTextUrl}/history/{versionNumber}", boxesHandler.GetHistoryVersion)
		r.Get("/{keyTextUrl}/meta", boxesHandler.GetMetadata)
	})

	r.Route("/keys", func(r chi.Router) {
		r.Post("/", keysHandler.CreateKey)
		r.Get("/", keysHandler.ListKeys)
		r.Get("/{id}", keysHandler.GetKey)
		r.Delete("/{id}", keysHandler.RevokeKey)
		r.Delete("/{id}/permanent", keysHandler.DeleteKey)
	})

	r.Get("/audit", auditHandler.GetAuditLogs)
	r.Get("/stats", statsHandler.GetStats)

	// Static files (embedded)
	r.Handle("/static/*", http.StripPrefix("/static/", http.FileServer(handlers.StaticFS())))

	// Admin dashboard
	dashboard := handlers.NewDashboardHandler(storage, apiKeyStore, auditStore, cfg.MasterKey, cfg.Version, cfg.DefaultRateLimitPerMin)
	dashboard.AuthGuard = authGuard
	dashboard.RetentionDays = cfg.DeletedRetentionDays
	r.Route("/admin", dashboard.Routes)

	return r
}
