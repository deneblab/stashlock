package handlers

import (
	"crypto/rand"
	"embed"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"html/template"
	"io/fs"
	"math"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
	"github.com/rs/zerolog/log"
)

// TemplateFS and StaticEmbedFS are set by main during init (go:embed can't use .. paths).
// TemplateFS is an fs.FS so tests can load the templates from disk.
var TemplateFS fs.FS
var StaticEmbedFS embed.FS

// StaticFS returns the embedded static file system (css, js, etc.) rooted at "static/".
func StaticFS() http.FileSystem {
	sub, err := fs.Sub(StaticEmbedFS, "static")
	if err != nil {
		panic(err)
	}
	return http.FS(sub)
}

func formatBytes(b int64) string {
	if b <= 0 {
		return "0 B"
	}
	units := []string{"B", "KB", "MB", "GB"}
	i := int(math.Floor(math.Log(float64(b)) / math.Log(1024)))
	if i >= len(units) {
		i = len(units) - 1
	}
	if i == 0 {
		return fmt.Sprintf("%d B", b)
	}
	return fmt.Sprintf("%.1f %s", float64(b)/math.Pow(1024, float64(i)), units[i])
}

// Template functions shared by all templates.
var funcMap = template.FuncMap{
	"formatBytes":    formatBytes,
	"formatBytesInt": func(b int) string { return formatBytes(int64(b)) },
	"add":            func(a, b int) int { return a + b },
	// purgeDate is when a soft-deleted entry is removed for good (empty when kept forever).
	"purgeDate": func(deletedAt string, days int) string {
		t, err := time.Parse(time.RFC3339Nano, deletedAt)
		if err != nil || days <= 0 {
			return ""
		}
		return t.AddDate(0, 0, days).UTC().Format(time.RFC3339)
	},
	"derefStr": func(p *string) string {
		if p == nil {
			return ""
		}
		return *p
	},
	"outcomeClass": func(outcome string) string {
		switch outcome {
		case "ok":
			return "ok"
		case "error", "failed", "denied":
			return "bad"
		default:
			return "neutral"
		}
	},
	"keyStatus": func(k store.ApiKeyInfo) template.HTML {
		switch {
		case !k.IsActive:
			return `<span class="pill neutral">Revoked</span>`
		case isExpiredPtr(k.ExpiresAt, time.Now().UTC()):
			return `<span class="pill bad">Expired</span>`
		case k.ExpiresAt != nil && !isExpiredPtr(k.ExpiresAt, time.Now().UTC().Add(7*24*time.Hour)):
			return `<span class="pill ok">Active</span>`
		case k.ExpiresAt != nil:
			return `<span class="pill warn">Expires soon</span>`
		default:
			return `<span class="pill ok">Active</span>`
		}
	},
	"accessPills": func(k store.ApiKeyInfo) template.HTML {
		var b strings.Builder
		b.WriteString(`<span class="access">`)
		for _, p := range []struct {
			on   bool
			name string
		}{{k.CanRead, "Read"}, {k.CanWrite, "Write"}, {k.CanDelete, "Delete"}} {
			if p.on {
				b.WriteString(`<span class="pill neutral">` + p.name + `</span>`)
			}
		}
		b.WriteString(`</span>`)
		return template.HTML(b.String())
	},
}

// DashboardHandler serves the admin dashboard pages.
type DashboardHandler struct {
	storage          *store.StorageService
	apiKeyStore      *store.ApiKeyStore
	auditStore       *store.AuditStore
	masterKey        string // one-time bootstrap key (STASHLOCK_MASTER_KEY)
	version          string
	defaultRateLimit int

	// AuthGuard counts failed sign-ins toward the per-IP block (optional).
	AuthGuard *middleware.AuthGuard
	// RetentionDays is how long deleted entries stay restorable (0 = forever).
	RetentionDays int

	sessions sync.Map // token -> session

	pages    map[string]*template.Template
	partials map[string]*template.Template
}

type session struct {
	adminKeyId string
	expiresAt  time.Time
}

