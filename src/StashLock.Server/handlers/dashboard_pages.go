package handlers

import (
	"context"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/deneblab/stashlock-servergo/models"
	"github.com/deneblab/stashlock-servergo/store"
)

func contextWithAdmin(r *http.Request, admin *store.ApiKeyInfo) context.Context {
	return context.WithValue(r.Context(), adminCtxKey, admin)
}

func adminFrom(r *http.Request) *store.ApiKeyInfo {
	a, _ := r.Context().Value(adminCtxKey).(*store.ApiKeyInfo)
	return a
}

// pager holds paging numbers for list partials.
type pager struct {
	Total, Offset, End, PrevOffset, NextOffset int
	HasPrev, HasNext                           bool
}

func newPager(total, offset, limit, count int) pager {
	p := pager{Total: total, Offset: offset, End: offset + count}
	p.HasPrev = offset > 0
	p.PrevOffset = offset - limit
	if p.PrevOffset < 0 {
		p.PrevOffset = 0
	}
	p.HasNext = offset+count < total
	p.NextOffset = offset + limit
	return p
}

func queryInt(r *http.Request, name string) int {
	n, err := strconv.Atoi(r.URL.Query().Get(name))
	if err != nil || n < 0 {
		return 0
	}
	return n
}

// --- Overview ---

type overviewData struct {
	Stats          *store.DashboardStats
	Expiring       []store.ApiKeyInfo
	NeverUsed      []store.ApiKeyInfo
	FailedLogins   int
	AttentionCount int
	Recent         []store.AuditEntry
}

func (h *DashboardHandler) OverviewPage(w http.ResponseWriter, r *http.Request) {
	h.page(w, r, "index", "overview", nil)
}

func (h *DashboardHandler) OverviewPartial(w http.ResponseWriter, r *http.Request) {
	stats, err := h.storage.Stats()
	if err != nil {
		fail(w, err)
		return
	}
	d := overviewData{Stats: stats}
	if d.Expiring, err = h.apiKeyStore.ExpiringSoon(7 * 24 * time.Hour); err != nil {
		fail(w, err)
		return
	}
	if d.NeverUsed, err = h.apiKeyStore.NeverUsed(); err != nil {
		fail(w, err)
		return
	}
	if d.FailedLogins, err = h.auditStore.CountSince("login.failed", time.Now().Add(-24*time.Hour)); err != nil {
		fail(w, err)
		return
	}
	d.AttentionCount = len(d.Expiring) + len(d.NeverUsed)
	if d.FailedLogins > 0 {
		d.AttentionCount++
	}
	if d.Recent, _, err = h.auditStore.QueryPage(store.AuditFilter{Limit: 8}); err != nil {
		fail(w, err)
		return
	}
	h.partial(w, "overview", d)
}

// --- Boxes ---

const boxPageSize = 50

type boxesPageData struct {
	Boxes, Tags []string
}

type boxGroup struct {
	BoxTag  string
	Entries []models.KeyEntry
}

type boxListData struct {
	pager
	Groups   []boxGroup
	Filtered bool
}

func (h *DashboardHandler) BoxesPage(w http.ResponseWriter, r *http.Request) {
	boxes, tags, err := h.storage.BoxTags()
	if err != nil {
		fail(w, err)
		return
	}
	h.page(w, r, "boxes", "boxes", boxesPageData{Boxes: boxes, Tags: tags})
}

func (h *DashboardHandler) BoxList(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	opts := store.ListOptions{
		Box:            q.Get("box"),
		Tag:            q.Get("tag"),
		Prefix:         strings.TrimSpace(q.Get("q")),
		IncludeDeleted: q.Get("deleted") == "1",
		Limit:          boxPageSize,
		Offset:         queryInt(r, "offset"),
	}
	page, err := h.storage.ListFiltered(opts)
	if err != nil {
		fail(w, err)
		return
	}
	d := boxListData{
		pager:    newPager(page.Total, opts.Offset, boxPageSize, len(page.Entries)),
		Filtered: opts.Box != "" || opts.Tag != "" || opts.Prefix != "" || opts.IncludeDeleted,
	}
	for _, e := range page.Entries {
		bt := e.Box + "." + e.Tag
		if n := len(d.Groups); n == 0 || d.Groups[n-1].BoxTag != bt {
			d.Groups = append(d.Groups, boxGroup{BoxTag: bt})
		}
		d.Groups[len(d.Groups)-1].Entries = append(d.Groups[len(d.Groups)-1].Entries, e)
	}
	h.partial(w, "box_list", d)
}

