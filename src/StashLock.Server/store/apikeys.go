package store

import (
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/base64"
	"fmt"
	"regexp"
	"strings"
	"time"

	"github.com/deneblab/stashlock-servergo/models"
	"github.com/google/uuid"
	"github.com/jmoiron/sqlx"
)

// ApiKeyStore handles API key CRUD operations.
type ApiKeyStore struct {
	db *sqlx.DB
}

func NewApiKeyStore(db *sqlx.DB) *ApiKeyStore {
	return &ApiKeyStore{db: db}
}

// ApiKeyInfo is the public representation of an API key (no hash/secret).
type ApiKeyInfo struct {
	Id                 string  `json:"id"`
	Name               string  `json:"name"`
	KeyPrefix          string  `json:"keyPrefix"`
	IsAdmin            bool    `json:"isAdmin"`
	Box                string  `json:"box,omitempty"`
	Tag                string  `json:"tag,omitempty"`
	CanRead            bool    `json:"canRead"`
	CanWrite           bool    `json:"canWrite"`
	CanDelete          bool    `json:"canDelete"`
	IsActive           bool    `json:"isActive"`
	ExpiresAt          *string `json:"expiresAt,omitempty"`
	RateLimitPerMinute *int    `json:"rateLimitPerMinute,omitempty"`
	CreatedAt          string  `json:"createdAt"`
}

// CreateApiKeyRequest is the request body for creating a new API key.
type CreateApiKeyRequest struct {
	Name               string  `json:"name"`
	IsAdmin            bool    `json:"isAdmin"`
	Box                string  `json:"box"`
	Tag                string  `json:"tag"`
	CanRead            *bool   `json:"canRead"`
	CanWrite           *bool   `json:"canWrite"`
	CanDelete          *bool   `json:"canDelete"`
	ExpiresAt          *string `json:"expiresAt,omitempty"`
	RateLimitPerMinute *int    `json:"rateLimitPerMinute,omitempty"`
}

// boxTagPattern restricts box and tag names bound to API keys (no dots: they separate box.tag.version).
var boxTagPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)

// ValidateBoxTag checks that box and tag are usable as an API key binding.
func ValidateBoxTag(box, tag string) error {
	if !boxTagPattern.MatchString(box) {
		return models.NewStashLockError(models.ErrorCodes.InvalidKeyFormat,
			"box must be 1-64 characters: letters, digits, '-' or '_'", 400)
	}
	if !boxTagPattern.MatchString(tag) {
		return models.NewStashLockError(models.ErrorCodes.InvalidKeyFormat,
			"tag must be 1-64 characters: letters, digits, '-' or '_'", 400)
	}
	return nil
}

// CreateApiKeyResponse includes the key info and the plaintext key (shown only once).
type CreateApiKeyResponse struct {
	Info ApiKeyInfo `json:"info"`
	Key  string     `json:"key"`
}

// AdminKeyPrefix marks admin keys so they are easy to tell apart from box.tag API keys.
const AdminKeyPrefix = "slk_adm_"

// GenerateKey creates a random API key with the slk_ prefix.
func GenerateKey() (string, error) {
	b := make([]byte, 48)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("generate random key: %w", err)
	}
	encoded := base64.StdEncoding.EncodeToString(b)
	// Strip +, /, = to make URL-safe
	encoded = strings.NewReplacer("+", "", "/", "", "=", "").Replace(encoded)
	if len(encoded) > 40 {
		encoded = encoded[:40]
	}
	return "slk_" + encoded, nil
}

// HashKey computes the SHA-256 hash of the key as lowercase hex.
func HashKey(key string) string {
	h := sha256.Sum256([]byte(key))
	return fmt.Sprintf("%x", h[:])
}

// Create generates a new API key and stores it.
func (s *ApiKeyStore) Create(req CreateApiKeyRequest) (*CreateApiKeyResponse, error) {
	if req.Name == "" {
		return nil, models.NewStashLockError(models.ErrorCodes.MissingRequiredFields, "Name is required", 400)
	}
	return s.create(s.db, req)
}

// execer is satisfied by *sqlx.DB and *sqlx.Tx.
type execer interface {
	Exec(query string, args ...interface{}) (sql.Result, error)
}

