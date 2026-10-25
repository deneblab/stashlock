package handlers

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/deneblab/stashlock-servergo/middleware"
	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
	"github.com/jmoiron/sqlx"
	_ "modernc.org/sqlite"
)

// testServer runs requests through the real auth middleware. Requests without an
// Authorization header get a read/write/delete API key for the box.tag they target.
type testServer struct {
	t       *testing.T
	handler http.Handler
	keys    *store.ApiKeyStore
	tokens  map[string]string
	admin   string // admin key token
}

func (s *testServer) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.Header.Get("Authorization") == "" {
		if box, tag, ok := requestBoxTag(r); ok {
			r.Header.Set("Authorization", "Bearer "+s.token(box, tag, true, true, true))
		}
	}
	s.handler.ServeHTTP(w, r)
}

// token returns (and caches) an API key bound to box.tag with the given permissions.
func (s *testServer) token(box, tag string, canRead, canWrite, canDelete bool) string {
	s.t.Helper()
	cacheKey := fmt.Sprintf("%s.%s.%v%v%v", box, tag, canRead, canWrite, canDelete)
	if tok, ok := s.tokens[cacheKey]; ok {
		return tok
	}
	resp, err := s.keys.Create(store.CreateApiKeyRequest{
		Name: cacheKey, Box: box, Tag: tag,
		CanRead: &canRead, CanWrite: &canWrite, CanDelete: &canDelete,
	})
	if err != nil {
		s.t.Fatalf("create api key: %v", err)
	}
	s.tokens[cacheKey] = resp.Key
	return resp.Key
}

// requestBoxTag extracts box and tag from /boxes/{b64key}...
func requestBoxTag(r *http.Request) (string, string, bool) {
	rest, ok := strings.CutPrefix(r.URL.Path, "/boxes/")
	if !ok || rest == "" {
		return "", "", false
	}
	key, err := models.FromBase64Url(strings.SplitN(rest, "/", 2)[0])
	if err != nil {
		return "", "", false
	}
	return key.Box, key.Tag, true
}

func setupTestRouter(t *testing.T) (*testServer, *store.StorageService) {
	t.Helper()
	db, err := sqlx.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatalf("open db: %v", err)
	}
	db.SetMaxOpenConns(1)
	if err := store.RunMigrations(db); err != nil {
		t.Fatalf("migrations: %v", err)
	}
	t.Cleanup(func() { db.Close() })

	storage := store.NewStorageService(db, 100*1024)
	auditStore := store.NewAuditStore(db)
	apiKeyStore := store.NewApiKeyStore(db)
	boxesHandler := NewBoxesHandler(storage, auditStore, "1.0.0-test", 120)
	keysHandler := NewKeysHandler(apiKeyStore, auditStore)

	r := chi.NewRouter()
	r.Use(middleware.ApiKeyAuth(apiKeyStore))
	r.Get("/", boxesHandler.GetRoot)
	r.Get("/boxes", boxesHandler.ListBoxes)
	r.Post("/boxes/{keyTextUrl}", boxesHandler.SetBox)
	r.Get("/boxes/{keyTextUrl}", boxesHandler.GetBox)
	r.Delete("/boxes/{keyTextUrl}", boxesHandler.DeleteBox)
	r.Get("/boxes/{keyTextUrl}/meta", boxesHandler.GetMetadata)
	r.Get("/boxes/{keyTextUrl}/history", boxesHandler.GetHistory)
	r.Get("/boxes/{keyTextUrl}/history/{versionNumber}", boxesHandler.GetHistoryVersion)
	r.Post("/keys", keysHandler.CreateKey)
	r.Get("/keys", keysHandler.ListKeys)

	admin, err := apiKeyStore.Create(store.CreateApiKeyRequest{Name: "admin", IsAdmin: true})
	if err != nil {
		t.Fatalf("create admin key: %v", err)
	}

	return &testServer{t: t, handler: r, keys: apiKeyStore, tokens: map[string]string{}, admin: admin.Key}, storage
}

