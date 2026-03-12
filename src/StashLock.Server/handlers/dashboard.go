package handlers

import (
	"crypto/rand"
	"embed"
	"encoding/hex"
	"fmt"
	"html/template"
	"io/fs"
	"math"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
	"github.com/jmoiron/sqlx"
	"github.com/rs/zerolog/log"
)

// TemplateFS and StaticEmbedFS are set by main during init (go:embed can't use .. paths).
var TemplateFS embed.FS
var StaticEmbedFS embed.FS

// StaticFS returns the embedded static file system (css, js, etc.) rooted at "static/".
func StaticFS() http.FileSystem {
	sub, err := fs.Sub(StaticEmbedFS, "static")
	if err != nil {
		panic(err)
	}
	return http.FS(sub)
}

// Template functions shared by all templates.
var funcMap = template.FuncMap{
	"formatBytes": func(b int64) string {
		if b == 0 {
			return "0 B"
		}
		units := []string{"B", "KB", "MB", "GB"}
		i := int(math.Floor(math.Log(float64(b)) / math.Log(1024)))
		if i >= len(units) {
			i = len(units) - 1
		}
		return fmt.Sprintf("%.1f %s", float64(b)/math.Pow(1024, float64(i)), units[i])
	},
	"formatBytesInt": func(b int) string {
		if b == 0 {
			return "0 B"
		}
		units := []string{"B", "KB", "MB", "GB"}
		i := int(math.Floor(math.Log(float64(b)) / math.Log(1024)))
		if i >= len(units) {
			i = len(units) - 1
		}
		return fmt.Sprintf("%.1f %s", float64(b)/math.Pow(1024, float64(i)), units[i])
	},
	"formatDate": func(iso string) string {
		if iso == "" {
			return "-"
		}
		t, err := time.Parse(time.RFC3339Nano, iso)
		if err != nil {
			t, err = time.Parse(time.RFC3339, iso)
			if err != nil {
				return iso
			}
		}
		return t.Format("2006-01-02 15:04:05")
	},
	"escapeJs": func(s string) string {
		s = strings.ReplaceAll(s, `\`, `\\`)
		s = strings.ReplaceAll(s, `'`, `\'`)
		return s
	},
	"derefInt": func(p *int) int {
		if p == nil {
			return 0
		}
		return *p
	},
}

// DashboardHandler serves the admin dashboard pages.
type DashboardHandler struct {
	db          *sqlx.DB
	storage     *store.StorageService
	apiKeyStore *store.ApiKeyStore
	auditStore  *store.AuditStore
	masterKey   string
	version     string

	sessions sync.Map // token -> true

	layoutTmpl      *template.Template
	loginTmpl       *template.Template
	indexTmpl       *template.Template
	boxesTmpl       *template.Template
	keysTmpl        *template.Template
	auditTmpl       *template.Template
	statCardsTmpl   *template.Template
	boxListTmpl     *template.Template
	boxDetailTmpl   *template.Template
	keyListTmpl     *template.Template
	auditListTmpl   *template.Template
}

type pageData struct {
	IsLoggedIn   bool
	Version      string
	ActiveTab    string
	ErrorMessage string
}

func NewDashboardHandler(db *sqlx.DB, storage *store.StorageService, apiKeyStore *store.ApiKeyStore, auditStore *store.AuditStore, masterKey, version string) *DashboardHandler {
	h := &DashboardHandler{
		db:          db,
		storage:     storage,
		apiKeyStore: apiKeyStore,
		auditStore:  auditStore,
		masterKey:   masterKey,
		version:     version,
	}

	// Parse layout + each page
	layoutHTML := mustRead("templates/layout.html")

	h.loginTmpl = template.Must(template.New("page").Funcs(funcMap).Parse(layoutHTML + mustRead("templates/login.html")))
	h.indexTmpl = template.Must(template.New("page").Funcs(funcMap).Parse(layoutHTML + mustRead("templates/index.html")))
	h.boxesTmpl = template.Must(template.New("page").Funcs(funcMap).Parse(layoutHTML + mustRead("templates/boxes.html")))
	h.keysTmpl = template.Must(template.New("page").Funcs(funcMap).Parse(layoutHTML + mustRead("templates/keys.html")))
	h.auditTmpl = template.Must(template.New("page").Funcs(funcMap).Parse(layoutHTML + mustRead("templates/audit.html")))

	// Partials (standalone, no layout)
	h.statCardsTmpl = template.Must(template.New("partial").Funcs(funcMap).Parse(mustRead("templates/partials/stat_cards.html")))
	h.boxListTmpl = template.Must(template.New("partial").Funcs(funcMap).Parse(mustRead("templates/partials/box_list.html")))
	h.boxDetailTmpl = template.Must(template.New("partial").Funcs(funcMap).Parse(mustRead("templates/partials/box_detail.html")))
	h.keyListTmpl = template.Must(template.New("partial").Funcs(funcMap).Parse(mustRead("templates/partials/key_list.html")))
	h.auditListTmpl = template.Must(template.New("partial").Funcs(funcMap).Parse(mustRead("templates/partials/audit_list.html")))

	return h
}

func mustRead(path string) string {
	data, err := TemplateFS.ReadFile(path)
	if err != nil {
		panic(fmt.Sprintf("failed to read embedded template %s: %v", path, err))
	}
	return string(data)
}

// Session helpers using crypto-random tokens.
const sessionCookieName = "StashLock.Session"

func generateSessionToken() string {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		panic("crypto/rand failed: " + err.Error())
	}
	return hex.EncodeToString(b)
}

