package daemon

import (
	"errors"
	"testing"

	"github.com/colitu/colitu-linux/headless/internal/api"
)

var testServers = []api.Server{
	{ID: "id-de-1", Name: "Frankfurt", Country: "DE", Status: "online", Load: "medium"},
	{ID: "id-tr-1", Name: "Istanbul", Country: "TR", Status: "online", Load: "high"},
	{ID: "id-tr-2", Name: "Ankara", Country: "TR", Status: "online", Load: "low"},
	{ID: "id-tr-3", Name: "Izmir", Country: "TR", Status: "offline", Load: "low"},
	{ID: "id-nl-1", Name: "Amsterdam", Country: "NL", Status: "online", Load: "low"},
}

func TestPickServer(t *testing.T) {
	cases := []struct {
		name, id, country string
		want              string // server id, or "" for an error
	}{
		{"best overall is lowest load, name breaks ties", "", "", "id-nl-1"},
		{"country picks lowest load online", "", "tr", "id-tr-2"},
		{"country is case-insensitive", "", "De", "id-de-1"},
		{"by id", "id-tr-1", "", "id-tr-1"},
		{"by id ignores case", "ID-TR-1", "", "id-tr-1"},
		{"by name", "istanbul", "", "id-tr-1"},
		{"offline server by id is refused", "id-tr-3", "", ""},
		{"unknown id", "nope", "", ""},
		{"unknown country", "", "ZZ", ""},
		{"both id and country", "id-tr-1", "TR", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got, err := PickServer(testServers, c.id, c.country)
			if c.want == "" {
				if err == nil {
					t.Fatalf("PickServer = %+v, want an error", got)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if got.ID != c.want {
				t.Errorf("PickServer = %s, want %s", got.ID, c.want)
			}
		})
	}
}

func TestPickServerNoMatchIsErrNoMatch(t *testing.T) {
	_, err := PickServer(nil, "", "")
	if !errors.Is(err, ErrNoMatch) {
		t.Errorf("err = %v, want ErrNoMatch", err)
	}
	_, err = PickServer(testServers, "", "ZZ")
	if !errors.Is(err, ErrNoMatch) {
		t.Errorf("err = %v, want ErrNoMatch", err)
	}
}