func TestGetRoot_ReturnsVersionString(t *testing.T) {
	r, _ := setupTestRouter(t)
	req := httptest.NewRequest("GET", "/", nil)
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 200 {
		t.Errorf("status = %d, want 200", w.Code)
	}
	body := w.Body.String()
	if !strings.Contains(body, "OK ; Version: 1.0.0-test") {
		t.Errorf("body = %q, want to contain version", body)
	}
}

func TestSetAndGetBox_RoundTrip(t *testing.T) {
	r, _ := setupTestRouter(t)
	key := models.Base64UrlEncode("app.config.00001")

	// Set
	req := httptest.NewRequest("POST", "/boxes/"+key, strings.NewReader("my secret value"))
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 200 {
		t.Fatalf("SET status = %d, body = %s", w.Code, w.Body.String())
	}

	// Get
	req = httptest.NewRequest("GET", "/boxes/"+key, nil)
	w = httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 200 {
		t.Fatalf("GET status = %d", w.Code)
	}
	if w.Body.String() != "my secret value" {
		t.Errorf("GET body = %q, want %q", w.Body.String(), "my secret value")
	}
}

func TestGetBox_Returns404ForMissing(t *testing.T) {
	r, _ := setupTestRouter(t)
	key := models.Base64UrlEncode("miss.ing.00001")

	req := httptest.NewRequest("GET", "/boxes/"+key, nil)
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 404 {
		t.Errorf("status = %d, want 404", w.Code)
	}
}

func TestDeleteBox(t *testing.T) {
	r, _ := setupTestRouter(t)
	key := models.Base64UrlEncode("app.config.00001")

	// Set first
	req := httptest.NewRequest("POST", "/boxes/"+key, strings.NewReader("value"))
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	// Delete
	req = httptest.NewRequest("DELETE", "/boxes/"+key, nil)
	w = httptest.NewRecorder()
	r.ServeHTTP(w, req)
	if w.Code != 200 {
		t.Errorf("DELETE status = %d, want 200", w.Code)
	}

	// Get should 404
	req = httptest.NewRequest("GET", "/boxes/"+key, nil)
	w = httptest.NewRecorder()
	r.ServeHTTP(w, req)
	if w.Code != 404 {
		t.Errorf("GET after DELETE status = %d, want 404", w.Code)
	}
}

func TestListBoxes_OnlyOwnBoxTag(t *testing.T) {
	r, _ := setupTestRouter(t)

	for _, k := range []string{"app.cfg.00001", "app.cfg.00002", "app.secrets.00001", "other.cfg.00001"} {
		encoded := models.Base64UrlEncode(k)
		req := httptest.NewRequest("POST", "/boxes/"+encoded, strings.NewReader("val"))
		w := httptest.NewRecorder()
		r.ServeHTTP(w, req)
		if w.Code != 200 {
			t.Fatalf("SET %s status = %d", k, w.Code)
		}
	}

	list := func(query string) []models.KeyEntry {
		t.Helper()
		req := httptest.NewRequest("GET", "/boxes"+query, nil)
		req.Header.Set("Authorization", "Bearer "+r.token("app", "cfg", true, true, true))
		w := httptest.NewRecorder()
		r.ServeHTTP(w, req)
		if w.Code != 200 {
			t.Fatalf("list status = %d", w.Code)
		}
		var keys []models.KeyEntry
		json.NewDecoder(w.Body).Decode(&keys)
		return keys
	}

	if keys := list(""); len(keys) != 2 {
		t.Errorf("expected 2 keys for app.cfg, got %d", len(keys))
	}
	if keys := list("?prefix=app."); len(keys) != 2 {
		t.Errorf("prefix wider than own box.tag: expected 2 keys, got %d", len(keys))
	}
	if keys := list("?prefix=app.cfg.00002"); len(keys) != 1 {
		t.Errorf("narrower prefix: expected 1 key, got %d", len(keys))
	}
	if keys := list("?prefix=other."); len(keys) != 0 {
		t.Errorf("foreign prefix: expected 0 keys, got %d", len(keys))
	}
}