func (h *DashboardHandler) setSession(w http.ResponseWriter) string {
	token := generateSessionToken()
	h.sessions.Store(token, true)
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookieName,
		Value:    token,
		Path:     "/",
		HttpOnly: true,
		SameSite: http.SameSiteStrictMode,
		MaxAge:   8 * 60 * 60, // 8 hours
	})
	return token
}

func (h *DashboardHandler) clearSession(w http.ResponseWriter, r *http.Request) {
	if c, err := r.Cookie(sessionCookieName); err == nil {
		h.sessions.Delete(c.Value)
	}
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookieName,
		Value:    "",
		Path:     "/",
		HttpOnly: true,
		MaxAge:   -1,
	})
}

func (h *DashboardHandler) isAuthenticated(r *http.Request) bool {
	c, err := r.Cookie(sessionCookieName)
	if err != nil || c.Value == "" {
		return false
	}
	_, ok := h.sessions.Load(c.Value)
	return ok
}

func (h *DashboardHandler) ensureAuth(w http.ResponseWriter, r *http.Request) bool {
	if !h.isAuthenticated(r) {
		http.Redirect(w, r, "/admin/login", http.StatusFound)
		return false
	}
	return true
}

func (h *DashboardHandler) renderPage(w http.ResponseWriter, tmpl *template.Template, data interface{}) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	if err := tmpl.ExecuteTemplate(w, "layout", data); err != nil {
		log.Error().Err(err).Msg("Template render error")
		http.Error(w, "Internal Server Error", 500)
	}
}

func (h *DashboardHandler) renderPartial(w http.ResponseWriter, tmpl *template.Template, data interface{}) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	if err := tmpl.Execute(w, data); err != nil {
		log.Error().Err(err).Msg("Partial template render error")
		http.Error(w, "Internal Server Error", 500)
	}
}

// Routes registers all admin dashboard routes on the given router.
func (h *DashboardHandler) Routes(r chi.Router) {
	r.Get("/login", h.LoginPage)
	r.Post("/login", h.LoginSubmit)
	r.Post("/logout", h.Logout)
	r.Get("/", h.IndexPage)
	r.Get("/index", h.IndexHandler)
	r.Get("/boxes", h.BoxesHandler)
	r.Post("/boxes", h.BoxesPostHandler)
	r.Get("/keys", h.KeysHandler)
	r.Post("/keys", h.KeysPostHandler)
	r.Get("/audit", h.AuditHandler)
}

// --- Login ---

