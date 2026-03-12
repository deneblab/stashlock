package middleware

import (
	"net"
	"net/http"
	"strings"

	"github.com/rs/zerolog/log"
)

// NetworkAccess creates middleware that restricts /admin paths to allowed CIDR ranges.
func NetworkAccess(allowedNetworks []string) func(http.Handler) http.Handler {
	var cidrs []*net.IPNet
	for _, n := range allowedNetworks {
		_, cidr, err := net.ParseCIDR(n)
		if err != nil {
			log.Warn().Str("network", n).Err(err).Msg("Invalid CIDR in AdminAllowedNetworks, skipping")
			continue
		}
		cidrs = append(cidrs, cidr)
	}

	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			// Only enforce for /admin paths
			if !strings.HasPrefix(r.URL.Path, "/admin") {
				next.ServeHTTP(w, r)
				return
			}

			// No networks configured — allow all (dev mode)
			if len(cidrs) == 0 {
				next.ServeHTTP(w, r)
				return
			}

			remoteIP := extractIP(r)
			if remoteIP == nil {
				log.Warn().Str("remoteAddr", r.RemoteAddr).Msg("Could not parse remote IP for admin access check")
				http.Error(w, "Forbidden", http.StatusForbidden)
				return
			}

			for _, cidr := range cidrs {
				if cidr.Contains(remoteIP) {
					next.ServeHTTP(w, r)
					return
				}
			}

			log.Warn().Str("ip", remoteIP.String()).Str("path", r.URL.Path).Msg("Admin access denied by network policy")
			http.Error(w, "Forbidden", http.StatusForbidden)
		})
	}
}

func extractIP(r *http.Request) net.IP {
	// Check X-Forwarded-For first
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		parts := strings.Split(xff, ",")
		ip := net.ParseIP(strings.TrimSpace(parts[0]))
		if ip != nil {
			return unmapIPv4(ip)
		}
	}

	// Fall back to RemoteAddr
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return net.ParseIP(r.RemoteAddr)
	}
	return unmapIPv4(net.ParseIP(host))
}

// unmapIPv4 converts IPv4-mapped IPv6 addresses to plain IPv4.
func unmapIPv4(ip net.IP) net.IP {
	if ip == nil {
		return nil
	}
	if v4 := ip.To4(); v4 != nil {
		return v4
	}
	return ip
}
