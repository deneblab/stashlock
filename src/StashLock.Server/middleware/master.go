package middleware

import "crypto/subtle"

// MatchMasterKey compares a token with the configured one-time master key in constant time.
// An empty configured key never matches.
func MatchMasterKey(configured, token string) bool {
	if configured == "" {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(configured), []byte(token)) == 1
}