const sessionTTL = 8 * time.Hour

// adminInfo is the signed-in admin shown in the top bar.
type adminInfo struct {
	Id        string
	Name      string
	KeyPrefix string
}

type pageData struct {
	IsLoggedIn   bool
	Version      string
	ActiveTab    string
	ErrorMessage string
	Notice       string
	AdminKey     string // shown once after the master key is exchanged
	Admin        *adminInfo
	Data         interface{}
}

func NewDashboardHandler(storage *store.StorageService, apiKeyStore *store.ApiKeyStore, auditStore *store.AuditStore, masterKey, version string, defaultRateLimit int) *DashboardHandler {
	h := &DashboardHandler{
		storage:          storage,
		apiKeyStore:      apiKeyStore,
		auditStore:       auditStore,
		masterKey:        masterKey,
		version:          version,
		defaultRateLimit: defaultRateLimit,
		pages:            map[string]*template.Template{},
		partials:         map[string]*template.Template{},
	}

	layoutHTML := mustRead("templates/layout.html")
	for _, p := range []string{"login", "bootstrap", "index", "boxes", "keys", "audit"} {
		h.pages[p] = template.Must(template.New("page").Funcs(funcMap).Parse(layoutHTML + mustRead("templates/"+p+".html")))
	}
	for _, p := range []string{"overview", "box_list", "box_detail", "box_value", "confirm", "key_list", "key_form", "key_created", "audit_list"} {
		h.partials[p] = template.Must(template.New("partial").Funcs(funcMap).Parse(mustRead("templates/partials/" + p + ".html")))
	}
	return h
}

func mustRead(path string) string {
	data, err := fs.ReadFile(TemplateFS, path)
	if err != nil {
		panic(fmt.Sprintf("failed to read embedded template %s: %v", path, err))
	}
	return string(data)
}

// Routes registers all admin dashboard routes on the given router.
func (h *DashboardHandler) Routes(r chi.Router) {
	r.Get("/login", h.LoginPage)
	r.Post("/login", h.LoginSubmit)
	r.Post("/logout", h.Logout)

	r.Group(func(r chi.Router) {
		r.Use(h.requireSession)
		r.Get("/", h.OverviewPage)
		r.Get("/overview", h.OverviewPartial)

		r.Get("/boxes", h.BoxesPage)
		r.Get("/boxes/list", h.BoxList)
		r.Get("/boxes/detail", h.BoxDetail)
		r.Get("/boxes/value", h.BoxValue)
		r.Get("/boxes/confirm-delete", h.BoxConfirmDelete)
		r.Post("/boxes/delete", h.BoxDelete)
		r.Post("/boxes/restore", h.BoxRestore)
		r.Post("/boxes/restore-version", h.BoxRestoreVersion)

		r.Get("/keys", h.KeysPage)
		r.Get("/keys/list", h.KeyList)
		r.Get("/keys/new", h.KeyForm)
		r.Post("/keys/create", h.KeyCreate)
		r.Get("/keys/confirm", h.KeyConfirm)
		r.Post("/keys/revoke", h.KeyRevoke)
		r.Post("/keys/delete", h.KeyDelete)

		r.Get("/audit", h.AuditPage)
		r.Get("/audit/list", h.AuditList)
	})
}

// --- Sessions ---

const sessionCookieName = "StashLock.Session"

type ctxKey string

const adminCtxKey ctxKey = "dashboardAdmin"

func generateSessionToken() string {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		panic("crypto/rand failed: " + err.Error())
	}
	return hex.EncodeToString(b)
}

func (h *DashboardHandler) setSession(w http.ResponseWriter, r *http.Request, adminKeyId string) {
	token := generateSessionToken()
	h.sessions.Store(token, session{adminKeyId: adminKeyId, expiresAt: time.Now().Add(sessionTTL)})
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookieName,
		Value:    token,
		Path:     "/admin",
		HttpOnly: true,
		Secure:   r.TLS != nil || r.Header.Get("X-Forwarded-Proto") == "https",
		SameSite: http.SameSiteStrictMode,
		MaxAge:   int(sessionTTL.Seconds()),
	})
}

