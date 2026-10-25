package middleware

import (
	"net"
	"net/http"
	"strings"

	"github.com/rs/zerolog/log"
)

// NetworkAccess creates middleware that restricts /admin paths to allowed CIDR ranges.
// X-Forwarded-For is honoured only when the direct peer is one of trustedProxies.
func NetworkAccess(allowedNetworks, trustedProxies []string) func(http.Handler) http.Handler {
	cidrs := parseCIDRs(allowedNetworks, "AdminAllowedNetworks")
	proxies := parseCIDRs(trustedProxies, "TrustedProxies")

	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			// Only enforce for /admin paths
			if !strings.HasPrefix(r.URL.Path, "/admin") {
				next.ServeHTTP(w, r)
				return
			}

			// No networks configured — allow all
			if len(cidrs) == 0 {
				next.ServeHTTP(w, r)
				return
			}

			remoteIP := clientIP(r, proxies)
			if remoteIP == nil {
				log.Warn().Str("remoteAddr", r.RemoteAddr).Msg("Could not parse remote IP for admin access check")
				http.Error(w, "Forbidden", http.StatusForbidden)
				return
			}

			if containsIP(cidrs, remoteIP) {
				next.ServeHTTP(w, r)
				return
			}

			log.Warn().Str("ip", remoteIP.String()).Str("path", r.URL.Path).Msg("Admin access denied by network policy")
			http.Error(w, "Forbidden", http.StatusForbidden)
		})
	}
}

func parseCIDRs(networks []string, setting string) []*net.IPNet {
	var cidrs []*net.IPNet
	for _, n := range networks {
		_, cidr, err := net.ParseCIDR(n)
		if err != nil {
			log.Warn().Str("network", n).Str("setting", setting).Err(err).Msg("Invalid CIDR, skipping")
			continue
		}
		cidrs = append(cidrs, cidr)
	}
	return cidrs
}

func containsIP(cidrs []*net.IPNet, ip net.IP) bool {
	for _, c := range cidrs {
		if c.Contains(ip) {
			return true
		}
	}
	return false
}

// clientIP returns the direct peer address, or — when the peer is a trusted proxy —
// the right-most X-Forwarded-For entry that is not itself a trusted proxy.
func clientIP(r *http.Request, trustedProxies []*net.IPNet) net.IP {
	peer := remoteAddrIP(r)
	if peer == nil || !containsIP(trustedProxies, peer) {
		return peer
	}

	xff := r.Header.Get("X-Forwarded-For")
	if xff == "" {
		return peer
	}
	parts := strings.Split(xff, ",")
	for i := len(parts) - 1; i >= 0; i-- {
		ip := unmapIPv4(net.ParseIP(strings.TrimSpace(parts[i])))
		if ip == nil {
			return nil
		}
		if !containsIP(trustedProxies, ip) {
			return ip
		}
	}
	return peer
}

func remoteAddrIP(r *http.Request) net.IP {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return unmapIPv4(net.ParseIP(r.RemoteAddr))
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
