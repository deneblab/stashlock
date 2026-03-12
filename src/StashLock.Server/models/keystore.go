package models

import (
	"encoding/base64"
	"fmt"
	"strings"
)

// KeyStore represents a parsed key in box.tag.version format.
type KeyStore struct {
	Box     string
	Tag     string
	Version string
}

func NewKeyStore(keyText string) (*KeyStore, error) {
	parts := strings.Split(keyText, ".")
	// Remove empty entries (matching .NET StringSplitOptions.RemoveEmptyEntries)
	filtered := make([]string, 0, len(parts))
	for _, p := range parts {
		if p != "" {
			filtered = append(filtered, p)
		}
	}

	if len(filtered) != 3 {
		return nil, fmt.Errorf("Key must have format: box.tag.version")
	}

	box, tag, version := filtered[0], filtered[1], filtered[2]

	if strings.TrimSpace(box) == "" {
		return nil, fmt.Errorf("Box cannot be empty")
	}
	if strings.TrimSpace(tag) == "" {
		return nil, fmt.Errorf("Tag cannot be empty")
	}
	if strings.TrimSpace(version) == "" {
		return nil, fmt.Errorf("Version cannot be empty")
	}

	return &KeyStore{Box: box, Tag: tag, Version: version}, nil
}

func (k *KeyStore) FullAddress() string {
	return k.Box + "." + k.Tag + "." + k.Version
}

// FromBase64Url decodes a base64url-encoded key and parses it.
func FromBase64Url(base64UrlKey string) (*KeyStore, error) {
	keyText, err := Base64UrlDecode(base64UrlKey)
	if err != nil {
		return nil, err
	}
	return NewKeyStore(keyText)
}

// Base64UrlDecode decodes a base64url string (URL-safe, no padding).
func Base64UrlDecode(text string) (string, error) {
	// Replace URL-safe characters back to standard base64
	text = strings.ReplaceAll(text, "-", "+")
	text = strings.ReplaceAll(text, "_", "/")

	// Add padding
	switch len(text) % 4 {
	case 2:
		text += "=="
	case 3:
		text += "="
	}

	decoded, err := base64.StdEncoding.DecodeString(text)
	if err != nil {
		return "", fmt.Errorf("invalid base64: %w", err)
	}
	return string(decoded), nil
}

// Base64UrlEncode encodes a string to base64url (URL-safe, no padding).
func Base64UrlEncode(input string) string {
	encoded := base64.StdEncoding.EncodeToString([]byte(input))
	encoded = strings.ReplaceAll(encoded, "+", "-")
	encoded = strings.ReplaceAll(encoded, "/", "_")
	encoded = strings.ReplaceAll(encoded, "=", "")
	return encoded
}
