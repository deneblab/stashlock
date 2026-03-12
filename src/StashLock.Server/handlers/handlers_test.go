package handlers

import (
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"io"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/deneblab/stashlock-servergo/crypto"
	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
	"github.com/go-chi/chi/v5"
	"github.com/jmoiron/sqlx"
	"golang.org/x/crypto/curve25519"
	_ "modernc.org/sqlite"
)

// testKeyPair generates an X25519 keypair for handler tests.
func testKeyPair(t *testing.T) (pubB64, privB64 string, pub, priv []byte) {
	t.Helper()
	priv = make([]byte, 32)
	if _, err := rand.Read(priv); err != nil {
		t.Fatal(err)
	}
	pub, err := curve25519.X25519(priv, curve25519.Basepoint)
	if err != nil {
		t.Fatal(err)
	}
	return base64.StdEncoding.EncodeToString(pub),
		base64.StdEncoding.EncodeToString(priv),
		pub, priv
}

// sealAndStore encrypts plaintext with the public key and stores it as base64.
func sealAndStore(t *testing.T, storage *store.StorageService, keyStr string, plaintext []byte, pub []byte) {
	t.Helper()
	sealed := crypto.TestSeal(plaintext, pub)
	sealedB64 := base64.StdEncoding.EncodeToString(sealed)
	key, err := models.NewKeyStore(keyStr)
	if err != nil {
		t.Fatal(err)
	}
	if err := storage.Set(key, sealedB64, nil); err != nil {
		t.Fatal(err)
	}
}

func setupTestRouter(t *testing.T) (*chi.Mux, *store.StorageService) {
	t.Helper()
	db, err := sqlx.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatalf("open db: %v", err)
	}
	if err := store.RunMigrations(db); err != nil {
		t.Fatalf("migrations: %v", err)
	}
	t.Cleanup(func() { db.Close() })

	storage := store.NewStorageService(db, 100*1024)
	auditStore := store.NewAuditStore(db)
	boxesHandler := NewBoxesHandler(storage, auditStore, "1.0.0-test")
	decodeHandler := NewDecodeHandler(storage)

	r := chi.NewRouter()
	r.Get("/", boxesHandler.GetRoot)
	r.Get("/boxes", boxesHandler.ListBoxes)
	r.Post("/boxes/{keyTextUrl}", boxesHandler.SetBox)
	r.Get("/boxes/{keyTextUrl}", boxesHandler.GetBox)
	r.Delete("/boxes/{keyTextUrl}", boxesHandler.DeleteBox)
	r.Get("/boxes/{keyTextUrl}/meta", boxesHandler.GetMetadata)
	r.Get("/boxes/{keyTextUrl}/history", boxesHandler.GetHistory)
	r.Get("/boxes/{keyTextUrl}/history/{versionNumber}", boxesHandler.GetHistoryVersion)
	r.Post("/boxes/decode", decodeHandler.DecodeBox)

	return r, storage
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

func TestListBoxes(t *testing.T) {
	r, _ := setupTestRouter(t)

	// Add some boxes
	for _, k := range []string{"app.cfg.00001", "app.secrets.00001", "other.cfg.00001"} {
		encoded := models.Base64UrlEncode(k)
		req := httptest.NewRequest("POST", "/boxes/"+encoded, strings.NewReader("val"))
		w := httptest.NewRecorder()
		r.ServeHTTP(w, req)
	}

	// List all
	req := httptest.NewRequest("GET", "/boxes", nil)
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 200 {
		t.Fatalf("status = %d", w.Code)
	}
	var keys []models.KeyEntry
	json.NewDecoder(w.Body).Decode(&keys)
	if len(keys) != 3 {
		t.Errorf("expected 3 keys, got %d", len(keys))
	}

	// List with prefix
	req = httptest.NewRequest("GET", "/boxes?prefix=app.", nil)
	w = httptest.NewRecorder()
	r.ServeHTTP(w, req)

	json.NewDecoder(w.Body).Decode(&keys)
	if len(keys) != 2 {
		t.Errorf("expected 2 keys with prefix, got %d", len(keys))
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

func TestDecodeBox_FullFlow(t *testing.T) {
	r, storage := setupTestRouter(t)
	_, privB64, pub, _ := testKeyPair(t)

	// Seal and store a value
	plaintext := []byte(`{"db_password":"hunter2"}`)
	sealAndStore(t, storage, "vault.secrets.00001", plaintext, pub)

	body := `{"box":"vault","tag":"secrets","version":"00001","privateKey":"` + privB64 + `"}`
	req := httptest.NewRequest("POST", "/boxes/decode", strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 200 {
		t.Fatalf("status = %d, body = %s", w.Code, w.Body.String())
	}

	result := w.Body.String()
	expected := `{"db_password":"hunter2"}`
	if result != expected {
		t.Errorf("decode result = %q, want %q", result, expected)
	}
}

func TestDecodeBox_DefaultVersion(t *testing.T) {
	r, storage := setupTestRouter(t)
	_, privB64, pub, _ := testKeyPair(t)

	sealAndStore(t, storage, "vault.secrets.00001", []byte(`{"db_password":"hunter2"}`), pub)

	// Omit version — should default to 00001
	body := `{"box":"vault","tag":"secrets","privateKey":"` + privB64 + `"}`
	req := httptest.NewRequest("POST", "/boxes/decode", strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 200 {
		t.Fatalf("status = %d, body = %s", w.Code, w.Body.String())
	}
	if w.Body.String() != `{"db_password":"hunter2"}` {
		t.Errorf("unexpected result: %s", w.Body.String())
	}
}

func TestDecodeBox_MissingBox(t *testing.T) {
	r, _ := setupTestRouter(t)
	body := `{"tag":"secrets","privateKey":"dGVzdA=="}`
	req := httptest.NewRequest("POST", "/boxes/decode", strings.NewReader(body))
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 400 {
		t.Errorf("status = %d, want 400", w.Code)
	}
}

func TestDecodeBox_MissingPrivateKey(t *testing.T) {
	r, _ := setupTestRouter(t)
	body := `{"box":"vault","tag":"secrets"}`
	req := httptest.NewRequest("POST", "/boxes/decode", strings.NewReader(body))
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 400 {
		t.Errorf("status = %d, want 400", w.Code)
	}
}

func TestDecodeBox_KeyNotFound(t *testing.T) {
	r, _ := setupTestRouter(t)
	_, privB64, _, _ := testKeyPair(t)
	body := `{"box":"missing","tag":"key","privateKey":"` + privB64 + `"}`
	req := httptest.NewRequest("POST", "/boxes/decode", strings.NewReader(body))
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 404 {
		t.Errorf("status = %d, want 404", w.Code)
	}
}

func TestDecodeBox_WrongPrivateKey(t *testing.T) {
	r, storage := setupTestRouter(t)
	_, _, pub, _ := testKeyPair(t)
	_, wrongPrivB64, _, _ := testKeyPair(t)

	sealAndStore(t, storage, "vault.secrets.00001", []byte("secret data"), pub)

	body := `{"box":"vault","tag":"secrets","privateKey":"` + wrongPrivB64 + `"}`
	req := httptest.NewRequest("POST", "/boxes/decode", strings.NewReader(body))
	w := httptest.NewRecorder()
	r.ServeHTTP(w, req)

	if w.Code != 400 {
		t.Errorf("status = %d, want 400", w.Code)
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
