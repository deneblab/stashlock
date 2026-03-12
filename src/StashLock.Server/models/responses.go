package models

// ApiResult is the standard success response.
type ApiResult struct {
	Status  string `json:"status"`
	Key     string `json:"key,omitempty"`
	Message string `json:"message,omitempty"`
}

// ApiError is the standard error response.
type ApiError struct {
	Status    int               `json:"status"`
	Title     string            `json:"title"`
	Detail    string            `json:"detail,omitempty"`
	TraceId   string            `json:"traceId,omitempty"`
	ErrorCode string            `json:"errorCode,omitempty"`
	Errors    map[string][]string `json:"errors,omitempty"`
}

// KeyEntry represents a key in the list response.
type KeyEntry struct {
	Key       string `json:"key"`
	Box       string `json:"box"`
	Tag       string `json:"tag"`
	Version   string `json:"version"`
	CreatedAt string `json:"createdAt"`
	UpdatedAt string `json:"updatedAt"`
	SizeBytes int    `json:"sizeBytes"`
	ExpiresAt string `json:"expiresAt,omitempty"`
}

// KeyMetadata holds metadata about a stored key.
type KeyMetadata struct {
	CreatedAt    string `json:"createdAt"`
	UpdatedAt    string `json:"updatedAt"`
	SizeBytes    int    `json:"sizeBytes"`
	IsDeleted    bool   `json:"isDeleted"`
	DeletedAt    string `json:"deletedAt,omitempty"`
	ExpiresAt    string `json:"expiresAt,omitempty"`
	IsExpired    bool   `json:"isExpired"`
	VersionCount int    `json:"versionCount"`
}

// HistoryEntry represents a single version in the history.
type HistoryEntry struct {
	VersionNumber int    `json:"versionNumber"`
	SavedAt       string `json:"savedAt"`
	SizeBytes     int    `json:"sizeBytes"`
}

// HistoryResponse wraps history entries.
type HistoryResponse struct {
	Key           string         `json:"key"`
	TotalVersions int            `json:"totalVersions"`
	Versions      []HistoryEntry `json:"versions"`
}

// DecodeRequest is the body for POST /boxes/decode.
type DecodeRequest struct {
	Box        string `json:"box"`
	Tag        string `json:"tag"`
	Version    string `json:"version"`
	PrivateKey string `json:"privateKey"`
}

// StatsResponse holds server statistics.
type StatsResponse struct {
	TotalKeys        int    `json:"totalKeys"`
	ActiveKeys       int    `json:"activeKeys"`
	DeletedKeys      int    `json:"deletedKeys"`
	ExpiredKeys      int    `json:"expiredKeys"`
	TotalSizeBytes   int64  `json:"totalSizeBytes"`
	TotalApiKeys     int    `json:"totalApiKeys"`
	ActiveApiKeys    int    `json:"activeApiKeys"`
	TotalAuditEvents int64  `json:"totalAuditEvents"`
	ServerVersion    string `json:"serverVersion"`
}

// ErrorCodes mirrors the .NET ErrorCodes constants.
var ErrorCodes = struct {
	KeyNotFound          string
	ValueTooLarge        string
	ValueRequired        string
	InvalidKeyFormat     string
	InvalidBase64        string
	Unauthorized         string
	InternalError        string
	AlreadyDeleted       string
	Forbidden            string
	InvalidTtl           string
	RateLimited          string
	DecryptionFailed     string
	MissingRequiredFields string
}{
	KeyNotFound:          "KEY_NOT_FOUND",
	ValueTooLarge:        "VALUE_TOO_LARGE",
	ValueRequired:        "VALUE_REQUIRED",
	InvalidKeyFormat:     "INVALID_KEY_FORMAT",
	InvalidBase64:        "INVALID_BASE64",
	Unauthorized:         "UNAUTHORIZED",
	InternalError:        "INTERNAL_ERROR",
	AlreadyDeleted:       "ALREADY_DELETED",
	Forbidden:            "FORBIDDEN",
	InvalidTtl:           "INVALID_TTL",
	RateLimited:          "RATE_LIMITED",
	DecryptionFailed:     "DECRYPTION_FAILED",
	MissingRequiredFields: "MISSING_REQUIRED_FIELDS",
}

// StashLockError is the application error type.
type StashLockError struct {
	ErrorCode  string
	Message    string
	StatusCode int
}

func (e *StashLockError) Error() string {
	return e.Message
}

func NewStashLockError(errorCode, message string, statusCode int) *StashLockError {
	return &StashLockError{
		ErrorCode:  errorCode,
		Message:    message,
		StatusCode: statusCode,
	}
}