func parseBoxKey(w http.ResponseWriter, r *http.Request) *models.KeyStore {
	ks, err := models.NewKeyStore(r.URL.Query().Get("key"))
	if err != nil {
		http.Error(w, "Invalid key: "+err.Error(), http.StatusBadRequest)
		return nil
	}
	return ks
}

type boxDetailData struct {
	Key, Box, Tag, Version string
	Metadata               *models.KeyMetadata
	History                []models.HistoryEntry
}

func (h *DashboardHandler) renderBoxDetail(w http.ResponseWriter, ks *models.KeyStore) {
	meta, err := h.storage.GetMetadata(ks)
	if err != nil {
		fail(w, err)
		return
	}
	if meta == nil {
		fail(w, models.NewStashLockError(models.ErrorCodes.KeyNotFound, "This entry no longer exists.", 404))
		return
	}
	history, err := h.storage.GetHistory(ks)
	if err != nil {
		fail(w, err)
		return
	}
	h.partial(w, "box_detail", boxDetailData{Key: ks.FullAddress(), Box: ks.Box, Tag: ks.Tag, Version: ks.Version, Metadata: meta, History: history})
}

func (h *DashboardHandler) BoxDetail(w http.ResponseWriter, r *http.Request) {
	if ks := parseBoxKey(w, r); ks != nil {
		h.renderBoxDetail(w, ks)
	}
}

func (h *DashboardHandler) BoxValue(w http.ResponseWriter, r *http.Request) {
	ks := parseBoxKey(w, r)
	if ks == nil {
		return
	}
	value, err := h.storage.Get(ks)
	if err != nil {
		fail(w, err)
		return
	}
	if value == "" {
		fail(w, models.NewStashLockError(models.ErrorCodes.KeyNotFound, "No current value: the entry is deleted or expired.", 404))
		return
	}
	h.audit(r, "get", ks.FullAddress(), "ok", "dashboard: show encrypted value")
	h.partial(w, "box_value", value)
}

type confirmData struct {
	Title, Body, Action, Confirm string
	Danger                       bool
}

func (h *DashboardHandler) BoxConfirmDelete(w http.ResponseWriter, r *http.Request) {
	ks := parseBoxKey(w, r)
	if ks == nil {
		return
	}
	key := ks.FullAddress()
	h.partial(w, "confirm", confirmData{
		Title:   "Delete " + key + "?",
		Body:    fmt.Sprintf("Apps reading %s.%s will get “not found” for this entry. This is a soft delete: you can restore it from “Show deleted”.", ks.Box, ks.Tag),
		Action:  "/admin/boxes/delete?key=" + url.QueryEscape(key),
		Confirm: "Delete entry",
		Danger:  true,
	})
}

func (h *DashboardHandler) BoxDelete(w http.ResponseWriter, r *http.Request) {
	ks := parseBoxKey(w, r)
	if ks == nil {
		return
	}
	if err := h.storage.Delete(ks); err != nil {
		h.audit(r, "delete", ks.FullAddress(), "error", err.Error())
		fail(w, err)
		return
	}
	h.audit(r, "delete", ks.FullAddress(), "ok", "dashboard")
	trigger(w, "Deleted "+ks.FullAddress(), true, true)
}

func (h *DashboardHandler) BoxRestore(w http.ResponseWriter, r *http.Request) {
	ks := parseBoxKey(w, r)
	if ks == nil {
		return
	}
	if err := h.storage.Restore(ks); err != nil {
		fail(w, err)
		return
	}
	h.audit(r, "restore", ks.FullAddress(), "ok", "dashboard")
	trigger(w, "Restored "+ks.FullAddress(), true, true)
}

