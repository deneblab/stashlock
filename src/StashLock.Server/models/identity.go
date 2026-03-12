package models

import "strings"

// ApiKeyIdentity represents the authenticated principal for a request.
type ApiKeyIdentity struct {
	Id                 string   `json:"id"`
	Name               string   `json:"name"`
	IsMaster           bool     `json:"isMaster"`
	Scopes             []string `json:"scopes"`
	CanRead            bool     `json:"canRead"`
	CanWrite           bool     `json:"canWrite"`
	CanDelete          bool     `json:"canDelete"`
	RateLimitPerMinute *int     `json:"rateLimitPerMinute,omitempty"`
}

// HasScopeFor checks whether this identity has access to the given box name.
func (id *ApiKeyIdentity) HasScopeFor(boxName string) bool {
	if id.IsMaster {
		return true
	}
	for _, s := range id.Scopes {
		if s == "*" || strings.EqualFold(s, boxName) {
			return true
		}
	}
	return false
}