func (h *DashboardHandler) LoginPage(w http.ResponseWriter, r *http.Request) {
	if h.isAuthenticated(r) {
		http.Redirect(w, r, "/admin", http.StatusFound)
		return
	}
	h.renderPage(w, h.loginTmpl, pageData{IsLoggedIn: false})
}

func (h *DashboardHandler) LoginSubmit(w http.ResponseWriter, r *http.Request) {
	apiKey := r.FormValue("apiKey")

	if apiKey == "" {
		h.renderPage(w, h.loginTmpl, pageData{IsLoggedIn: false, ErrorMessage: "Please enter an API key"})
		return
	}

	// No master key = open access
	if h.masterKey == "" {
		h.setSession(w)
		http.Redirect(w, r, "/admin", http.StatusFound)
		return
	}

	// Check master key
	if apiKey == h.masterKey {
		h.setSession(w)
		http.Redirect(w, r, "/admin", http.StatusFound)
		return
	}

	// Check DB keys
	identity, err := h.apiKeyStore.Validate(apiKey)
	if err == nil && identity != nil && identity.IsMaster {
		h.setSession(w)
		http.Redirect(w, r, "/admin", http.StatusFound)
		return
	}

	h.renderPage(w, h.loginTmpl, pageData{IsLoggedIn: false, ErrorMessage: "Invalid API key. The dashboard requires the master API key."})
}

func (h *DashboardHandler) Logout(w http.ResponseWriter, r *http.Request) {
	h.clearSession(w, r)
	http.Redirect(w, r, "/admin/login", http.StatusFound)
}

// --- Index (Overview) ---

func (h *DashboardHandler) IndexPage(w http.ResponseWriter, r *http.Request) {
	if !h.ensureAuth(w, r) {
		return
	}
	h.renderPage(w, h.indexTmpl, pageData{IsLoggedIn: true, Version: "v" + h.version, ActiveTab: "overview"})
}

func (h *DashboardHandler) IndexHandler(w http.ResponseWriter, r *http.Request) {
	handler := r.URL.Query().Get("handler")
	if handler == "Stats" {
		if !h.ensureAuth(w, r) {
			return
		}
		h.handleStats(w)
		return
	}
	// Default: serve index page
	h.IndexPage(w, r)
}

func (h *DashboardHandler) handleStats(w http.ResponseWriter) {
	now := time.Now().UTC()

	type keyRow struct {
		Value     string  `db:"Value"`
		IsDeleted bool    `db:"IsDeleted"`
		ExpiresAt *string `db:"ExpiresAt"`
	}

	var allKeys []keyRow
	_ = h.db.Select(&allKeys, "SELECT Value, IsDeleted, ExpiresAt FROM KeyValues")

	var totalKeys, activeKeys, deletedKeys, expiredKeys int
	var totalSizeBytes int64
	totalKeys = len(allKeys)

	for _, k := range allKeys {
		if k.IsDeleted {
			deletedKeys++
			continue
		}
		if isExpiredPtr(k.ExpiresAt, now) {
			expiredKeys++
			continue
		}
		activeKeys++
		totalSizeBytes += int64(len(k.Value))
	}

	var totalApiKeys, activeApiKeys int
	_ = h.db.Get(&totalApiKeys, "SELECT COUNT(*) FROM ApiKeys")
	_ = h.db.Get(&activeApiKeys, "SELECT COUNT(*) FROM ApiKeys WHERE IsActive = 1")

	var totalAuditEvents int64
	_ = h.db.Get(&totalAuditEvents, "SELECT COUNT(*) FROM AuditLogs")

	h.renderPartial(w, h.statCardsTmpl, models.StatsResponse{
		TotalKeys:        totalKeys,
		ActiveKeys:       activeKeys,
		DeletedKeys:      deletedKeys,
		ExpiredKeys:      expiredKeys,
		TotalSizeBytes:   totalSizeBytes,
		TotalApiKeys:     totalApiKeys,
		ActiveApiKeys:    activeApiKeys,
		TotalAuditEvents: totalAuditEvents,
		ServerVersion:    h.version,
	})
}