func (h *DashboardHandler) BoxRestoreVersion(w http.ResponseWriter, r *http.Request) {
	ks := parseBoxKey(w, r)
	if ks == nil {
		return
	}
	ver, err := strconv.Atoi(r.URL.Query().Get("ver"))
	if err != nil || ver <= 0 {
		http.Error(w, "Invalid revision number", http.StatusBadRequest)
		return
	}
	if err := h.storage.RestoreVersion(ks, ver); err != nil {
		fail(w, err)
		return
	}
	h.audit(r, "restore", ks.FullAddress(), "ok", fmt.Sprintf("dashboard: revision #%d", ver))
	trigger(w, fmt.Sprintf("Revision #%d is now the current value", ver), false, true)
	h.renderBoxDetail(w, ks)
}

// --- Keys ---

type keysPageData struct{ Admin bool }

type keyListData struct {
	Admin          bool
	Keys           []store.ApiKeyInfo
	ActiveAdmins   int
	CurrentAdminId string
}

type keyFormData struct {
	Admin            bool
	Boxes, Tags      []string
	Tomorrow         string
	DefaultRateLimit int
}

func isAdminType(r *http.Request) bool { return r.URL.Query().Get("type") == "admin" }

func (h *DashboardHandler) KeysPage(w http.ResponseWriter, r *http.Request) {
	h.page(w, r, "keys", "keys", keysPageData{Admin: isAdminType(r)})
}

func (h *DashboardHandler) KeyList(w http.ResponseWriter, r *http.Request) {
	admin := isAdminType(r)
	keys, err := h.apiKeyStore.ListByType(admin)
	if err != nil {
		fail(w, err)
		return
	}
	d := keyListData{Admin: admin, Keys: keys}
	if a := adminFrom(r); a != nil {
		d.CurrentAdminId = a.Id
	}
	for _, k := range keys {
		if k.IsAdmin && k.IsActive {
			d.ActiveAdmins++
		}
	}
	h.partial(w, "key_list", d)
}

func (h *DashboardHandler) KeyForm(w http.ResponseWriter, r *http.Request) {
	boxes, tags, err := h.storage.BoxTags()
	if err != nil {
		fail(w, err)
		return
	}
	h.partial(w, "key_form", keyFormData{
		Admin: isAdminType(r), Boxes: boxes, Tags: tags,
		Tomorrow:         time.Now().UTC().Add(24 * time.Hour).Format("2006-01-02"),
		DefaultRateLimit: h.defaultRateLimit,
	})
}

func (h *DashboardHandler) KeyCreate(w http.ResponseWriter, r *http.Request) {
	canRead := r.FormValue("canRead") == "true"
	canWrite := r.FormValue("canWrite") == "true"
	canDelete := r.FormValue("canDelete") == "true"
	req := store.CreateApiKeyRequest{
		Name:      strings.TrimSpace(r.FormValue("name")),
		IsAdmin:   r.FormValue("isAdmin") == "true",
		Box:       strings.TrimSpace(r.FormValue("box")),
		Tag:       strings.TrimSpace(r.FormValue("tag")),
		CanRead:   &canRead,
		CanWrite:  &canWrite,
		CanDelete: &canDelete,
	}
	if !req.IsAdmin && !canRead && !canWrite && !canDelete {
		http.Error(w, "Choose at least one access: Read, Write or Delete.", http.StatusBadRequest)
		return
	}
	if v := strings.TrimSpace(r.FormValue("expiresAt")); v != "" {
		req.ExpiresAt = &v
	}
	if v := strings.TrimSpace(r.FormValue("rateLimitPerMinute")); v != "" {
		rl, err := strconv.Atoi(v)
		if err != nil || rl <= 0 {
			http.Error(w, "Rate limit must be a whole number above 0.", http.StatusBadRequest)
			return
		}
		req.RateLimitPerMinute = &rl
	}

	resp, err := h.apiKeyStore.Create(req)
	if err != nil {
		fail(w, err)
		return
	}
	h.audit(r, "key.create", "", "ok", keyDetail(resp.Info))
	trigger(w, "Key created", true, true)
	h.partial(w, "key_created", resp)
}

func keyDetail(k store.ApiKeyInfo) string {
	if k.IsAdmin {
		return fmt.Sprintf("admin key %q (%s…)", k.Name, k.KeyPrefix)
	}
	return fmt.Sprintf("API key %q (%s…) for %s.%s", k.Name, k.KeyPrefix, k.Box, k.Tag)
}

