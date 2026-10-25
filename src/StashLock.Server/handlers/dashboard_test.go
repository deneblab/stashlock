package handlers

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"strings"
	"testing"

	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
	"github.com/jmoiron/sqlx"
)

const dashMasterKey = "0123456789abcdef0123456789abcdef"

type dashEnv struct {
	t      *testing.T
	router http.Handler
	keys   *store.ApiKeyStore
	cookie *http.Cookie
}

func setupDashboard(t *testing.T) *dashEnv {
	t.Helper()
	TemplateFS = os.DirFS("..")
	db, err := sqlx.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatal(err)
	}
	db.SetMaxOpenConns(1)
	if err := store.RunMigrations(db); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	keys := store.NewApiKeyStore(db)
	h := NewDashboardHandler(store.NewStorageService(db, 100*1024), keys, store.NewAuditStore(db), dashMasterKey, "test", 60)
	r := chi.NewRouter()
	r.Route("/admin", h.Routes)
	return &dashEnv{t: t, router: r, keys: keys}
}

func (e *dashEnv) do(method, path string, form url.Values, htmx bool) *httptest.ResponseRecorder {
	e.t.Helper()
	var body *strings.Reader
	if form != nil {
		body = strings.NewReader(form.Encode())
	} else {
		body = strings.NewReader("")
	}
	req := httptest.NewRequest(method, path, body)
	if form != nil {
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	}
	if htmx {
		req.Header.Set("HX-Request", "true")
	}
	if e.cookie != nil {
		req.AddCookie(e.cookie)
	}
	w := httptest.NewRecorder()
	e.router.ServeHTTP(w, req)
	for _, c := range w.Result().Cookies() {
		if c.Name == sessionCookieName && c.MaxAge >= 0 {
			e.cookie = c
		}
	}
	return w
}

func (e *dashEnv) login() string {
	e.t.Helper()
	w := e.do("POST", "/admin/login", url.Values{"apiKey": {dashMasterKey}}, false)
	if !strings.Contains(w.Body.String(), "slk_adm_") || e.cookie == nil {
		e.t.Fatalf("bootstrap login failed: %d", w.Code)
	}
	i := strings.Index(w.Body.String(), "slk_adm_")
	return w.Body.String()[i : i+len("slk_adm_")+40]
}

func TestDashboard_HtmxWithoutSessionGetsHxRedirect(t *testing.T) {
	e := setupDashboard(t)
	w := e.do("GET", "/admin/boxes/list", nil, true)
	if w.Code != 200 || w.Header().Get("HX-Redirect") != "/admin/login" {
		t.Errorf("status=%d HX-Redirect=%q", w.Code, w.Header().Get("HX-Redirect"))
	}

	e.cookie = &http.Cookie{Name: sessionCookieName, Value: "stale"}
	w = e.do("GET", "/admin/boxes/list", nil, true)
	if w.Header().Get("HX-Redirect") != "/admin/login?expired=1" {
		t.Errorf("stale session: HX-Redirect=%q", w.Header().Get("HX-Redirect"))
	}
	w = e.do("GET", "/admin/boxes", nil, false)
	if w.Code != http.StatusFound {
		t.Errorf("page without session: status=%d, want 302", w.Code)
	}
}

func TestDashboard_PagesRenderAfterLogin(t *testing.T) {
	e := setupDashboard(t)
	e.login()
	for _, p := range []string{"/admin/", "/admin/overview", "/admin/boxes", "/admin/boxes/list", "/admin/keys", "/admin/keys/list",
		"/admin/keys?type=admin", "/admin/keys/list?type=admin", "/admin/keys/new", "/admin/audit", "/admin/audit/list"} {
		if w := e.do("GET", p, nil, true); w.Code != 200 {
			t.Errorf("GET %s: status=%d body=%s", p, w.Code, w.Body.String())
		}
	}
}

func TestDashboard_AdminKeyLoginAndRevokedKeyEndsSession(t *testing.T) {
	e := setupDashboard(t)
	adminKey := e.login()

	// Second login with the master key is refused
	e.cookie = nil
	w := e.do("POST", "/admin/login", url.Values{"apiKey": {dashMasterKey}}, false)
	if !strings.Contains(w.Body.String(), "already been used") {
		t.Error("master key should work only once")
	}

	w = e.do("POST", "/admin/login", url.Values{"apiKey": {adminKey}}, false)
	if w.Code != http.StatusFound || e.cookie == nil {
		t.Fatalf("admin key login: status=%d", w.Code)
	}

	// Create a second admin, revoke the first; its session must end
	second, err := e.keys.Create(store.CreateApiKeyRequest{Name: "second", IsAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	admins, _ := e.keys.ListByType(true)
	for _, a := range admins {
		if a.Id != second.Info.Id {
			if err := e.keys.Revoke(a.Id); err != nil {
				t.Fatal(err)
			}
		}
	}
	if w := e.do("GET", "/admin/keys/list", nil, true); w.Header().Get("HX-Redirect") == "" {
		t.Error("session of a revoked admin key should end")
	}
}

func TestDashboard_KeyCreateValidationAndLastAdmin(t *testing.T) {
	e := setupDashboard(t)
	e.login()

	w := e.do("POST", "/admin/keys/create", url.Values{"name": {"ci"}, "box": {"app"}, "tag": {"develop"}, "canRead": {"true"}, "expiresAt": {"2099-12-31"}}, true)
	if w.Code != 200 || !strings.Contains(w.Header().Get("HX-Trigger"), "closeDialog") || !strings.Contains(w.Body.String(), "STASHLOCK_API_KEY=slk_") {
		t.Errorf("create: status=%d trigger=%q", w.Code, w.Header().Get("HX-Trigger"))
	}
	for name, form := range map[string]url.Values{
		"dotted tag":  {"name": {"x"}, "box": {"a"}, "tag": {"b.c"}, "canRead": {"true"}},
		"past expiry": {"name": {"x"}, "box": {"a"}, "tag": {"b"}, "canRead": {"true"}, "expiresAt": {"2020-01-01"}},
		"no access":   {"name": {"x"}, "box": {"a"}, "tag": {"b"}},
		"bad limit":   {"name": {"x"}, "box": {"a"}, "tag": {"b"}, "canRead": {"true"}, "rateLimitPerMinute": {"0"}},
	} {
		if w := e.do("POST", "/admin/keys/create", form, true); w.Code != 400 {
			t.Errorf("%s: status=%d, want 400", name, w.Code)
		}
	}

	admins, _ := e.keys.ListByType(true)
	w = e.do("POST", "/admin/keys/revoke?id="+admins[0].Id, nil, true)
	if w.Code != 409 || !strings.Contains(w.Body.String(), "last active admin key") {
		t.Errorf("revoke last admin: status=%d body=%q", w.Code, w.Body.String())
	}
}
