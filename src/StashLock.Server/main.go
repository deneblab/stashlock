package main

import (
	"context"
	"embed"
	"fmt"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"

	"github.com/deneblab/stashlock-servergo/config"
	"github.com/deneblab/stashlock-servergo/handlers"
	"github.com/deneblab/stashlock-servergo/logging"
	mw "github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/services"
	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
	_ "modernc.org/sqlite"
	"github.com/rs/cors"

	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"
)

//go:embed templates
var templatesFS embed.FS

//go:embed static
var staticFS embed.FS

func main() {
	// Set embedded filesystems for handlers
	handlers.TemplateFS = templatesFS
	handlers.StaticEmbedFS = staticFS

	// Load configuration
	cfg := config.Load()

	// Setup logging (console + plain text file with rotation)
	logging.Setup(cfg.LogDir, cfg.LogLevel)

	// Ensure work directory exists
	dbDir := filepath.Join(cfg.WorkDir, "db")
	if err := os.MkdirAll(dbDir, 0755); err != nil {
		log.Fatal().Err(err).Msg("Failed to create work directory")
	}

	// Open SQLite database
	dbPath := filepath.Join(dbDir, "stashlock.sqlite")
	db, err := sqlx.Open("sqlite", dbPath+"?_journal_mode=WAL&_busy_timeout=5000")
	if err != nil {
		log.Fatal().Err(err).Msg("Failed to open database")
	}
	defer db.Close()

	// Run migrations
	if err := store.RunMigrations(db); err != nil {
		log.Fatal().Err(err).Msg("Failed to run migrations")
	}

	// Create stores
	storage := store.NewStorageService(db, cfg.MaxValueSizeBytes())
	apiKeyStore := store.NewApiKeyStore(db)
	auditStore := store.NewAuditStore(db)

	// Create handlers
	boxesHandler := handlers.NewBoxesHandler(storage, auditStore, cfg.Version)
	healthHandler := handlers.NewHealthHandler(db)
	keysHandler := handlers.NewKeysHandler(apiKeyStore)
	decodeHandler := handlers.NewDecodeHandler(storage)
	auditHandler := handlers.NewAuditHandler(auditStore)
	statsHandler := handlers.NewStatsHandler(db, cfg.Version)

	// Start background cleanup service
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	services.StartExpiredKeyCleanup(ctx, db)

	// Handle graceful shutdown
	go func() {
		sigCh := make(chan os.Signal, 1)
		signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
		<-sigCh
		log.Info().Msg("Shutting down...")
		cancel()
	}()

	// Setup router
	r := chi.NewRouter()

	// Middleware chain (order matches .NET server)
	r.Use(mw.Correlation)
	r.Use(mw.RequestLog)
	r.Use(mw.SecurityHeaders)

	// CORS
	corsHandler := cors.New(cors.Options{
		AllowedOrigins: cfg.AllowedOriginsList(),
		AllowedMethods: []string{"GET", "POST", "PUT", "DELETE", "OPTIONS"},
		AllowedHeaders: []string{"*"},
	})
	r.Use(corsHandler.Handler)

	// Network access (CIDR whitelist for /admin)
	r.Use(mw.NetworkAccess(cfg.AdminNetworks()))

	// API key auth
	r.Use(mw.ApiKeyAuth(cfg.ApiKey, apiKeyStore))

	// Rate limiting
	r.Use(mw.RateLimit(cfg.DefaultRateLimitPerMin))

	// Panic recovery / error handler (outermost catch)
	r.Use(mw.Recovery)

	// Routes
	r.Get("/", boxesHandler.GetRoot)
	r.Get("/healthz", healthHandler.Healthz)

	r.Route("/boxes", func(r chi.Router) {
		r.Get("/", boxesHandler.ListBoxes)
		r.Post("/decode", decodeHandler.DecodeBox)
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
	dashboard := handlers.NewDashboardHandler(db, storage, apiKeyStore, auditStore, cfg.ApiKey, cfg.Version)
	r.Route("/admin", dashboard.Routes)

	// Start server
	addr := fmt.Sprintf(":%d", cfg.Port)
	hasApiKey := cfg.ApiKey != ""
	log.Info().
		Str("version", cfg.Version).
		Str("addr", addr).
		Str("workDir", cfg.WorkDir).
		Str("logDir", cfg.LogDir).
		Str("logLevel", cfg.LogLevel).
		Bool("apiKeySet", hasApiKey).
		Msg("StashLock.ServerGo starting")

	if err := http.ListenAndServe(addr, r); err != nil {
		log.Fatal().Err(err).Msg("Server failed")
	}
}