func (h *DashboardHandler) keyFromQuery(w http.ResponseWriter, r *http.Request) *store.ApiKeyInfo {
	k, err := h.apiKeyStore.Get(r.URL.Query().Get("id"))
	if err != nil {
		fail(w, err)
		return nil
	}
	if k == nil {
		fail(w, models.NewStashLockError(models.ErrorCodes.KeyNotFound, "This key no longer exists.", 404))
		return nil
	}
	return k
}

func (h *DashboardHandler) KeyConfirm(w http.ResponseWriter, r *http.Request) {
	k := h.keyFromQuery(w, r)
	if k == nil {
		return
	}
	kind := "API key"
	if k.IsAdmin {
		kind = "admin key"
	}
	d := confirmData{Danger: true}
	if r.URL.Query().Get("op") == "delete" {
		d.Title = "Delete " + kind + " “" + k.Name + "”?"
		d.Body = "The key is removed for good and disappears from this list. Audit entries keep its name."
		d.Action = "/admin/keys/delete?id=" + url.QueryEscape(k.Id)
		d.Confirm = "Delete key"
	} else {
		d.Title = "Revoke " + kind + " “" + k.Name + "”?"
		d.Body = "Anything using this key gets 401 Unauthorized from now on. A revoked key cannot be turned back on; create a new one instead."
		if k.IsAdmin {
			d.Body = "Sessions signed in with this key end immediately. A revoked key cannot be turned back on."
		}
		d.Action = "/admin/keys/revoke?id=" + url.QueryEscape(k.Id)
		d.Confirm = "Revoke key"
	}
	h.partial(w, "confirm", d)
}

func (h *DashboardHandler) KeyRevoke(w http.ResponseWriter, r *http.Request) {
	k := h.keyFromQuery(w, r)
	if k == nil {
		return
	}
	if err := h.apiKeyStore.Revoke(k.Id); err != nil {
		fail(w, err)
		return
	}
	h.audit(r, "key.revoke", "", "ok", keyDetail(*k))
	trigger(w, "Key revoked", true, true)
}

func (h *DashboardHandler) KeyDelete(w http.ResponseWriter, r *http.Request) {
	k := h.keyFromQuery(w, r)
	if k == nil {
		return
	}
	if err := h.apiKeyStore.Delete(k.Id); err != nil {
		fail(w, err)
		return
	}
	h.audit(r, "key.delete", "", "ok", keyDetail(*k))
	trigger(w, "Key deleted", true, true)
}

// --- Audit ---

const auditPageSize = 50

type actionGroup struct {
	Label   string
	Actions []string
}

var auditActionGroups = []actionGroup{
	{"Secrets", []string{"get", "set", "delete", "restore"}},
	{"Access", []string{"login", "login.failed", "master_key.exchange"}},
	{"Keys", []string{"key.create", "key.revoke", "key.delete"}},
}

type auditPageData struct {
	ActionGroups []actionGroup
	Action       string
}

type auditListData struct {
	pager
	Entries  []store.AuditEntry
	Filtered bool
}

func (h *DashboardHandler) AuditPage(w http.ResponseWriter, r *http.Request) {
	h.page(w, r, "audit", "audit", auditPageData{ActionGroups: auditActionGroups, Action: r.URL.Query().Get("action")})
}

func (h *DashboardHandler) AuditList(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	f := store.AuditFilter{
		Action: q.Get("action"),
		BoxKey: strings.TrimSpace(q.Get("box")),
		Key:    strings.TrimSpace(q.Get("key")),
		From:   q.Get("from"),
		To:     q.Get("to"),
		Limit:  auditPageSize,
		Offset: queryInt(r, "offset"),
	}
	entries, total, err := h.auditStore.QueryPage(f)
	if err != nil {
		fail(w, err)
		return
	}
	h.partial(w, "audit_list", auditListData{
		pager:    newPager(total, f.Offset, auditPageSize, len(entries)),
		Entries:  entries,
		Filtered: f.Action != "" || f.BoxKey != "" || f.Key != "" || f.From != "" || f.To != "",
	})
}