func TestBoxes_ApiKeyLimitedToBoxTag(t *testing.T) {
	r, _ := setupTestRouter(t)
	token := r.token("app", "develop", true, true, true)

	for _, k := range []string{"app.production.00001", "other.develop.00001"} {
		req := httptest.NewRequest("GET", "/boxes/"+models.Base64UrlEncode(k), nil)
		req.Header.Set("Authorization", "Bearer "+token)
		w := httptest.NewRecorder()
		r.ServeHTTP(w, req)
		if w.Code != 403 {
			t.Errorf("GET %s with app.develop key: status = %d, want 403", k, w.Code)
		}
	}
}

func TestBoxes_PermissionsEnforced(t *testing.T) {
	r, _ := setupTestRouter(t)
	key := models.Base64UrlEncode("app.develop.00001")
	readOnly := r.token("app", "develop", true, false, false)

	req := httptest.NewRequest("POST", "/boxes/"+key, strings.NewReader("v"))
	req.Header.Set("Authorization", "Bearer "+readOnly)
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)
	if w.Code != 403 {
		t.Errorf("write with read-only key: status = %d, want 403", w.Code)
	}
}

func TestBoxes_AdminKeyForbidden(t *testing.T) {
	r, _ := setupTestRouter(t)
	for _, path := range []string{"/boxes", "/boxes/" + models.Base64UrlEncode("app.develop.00001")} {
		req := httptest.NewRequest("GET", path, nil)
		req.Header.Set("Authorization", "Bearer "+r.admin)
		w := httptest.NewRecorder()
		r.ServeHTTP(w, req)
		if w.Code != 403 {
			t.Errorf("GET %s with admin key: status = %d, want 403", path, w.Code)
		}
	}
}

func TestBoxes_MissingOrInvalidToken(t *testing.T) {
	r, _ := setupTestRouter(t)
	req := httptest.NewRequest("GET", "/boxes", nil)
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)
	if w.Code != 401 {
		t.Errorf("no token: status = %d, want 401", w.Code)
	}

	req = httptest.NewRequest("GET", "/boxes", nil)
	req.Header.Set("Authorization", "Bearer slk_wrong")
	w = httptest.NewRecorder()
	r.ServeHTTP(w, req)
	if w.Code != 401 {
		t.Errorf("invalid token: status = %d, want 401", w.Code)
	}
}

func TestKeys_CreateRequiresMasterAndBoxTag(t *testing.T) {
	r, _ := setupTestRouter(t)

	post := func(token, body string) int {
		req := httptest.NewRequest("POST", "/keys", strings.NewReader(body))
		req.Header.Set("Authorization", "Bearer "+token)
		w := httptest.NewRecorder()
		r.ServeHTTP(w, req)
		return w.Code
	}

	if code := post(r.admin, `{"name":"svc","box":"app","tag":"develop"}`); code != 201 {
		t.Errorf("admin create: status = %d, want 201", code)
	}
	if code := post(r.admin, `{"name":"svc","box":"app"}`); code != 400 {
		t.Errorf("missing tag: status = %d, want 400", code)
	}
	if code := post(r.admin, `{"name":"svc","box":"a.b","tag":"develop"}`); code != 400 {
		t.Errorf("dotted box: status = %d, want 400", code)
	}
	if code := post(r.token("app", "develop", true, true, true), `{"name":"svc","box":"app","tag":"develop"}`); code != 403 {
		t.Errorf("api key create: status = %d, want 403", code)
	}
}