// --- Boxes ---

func (h *DashboardHandler) BoxesHandler(w http.ResponseWriter, r *http.Request) {
	if !h.ensureAuth(w, r) {
		return
	}
	handler := r.URL.Query().Get("handler")

	switch handler {
	case "Search":
		prefix := r.URL.Query().Get("prefix")
		var prefixPtr *string
		if prefix != "" {
			prefixPtr = &prefix
		}
		keys, err := h.storage.ListKeys(prefixPtr, 1000, 0)
		if err != nil {
			http.Error(w, err.Error(), 500)
			return
		}
		h.renderPartial(w, h.boxListTmpl, keys)

	case "Detail":
		key := r.URL.Query().Get("key")
		ks, err := models.NewKeyStore(key)
		if err != nil {
			http.Error(w, err.Error(), 400)
			return
		}
		meta, _ := h.storage.GetMetadata(ks)
		history, _ := h.storage.GetHistory(ks)
		h.renderPartial(w, h.boxDetailTmpl, struct {
			Key      string
			Metadata *models.KeyMetadata
			History  []models.HistoryEntry
		}{Key: key, Metadata: meta, History: history})

	case "Value":
		key := r.URL.Query().Get("key")
		ks, err := models.NewKeyStore(key)
		if err != nil {
			http.Error(w, err.Error(), 400)
			return
		}
		value, _ := h.storage.Get(ks)
		if value == "" {
			value = "(empty)"
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		fmt.Fprintf(w, `<div class="detail-value-box">%s</div>`, template.HTMLEscapeString(value))

	default:
		h.renderPage(w, h.boxesTmpl, pageData{IsLoggedIn: true, Version: "v" + h.version, ActiveTab: "boxes"})
	}
}

func (h *DashboardHandler) BoxesPostHandler(w http.ResponseWriter, r *http.Request) {
	if !h.ensureAuth(w, r) {
		return
	}
	handler := r.URL.Query().Get("handler")

	switch handler {
	case "Create":
		box := r.FormValue("box")
		tag := r.FormValue("tag")
		version := r.FormValue("version")
		value := r.FormValue("value")
		ttlStr := r.FormValue("ttl")

		keyText := fmt.Sprintf("%s.%s.%s", box, tag, version)
		ks, err := models.NewKeyStore(keyText)
		if err != nil {
			http.Error(w, err.Error(), 400)
			return
		}

		var expiresAt *string
		if ttlStr != "" {
			ttl, err := strconv.Atoi(ttlStr)
			if err == nil && ttl > 0 {
				exp := time.Now().UTC().Add(time.Duration(ttl) * time.Second).Format(time.RFC3339Nano)
				expiresAt = &exp
			}
		}

		if err := h.storage.Set(ks, value, expiresAt); err != nil {
			http.Error(w, err.Error(), 500)
			return
		}

		keys, _ := h.storage.ListKeys(nil, 1000, 0)
		h.renderPartial(w, h.boxListTmpl, keys)

	case "Delete":
		key := r.URL.Query().Get("key")
		ks, err := models.NewKeyStore(key)
		if err != nil {
			http.Error(w, err.Error(), 400)
			return
		}
		_ = h.storage.Delete(ks)
		keys, _ := h.storage.ListKeys(nil, 1000, 0)
		h.renderPartial(w, h.boxListTmpl, keys)

	case "Restore":
		key := r.URL.Query().Get("key")
		verStr := r.URL.Query().Get("ver")
		ver, _ := strconv.Atoi(verStr)

		ks, err := models.NewKeyStore(key)
		if err != nil {
			http.Error(w, err.Error(), 400)
			return
		}
		oldValue, found, _ := h.storage.GetVersion(ks, ver)
		if found {
			_ = h.storage.Set(ks, oldValue, nil)
		}
		keys, _ := h.storage.ListKeys(nil, 1000, 0)
		h.renderPartial(w, h.boxListTmpl, keys)
	}
}

// --- Keys ---

func (h *DashboardHandler) KeysHandler(w http.ResponseWriter, r *http.Request) {
	if !h.ensureAuth(w, r) {
		return
	}
	handler := r.URL.Query().Get("handler")

	switch handler {
	case "List":
		keys, err := h.apiKeyStore.List()
		if err != nil {
			http.Error(w, err.Error(), 500)
			return
		}
		h.renderPartial(w, h.keyListTmpl, keys)
	default:
		h.renderPage(w, h.keysTmpl, pageData{IsLoggedIn: true, Version: "v" + h.version, ActiveTab: "keys"})
	}
}

func (h *DashboardHandler) KeysPostHandler(w http.ResponseWriter, r *http.Request) {
	if !h.ensureAuth(w, r) {
		return
	}
	handler := r.URL.Query().Get("handler")

	switch handler {
	case "Create":
		name := r.FormValue("name")
		scopesStr := r.FormValue("scopes")
		canRead := r.FormValue("canRead") == "true"
		canWrite := r.FormValue("canWrite") == "true"
		canDelete := r.FormValue("canDelete") == "true"
		expiresAt := r.FormValue("expiresAt")
		rateLimitStr := r.FormValue("rateLimitPerMinute")

		scopes := []string{"*"}
		if scopesStr != "" {
			scopes = nil
			for _, s := range strings.Split(scopesStr, ",") {
				s = strings.TrimSpace(s)
				if s != "" {
					scopes = append(scopes, s)
				}
			}
			if len(scopes) == 0 {
				scopes = []string{"*"}
			}
		}

		req := store.CreateApiKeyRequest{
			Name:    name,
			Scopes:  scopes,
			CanRead: &canRead,
			CanWrite: &canWrite,
			CanDelete: &canDelete,
		}
		if expiresAt != "" {
			req.ExpiresAt = &expiresAt
		}
		if rateLimitStr != "" {
			if rl, err := strconv.Atoi(rateLimitStr); err == nil {
				req.RateLimitPerMinute = &rl
			}
		}

		resp, err := h.apiKeyStore.Create(req)
		if err != nil {
			http.Error(w, err.Error(), 500)
			return
		}

		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, `{"key":"%s","info":{"id":"%s","name":"%s"}}`, resp.Key, resp.Info.Id, resp.Info.Name)

	case "Revoke":
		id := r.URL.Query().Get("id")
		_ = h.apiKeyStore.Revoke(id)
		keys, _ := h.apiKeyStore.List()
		h.renderPartial(w, h.keyListTmpl, keys)

	case "DeleteKey":
		id := r.URL.Query().Get("id")
		_ = h.apiKeyStore.Delete(id)
		keys, _ := h.apiKeyStore.List()
		h.renderPartial(w, h.keyListTmpl, keys)
	}
}