func (s *ApiKeyStore) create(db execer, req CreateApiKeyRequest) (*CreateApiKeyResponse, error) {
	if req.IsAdmin {
		// Admin keys administer the server; they are not bound to a box.tag and cannot touch secrets
		req.Box, req.Tag = "", ""
		f := false
		req.CanRead, req.CanWrite, req.CanDelete = &f, &f, &f
	} else if err := ValidateBoxTag(req.Box, req.Tag); err != nil {
		return nil, err
	}

	plainKey, err := GenerateKey()
	if err != nil {
		return nil, fmt.Errorf("generate key: %w", err)
	}
	keyPrefix := plainKey[:8]
	if req.IsAdmin {
		plainKey = AdminKeyPrefix + strings.TrimPrefix(plainKey, "slk_")
		keyPrefix = plainKey[:12]
	}

	keyHash := HashKey(plainKey)
	id := uuid.New().String()
	now := time.Now().UTC().Format(time.RFC3339Nano)

	canRead := true
	if req.CanRead != nil {
		canRead = *req.CanRead
	}
	canWrite := false
	if req.CanWrite != nil {
		canWrite = *req.CanWrite
	}
	canDelete := false
	if req.CanDelete != nil {
		canDelete = *req.CanDelete
	}

	_, err = db.Exec(`INSERT INTO ApiKeys (Id, Name, KeyHash, KeyPrefix, IsAdmin, Box, Tag, CanRead, CanWrite, CanDelete, IsActive, ExpiresAt, RateLimitPerMinute, CreatedAt)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?)`,
		id, req.Name, keyHash, keyPrefix, boolToInt(req.IsAdmin), req.Box, req.Tag,
		boolToInt(canRead), boolToInt(canWrite), boolToInt(canDelete),
		nullStr(req.ExpiresAt), nullInt(req.RateLimitPerMinute), now,
	)
	if err != nil {
		return nil, fmt.Errorf("insert api key: %w", err)
	}

	info := ApiKeyInfo{
		Id:                 id,
		Name:               req.Name,
		KeyPrefix:          keyPrefix,
		IsAdmin:            req.IsAdmin,
		Box:                req.Box,
		Tag:                req.Tag,
		CanRead:            canRead,
		CanWrite:           canWrite,
		CanDelete:          canDelete,
		IsActive:           true,
		ExpiresAt:          req.ExpiresAt,
		RateLimitPerMinute: req.RateLimitPerMinute,
		CreatedAt:          now,
	}

	return &CreateApiKeyResponse{Info: info, Key: plainKey}, nil
}

// List returns API keys sorted by box, tag and name; empty box/tag means no filter.
func (s *ApiKeyStore) List(box, tag string) ([]ApiKeyInfo, error) {
	query := "SELECT * FROM ApiKeys WHERE 1=1"
	var args []interface{}
	if box != "" {
		query += " AND Box = ?"
		args = append(args, box)
	}
	if tag != "" {
		query += " AND Tag = ?"
		args = append(args, tag)
	}
	query += " ORDER BY IsAdmin DESC, Box, Tag, Name"

	var entities []models.ApiKeyEntity
	err := s.db.Select(&entities, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list api keys: %w", err)
	}

	result := make([]ApiKeyInfo, 0, len(entities))
	for _, e := range entities {
		result = append(result, entityToInfo(e))
	}
	return result, nil
}

// Get returns a single API key by ID.
func (s *ApiKeyStore) Get(id string) (*ApiKeyInfo, error) {
	var entity models.ApiKeyEntity
	err := s.db.Get(&entity, "SELECT * FROM ApiKeys WHERE Id = ?", id)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("get api key: %w", err)
	}
	info := entityToInfo(entity)
	return &info, nil
}

// Revoke soft-disables an API key (IsActive = false).
func (s *ApiKeyStore) Revoke(id string) error {
	if err := s.ensureNotLastAdmin(id); err != nil {
		return err
	}
	result, err := s.db.Exec("UPDATE ApiKeys SET IsActive = 0 WHERE Id = ?", id)
	if err != nil {
		return fmt.Errorf("revoke api key: %w", err)
	}
	rows, _ := result.RowsAffected()
	if rows == 0 {
		return models.NewStashLockError(models.ErrorCodes.KeyNotFound, "API key not found", 404)
	}
	return nil
}

// Delete permanently removes an API key.
func (s *ApiKeyStore) Delete(id string) error {
	if err := s.ensureNotLastAdmin(id); err != nil {
		return err
	}
	result, err := s.db.Exec("DELETE FROM ApiKeys WHERE Id = ?", id)
	if err != nil {
		return fmt.Errorf("delete api key: %w", err)
	}
	rows, _ := result.RowsAffected()
	if rows == 0 {
		return models.NewStashLockError(models.ErrorCodes.KeyNotFound, "API key not found", 404)
	}
	return nil
}

// Validate checks a plaintext token against the database and returns the identity if valid.
func (s *ApiKeyStore) Validate(token string) (*models.ApiKeyIdentity, error) {
	keyHash := HashKey(token)

	var entity models.ApiKeyEntity
	err := s.db.Get(&entity, "SELECT * FROM ApiKeys WHERE KeyHash = ?", keyHash)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("validate api key: %w", err)
	}

	if !entity.IsActive {
		return nil, nil
	}

	// Check expiry
	if entity.ExpiresAt.Valid && entity.ExpiresAt.String != "" {
		expiresAt, err := time.Parse(time.RFC3339Nano, entity.ExpiresAt.String)
		if err == nil && expiresAt.Before(time.Now().UTC()) {
			return nil, nil
		}
	}

	identity := &models.ApiKeyIdentity{
		Id:        entity.Id,
		Name:      entity.Name,
		IsMaster:  entity.IsAdmin,
		Box:       entity.Box,
		Tag:       entity.Tag,
		CanRead:   entity.CanRead,
		CanWrite:  entity.CanWrite,
		CanDelete: entity.CanDelete,
	}

	if entity.RateLimitPerMinute.Valid {
		rl := int(entity.RateLimitPerMinute.Int64)
		identity.RateLimitPerMinute = &rl
	}

	return identity, nil
}

