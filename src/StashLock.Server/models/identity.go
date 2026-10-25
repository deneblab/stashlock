package models

// ApiKeyIdentity represents the authenticated principal for a request.
// Master identities administer the server; API key identities are bound to one box.tag.
type ApiKeyIdentity struct {
	Id                 string `json:"id"`
	Name               string `json:"name"`
	IsMaster           bool   `json:"isMaster"`
	Box                string `json:"box,omitempty"`
	Tag                string `json:"tag,omitempty"`
	CanRead            bool   `json:"canRead"`
	CanWrite           bool   `json:"canWrite"`
	CanDelete          bool   `json:"canDelete"`
	RateLimitPerMinute *int   `json:"rateLimitPerMinute,omitempty"`
}

// CanAccess reports whether this API key identity may access the given key.
// Master identities never access secrets directly.
func (id *ApiKeyIdentity) CanAccess(key *KeyStore) bool {
	if id.IsMaster {
		return false
	}
	return id.Box == key.Box && id.Tag == key.Tag
}

// KeyPrefix returns the "box.tag." prefix this identity is bound to.
func (id *ApiKeyIdentity) KeyPrefix() string {
	return id.Box + "." + id.Tag + "."
}