func (h *DashboardHandler) clearSession(w http.ResponseWriter, r *http.Request) {
	if c, err := r.Cookie(sessionCookieName); err == nil {
		h.sessions.Delete(c.Value)
	}
	http.SetCookie(w, &http.Cookie{Name: sessionCookieName, Value: "", Path: "/admin", HttpOnly: true, MaxAge: -1})
}

// currentAdmin returns the admin key behind the session, or nil when the session is
// missing, expired, or its admin key is no longer active.
func (h *DashboardHandler) currentAdmin(r *http.Request) *store.ApiKeyInfo {
	c, err := r.Cookie(sessionCookieName)
	if err != nil || c.Value == "" {
		return nil
	}
	v, ok := h.sessions.Load(c.Value)
	if !ok {
		return nil
	}
	s := v.(session)
	if time.Now().After(s.expiresAt) {
		h.sessions.Delete(c.Value)
		return nil
	}
	key, err := h.apiKeyStore.Get(s.adminKeyId)
	if err != nil || key == nil || !key.IsActive || !key.IsAdmin || isExpiredPtr(key.ExpiresAt, time.Now().UTC()) {
		h.sessions.Delete(c.Value)
		return nil
	}
	return key
}

// requireSession sends unauthenticated requests to the login page. htmx requests get
// HX-Redirect so the login page never renders inside a list or dialog.
func (h *DashboardHandler) requireSession(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		admin := h.currentAdmin(r)
		if admin == nil {
			target := "/admin/login"
			if _, err := r.Cookie(sessionCookieName); err == nil {
				target += "?expired=1"
			}
			if r.Header.Get("HX-Request") == "true" {
				w.Header().Set("HX-Redirect", target)
				w.WriteHeader(http.StatusOK)
				return
			}
			http.Redirect(w, r, target, http.StatusFound)
			return
		}
		next.ServeHTTP(w, r.WithContext(contextWithAdmin(r, admin)))
	})
}

// --- Rendering & responses ---

func (h *DashboardHandler) page(w http.ResponseWriter, r *http.Request, name, tab string, data interface{}) {
	pd := pageData{IsLoggedIn: true, Version: "v" + h.version, ActiveTab: tab, Data: data}
	if a := adminFrom(r); a != nil {
		pd.Admin = &adminInfo{Id: a.Id, Name: a.Name, KeyPrefix: a.KeyPrefix}
	}
	h.renderPage(w, name, pd)
}

func (h *DashboardHandler) renderPage(w http.ResponseWriter, name string, data pageData) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	if err := h.pages[name].ExecuteTemplate(w, "layout", data); err != nil {
		log.Error().Err(err).Str("page", name).Msg("Template render error")
		http.Error(w, "Internal Server Error", http.StatusInternalServerError)
	}
}

func (h *DashboardHandler) partial(w http.ResponseWriter, name string, data interface{}) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	if err := h.partials[name].Execute(w, data); err != nil {
		log.Error().Err(err).Str("partial", name).Msg("Partial template render error")
		http.Error(w, "Internal Server Error", http.StatusInternalServerError)
	}
}

// fail answers an htmx request with a plain-text message the page shows next to the form or as a toast.
func fail(w http.ResponseWriter, err error) {
	status, msg := http.StatusInternalServerError, "Something went wrong on the server. Check the server log."
	if se, ok := err.(*models.StashLockError); ok {
		status, msg = se.StatusCode, se.Message
	} else {
		log.Error().Err(err).Msg("Dashboard request failed")
	}
	http.Error(w, msg, status)
}

// trigger sets HX-Trigger events: a toast message, closing dialogs, refreshing lists.
func trigger(w http.ResponseWriter, toast string, closeDialog, refresh bool) {
	ev := map[string]interface{}{}
	if toast != "" {
		ev["toast"] = toast
	}
	if closeDialog {
		ev["closeDialog"] = true
	}
	if refresh {
		ev["refresh"] = true
	}
	b, _ := json.Marshal(ev)
	w.Header().Set("HX-Trigger", string(b))
}