func TestGetMetadata(t *testing.T) {
	r, _ := setupTestRouter(t)
	key := models.Base64UrlEncode("app.config.00001")

	req := httptest.NewRequest("POST", "/boxes/"+key, strings.NewReader("test data"))
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	req = httptest.NewRequest("GET", "/boxes/"+key+"/meta", nil)
	w = httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 200 {
		t.Fatalf("status = %d", w.Code)
	}
	var meta models.KeyMetadata
	json.NewDecoder(w.Body).Decode(&meta)
	if meta.SizeBytes != len("test data") {
		t.Errorf("SizeBytes = %d, want %d", meta.SizeBytes, len("test data"))
	}
}

func TestHistory(t *testing.T) {
	r, _ := setupTestRouter(t)
	key := models.Base64UrlEncode("app.config.00001")

	// Create 3 versions
	for _, val := range []string{"v1", "v2", "v3"} {
		req := httptest.NewRequest("POST", "/boxes/"+key, strings.NewReader(val))
		w := httptest.NewRecorder()
		r.ServeHTTP(w, req)
	}

	// Get history
	req := httptest.NewRequest("GET", "/boxes/"+key+"/history", nil)
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 200 {
		t.Fatalf("status = %d", w.Code)
	}
	var resp models.HistoryResponse
	json.NewDecoder(w.Body).Decode(&resp)
	if resp.TotalVersions != 2 {
		t.Errorf("TotalVersions = %d, want 2", resp.TotalVersions)
	}

	// Get specific version
	req = httptest.NewRequest("GET", "/boxes/"+key+"/history/1", nil)
	w = httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 200 {
		t.Fatalf("version status = %d", w.Code)
	}
	body, _ := io.ReadAll(w.Body)
	if string(body) != "v1" {
		t.Errorf("version 1 = %q, want %q", string(body), "v1")
	}
}

func TestSetBox_RejectsLongKey(t *testing.T) {
	r, _ := setupTestRouter(t)

	// Create a key that's too long after encoding
	longKey := strings.Repeat("a", 200)
	encoded := models.Base64UrlEncode(longKey)

	req := httptest.NewRequest("POST", "/boxes/"+encoded, strings.NewReader("value"))
	req.Header.Set("Authorization", "Bearer "+r.token("app", "config", true, true, false))
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 400 {
		t.Errorf("status = %d, want 400", w.Code)
	}
}

func TestErrorResponse_HasCorrectFormat(t *testing.T) {
	r, _ := setupTestRouter(t)
	key := models.Base64UrlEncode("miss.ing.00001")

	req := httptest.NewRequest("GET", "/boxes/"+key, nil)
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	var apiErr models.ApiError
	json.NewDecoder(w.Body).Decode(&apiErr)

	if apiErr.ErrorCode == "" {
		t.Error("errorCode should not be empty")
	}
	if apiErr.Status != 404 {
		t.Errorf("status = %d, want 404", apiErr.Status)
	}
	if apiErr.Detail == "" {
		t.Error("detail should not be empty")
	}
}

func TestHistoryAndMeta_HiddenForDeletedEntries(t *testing.T) {
	r, _ := setupTestRouter(t)
	key := models.Base64UrlEncode("app.config.00001")
	for _, v := range []string{"v1", "v2"} {
		w := httptest.NewRecorder()
		r.ServeHTTP(w, httptest.NewRequest("POST", "/boxes/"+key, strings.NewReader(v)))
	}
	w := httptest.NewRecorder()
	r.ServeHTTP(w, httptest.NewRequest("DELETE", "/boxes/"+key, nil))
	if w.Code != 200 {
		t.Fatalf("delete: %d", w.Code)
	}
	for _, p := range []string{"/history", "/history/1", "/meta"} {
		w := httptest.NewRecorder()
		r.ServeHTTP(w, httptest.NewRequest("GET", "/boxes/"+key+p, nil))
		if w.Code != 404 {
			t.Errorf("GET %s of deleted entry: %d, want 404", p, w.Code)
		}
	}
}
