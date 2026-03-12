package store

import (
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/base64"
	"fmt"
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
	Scopes             string  `json:"scopes"`
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
	Name               string   `json:"name"`
	Scopes             []string `json:"scopes"`
	CanRead            *bool    `json:"canRead"`
	CanWrite           *bool    `json:"canWrite"`
	CanDelete          *bool    `json:"canDelete"`
	ExpiresAt          *string  `json:"expiresAt,omitempty"`
	RateLimitPerMinute *int     `json:"rateLimitPerMinute,omitempty"`
}

// CreateApiKeyResponse includes the key info and the plaintext key (shown only once).
type CreateApiKeyResponse struct {
	Info ApiKeyInfo `json:"info"`
	Key  string     `json:"key"`
}

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

	plainKey, err := GenerateKey()
	if err != nil {
		return nil, fmt.Errorf("generate key: %w", err)
	}

	keyHash := HashKey(plainKey)
	keyPrefix := plainKey[:8]
	id := uuid.New().String()
	now := time.Now().UTC().Format(time.RFC3339Nano)

	scopes := "*"
	if len(req.Scopes) > 0 {
		scopes = strings.Join(req.Scopes, ",")
	}

	canRead := true
	if req.CanRead != nil {
		canRead = *req.CanRead
	}
	canWrite := true
	if req.CanWrite != nil {
		canWrite = *req.CanWrite
	}
	canDelete := false
	if req.CanDelete != nil {
		canDelete = *req.CanDelete
	}

	_, err = s.db.Exec(`INSERT INTO ApiKeys (Id, Name, KeyHash, KeyPrefix, Scopes, CanRead, CanWrite, CanDelete, IsActive, ExpiresAt, RateLimitPerMinute, CreatedAt)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?)`,
		id, req.Name, keyHash, keyPrefix, scopes,
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
		Scopes:             scopes,
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

// List returns all API keys (sorted by name).
func (s *ApiKeyStore) List() ([]ApiKeyInfo, error) {
	var entities []models.ApiKeyEntity
	err := s.db.Select(&entities, "SELECT * FROM ApiKeys ORDER BY Name")
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

	scopes := strings.Split(entity.Scopes, ",")

	identity := &models.ApiKeyIdentity{
		Id:       entity.Id,
		Name:     entity.Name,
		IsMaster: false,
		Scopes:   scopes,
		CanRead:  entity.CanRead,
		CanWrite: entity.CanWrite,
		CanDelete: entity.CanDelete,
	}

	if entity.RateLimitPerMinute.Valid {
		rl := int(entity.RateLimitPerMinute.Int64)
		identity.RateLimitPerMinute = &rl
	}

	return identity, nil
}

func entityToInfo(e models.ApiKeyEntity) ApiKeyInfo {
	info := ApiKeyInfo{
		Id:        e.Id,
		Name:      e.Name,
		KeyPrefix: e.KeyPrefix,
		Scopes:    e.Scopes,
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