// ensureNotLastAdmin refuses to disable the only remaining active admin key.
func (s *ApiKeyStore) ensureNotLastAdmin(id string) error {
	var isAdmin bool
	err := s.db.Get(&isAdmin, "SELECT IsAdmin FROM ApiKeys WHERE Id = ? AND IsActive = 1", id)
	if err == sql.ErrNoRows || (err == nil && !isAdmin) {
		return nil
	}
	if err != nil {
		return fmt.Errorf("check admin key: %w", err)
	}
	var others int
	if err := s.db.Get(&others, "SELECT COUNT(*) FROM ApiKeys WHERE IsAdmin = 1 AND IsActive = 1 AND Id <> ?", id); err != nil {
		return fmt.Errorf("count admin keys: %w", err)
	}
	if others == 0 {
		return models.NewStashLockError(models.ErrorCodes.Forbidden,
			"Cannot remove the last active admin key; create another admin key first", 409)
	}
	return nil
}

// HasActiveAdmin reports whether at least one active admin key exists.
func (s *ApiKeyStore) HasActiveAdmin() (bool, error) {
	var n int
	if err := s.db.Get(&n, "SELECT COUNT(*) FROM ApiKeys WHERE IsAdmin = 1 AND IsActive = 1"); err != nil {
		return false, fmt.Errorf("count admin keys: %w", err)
	}
	return n > 0, nil
}

// IsMasterKeyConsumed reports whether the one-time master key was already exchanged.
func (s *ApiKeyStore) IsMasterKeyConsumed(masterKey string) (bool, error) {
	var n int
	if err := s.db.Get(&n, "SELECT COUNT(*) FROM ConsumedMasterKeys WHERE KeyHash = ?", HashKey(masterKey)); err != nil {
		return false, fmt.Errorf("check master key: %w", err)
	}
	return n > 0, nil
}

// AdminAccessAvailable reports whether anyone can still administer the server:
// an active admin key exists, or the configured master key has not been used yet.
func (s *ApiKeyStore) AdminAccessAvailable(masterKey string) (bool, error) {
	if ok, err := s.HasActiveAdmin(); err != nil || ok {
		return ok, err
	}
	if masterKey == "" {
		return false, nil
	}
	consumed, err := s.IsMasterKeyConsumed(masterKey)
	return !consumed, err
}

// ErrMasterKeyConsumed is returned when the one-time master key was already exchanged.
var ErrMasterKeyConsumed = models.NewStashLockError(models.ErrorCodes.Unauthorized,
	"The master key has already been used. Log in with your admin key, or set a new STASHLOCK_MASTER_KEY and restart.", 401)

// ConsumeMasterKey exchanges the one-time master key for a new admin key, atomically.
// The caller must have verified that the key matches the configured master key.
func (s *ApiKeyStore) ConsumeMasterKey(masterKey, adminName string) (*CreateApiKeyResponse, error) {
	tx, err := s.db.Beginx()
	if err != nil {
		return nil, fmt.Errorf("begin: %w", err)
	}
	defer tx.Rollback()

	resp, err := s.create(tx, CreateApiKeyRequest{Name: adminName, IsAdmin: true})
	if err != nil {
		return nil, err
	}
	_, err = tx.Exec("INSERT INTO ConsumedMasterKeys (KeyHash, AdminKeyId, ConsumedAt) VALUES (?, ?, ?)",
		HashKey(masterKey), resp.Info.Id, time.Now().UTC().Format(time.RFC3339Nano))
	if err != nil {
		if strings.Contains(err.Error(), "UNIQUE") || strings.Contains(err.Error(), "PRIMARY KEY") {
			return nil, ErrMasterKeyConsumed
		}
		return nil, fmt.Errorf("record master key use: %w", err)
	}
	if err := tx.Commit(); err != nil {
		return nil, fmt.Errorf("commit: %w", err)
	}
	return resp, nil
}

func entityToInfo(e models.ApiKeyEntity) ApiKeyInfo {
	info := ApiKeyInfo{
		Id:        e.Id,
		Name:      e.Name,
		KeyPrefix: e.KeyPrefix,
		IsAdmin:   e.IsAdmin,
		Box:       e.Box,
		Tag:       e.Tag,
		CanRead:   e.CanRead,
		CanWrite:  e.CanWrite,
		CanDelete: e.CanDelete,
		IsActive:  e.IsActive,
		CreatedAt: e.CreatedAt,
	}
	if e.ExpiresAt.Valid {
		info.ExpiresAt = &e.ExpiresAt.String
	}
	if e.RateLimitPerMinute.Valid {
		rl := int(e.RateLimitPerMinute.Int64)
		info.RateLimitPerMinute = &rl
	}
	return info
}

func boolToInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

func nullInt(n *int) interface{} {
	if n == nil {
		return nil
	}
	return *n
}
