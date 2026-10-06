package daemon

import (
	"errors"
	"fmt"
	"sort"
	"strings"

	"github.com/colitu/colitu-linux/headless/internal/api"
)

// ErrNoMatch means no server fits the request.
var ErrNoMatch = errors.New("no matching server")

func loadRank(load string) int {
	switch strings.ToLower(load) {
	case "low":
		return 0
	case "medium":
		return 1
	case "high":
		return 2
	}
	return 3
}

// PickServer chooses the server of a connect request: an exact id or name,
// or the lowest-load online server of a country, or (both empty) of all.
func PickServer(servers []api.Server, id, country string) (api.Server, error) {
	id = strings.TrimSpace(id)
	country = strings.TrimSpace(country)
	if id != "" && country != "" {
		return api.Server{}, errors.New("choose either a server or a country, not both")
	}
	if id != "" {
		for _, s := range servers {
			if strings.EqualFold(s.ID, id) || strings.EqualFold(s.Name, id) {
				if !s.Online() {
					return api.Server{}, fmt.Errorf("%w: %s is offline", ErrNoMatch, s.Name)
				}
				return s, nil
			}
		}
		return api.Server{}, fmt.Errorf("%w: no server with id or name %q", ErrNoMatch, id)
	}
	var pool []api.Server
	for _, s := range servers {
		if !s.Online() {
			continue
		}
		if country != "" && !strings.EqualFold(s.Country, country) {
			continue
		}
		pool = append(pool, s)
	}
	if len(pool) == 0 {
		if country != "" {
			return api.Server{}, fmt.Errorf("%w: no online server in %q", ErrNoMatch, strings.ToUpper(country))
		}
		return api.Server{}, fmt.Errorf("%w: no server is online", ErrNoMatch)
	}
	sort.SliceStable(pool, func(i, j int) bool {
		ri, rj := loadRank(pool[i].Load), loadRank(pool[j].Load)
		if ri != rj {
			return ri < rj
		}
		if pool[i].Name != pool[j].Name {
			return pool[i].Name < pool[j].Name
		}
		return pool[i].ID < pool[j].ID
	})
	return pool[0], nil
}