// audit records a dashboard action under the signed-in admin key.
func (h *DashboardHandler) audit(r *http.Request, action, boxKey, outcome, detail string) {
	var id, name string
	if a := adminFrom(r); a != nil {
		id, name = a.Id, a.Name
	}
	h.auditStore.Log(action, boxKey, id, name, r.Header.Get("X-Correlation-Id"), outcome, detail)
}

// --- Login ---

func (h *DashboardHandler) LoginPage(w http.ResponseWriter, r *http.Request) {
	if h.currentAdmin(r) != nil {
		http.Redirect(w, r, "/admin", http.StatusFound)
		return
	}
	pd := pageData{}
	if r.URL.Query().Get("expired") == "1" {
		pd.Notice = "Your session ended. Sessions last 8 hours and end when the server restarts or the admin key is revoked. Sign in again to continue."
	}
	h.renderPage(w, "login", pd)
}

func (h *DashboardHandler) LoginSubmit(w http.ResponseWriter, r *http.Request) {
	token := strings.TrimSpace(r.FormValue("apiKey"))
	corr := r.Header.Get("X-Correlation-Id")

	if token == "" {
		h.renderPage(w, "login", pageData{ErrorMessage: "Enter your admin key."})
		return
	}

	// One-time master key: exchange it for a new admin key, shown once
	if middleware.MatchMasterKey(h.masterKey, token) {
		resp, err := h.apiKeyStore.ConsumeMasterKey(token, "Admin (from master key, "+time.Now().UTC().Format("2006-01-02")+")")
		if err != nil {
			msg := "The master key could not be exchanged. Check the server log."
			if se, ok := err.(*models.StashLockError); ok {
				msg = se.Message
			} else {
				log.Error().Err(err).Msg("Master key exchange failed")
			}
			h.auditStore.Log("login.failed", "", "", r.RemoteAddr, corr, "failed", "master key refused")
			h.AuthGuard.Fail(r)
			h.renderPage(w, "login", pageData{ErrorMessage: msg})
			return
		}
		log.Warn().Str("adminKeyId", resp.Info.Id).Msg("Master key exchanged for an admin key; the master key is now blocked")
		h.auditStore.Log("master_key.exchange", "", resp.Info.Id, resp.Info.Name, corr, "ok", "admin key "+resp.Info.KeyPrefix+"…")
		h.setSession(w, r, resp.Info.Id)
		h.renderPage(w, "bootstrap", pageData{AdminKey: resp.Key})
		return
	}

	identity, err := h.apiKeyStore.Validate(token)
	if err == nil && identity != nil && identity.IsMaster {
		h.setSession(w, r, identity.Id)
		h.auditStore.Log("login", "", identity.Id, identity.Name, corr, "ok", "")
		log.Info().Str("adminKeyId", identity.Id).Msg("Dashboard login")
		http.Redirect(w, r, "/admin", http.StatusFound)
		return
	}

	log.Warn().Str("ip", r.RemoteAddr).Msg("Dashboard login failed")
	h.AuthGuard.Fail(r)
	h.auditStore.Log("login.failed", "", "", r.RemoteAddr, corr, "failed", "invalid admin key")
	h.renderPage(w, "login", pageData{ErrorMessage: "That key is not valid. Check for missing characters; revoked or expired admin keys no longer work."})
}

func (h *DashboardHandler) Logout(w http.ResponseWriter, r *http.Request) {
	h.clearSession(w, r)
	http.Redirect(w, r, "/admin/login", http.StatusFound)
}

// isExpiredPtr reports whether an RFC 3339 expiry lies before now. Unparseable values count as expired.
func isExpiredPtr(expiresAt *string, now time.Time) bool {
	if expiresAt == nil || *expiresAt == "" {
		return false
	}
	t, err := time.Parse(time.RFC3339Nano, *expiresAt)
	if err != nil {
		return true
	}
	return t.Before(now)
}