// --- Audit ---

func (h *DashboardHandler) AuditHandler(w http.ResponseWriter, r *http.Request) {
	if !h.ensureAuth(w, r) {
		return
	}
	handler := r.URL.Query().Get("handler")

	switch handler {
	case "Query":
		boxKey := r.URL.Query().Get("boxKey")
		action := r.URL.Query().Get("action")
		limit := 100
		if l := r.URL.Query().Get("limit"); l != "" {
			if n, err := strconv.Atoi(l); err == nil && n > 0 {
				limit = n
			}
		}
		entries, err := h.auditStore.Query(boxKey, action, limit)
		if err != nil {
			http.Error(w, err.Error(), 500)
			return
		}
		h.renderPartial(w, h.auditListTmpl, entries)
	default:
		h.renderPage(w, h.auditTmpl, pageData{IsLoggedIn: true, Version: "v" + h.version, ActiveTab: "audit"})
	}
}

// Helper
func isExpiredPtr(expiresAt *string, now time.Time) bool {
	if expiresAt == nil || *expiresAt == "" {
		return false
	}
	t, err := time.Parse(time.RFC3339Nano, *expiresAt)
	if err != nil {
		t, err = time.Parse(time.RFC3339, *expiresAt)
		if err != nil {
			return false
		}
	}
	return t.Before(now)
}
