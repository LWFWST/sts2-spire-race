package integrity

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"sort"
)

type File struct {
	Path   string `json:"path"`
	SHA256 string `json:"sha256"`
	Size   int64  `json:"size"`
}
type Manifest struct {
	GameVersion     string   `json:"game_version"`
	ManifestVersion string   `json:"manifest_version"`
	GameFiles       []File   `json:"game_files"`
	AllowedModFiles []File   `json:"allowed_mod_files"`
	AllowedModIDs   []string `json:"allowed_mod_ids"`
	Signature       string   `json:"signature"`
}
type Attestation struct {
	GameVersion    string   `json:"game_version"`
	Files          []File   `json:"files"`
	LoadedModIDs   []string `json:"loaded_mod_ids"`
	ChallengeNonce string   `json:"challenge_nonce"`
}
type Verdict struct {
	Accepted bool   `json:"accepted"`
	Code     string `json:"code"`
	Detail   string `json:"detail"`
}

type Service struct {
	Directory string
	Secret    []byte
}

func (s Service) Manifest(version string) (Manifest, error) {
	data, err := os.ReadFile(filepath.Join(s.Directory, version+".json"))
	if err != nil {
		return Manifest{}, err
	}
	var m Manifest
	if err := json.Unmarshal(data, &m); err != nil {
		return Manifest{}, err
	}
	if m.GameVersion != version {
		return Manifest{}, errors.New("manifest version mismatch")
	}
	expected, err := Sign(m, s.Secret)
	if err != nil {
		return Manifest{}, err
	}
	if !hmac.Equal([]byte(expected), []byte(m.Signature)) {
		return Manifest{}, errors.New("manifest signature is invalid")
	}
	return m, nil
}

func (s Service) Verify(_ context.Context, a Attestation) (Verdict, error) {
	m, err := s.Manifest(a.GameVersion)
	if err != nil {
		return Verdict{false, "unsupported_version", err.Error()}, nil
	}
	expected := map[string]File{}
	for _, f := range append(append([]File{}, m.GameFiles...), m.AllowedModFiles...) {
		expected[clean(f.Path)] = f
	}
	for _, f := range a.Files {
		e, ok := expected[clean(f.Path)]
		if !ok || !hmac.Equal([]byte(e.SHA256), []byte(f.SHA256)) || e.Size != f.Size {
			return Verdict{false, "modified_file", f.Path}, nil
		}
		delete(expected, clean(f.Path))
	}
	if len(expected) > 0 {
		return Verdict{false, "missing_file", "required integrity file was not reported"}, nil
	}
	allowed := map[string]bool{}
	for _, id := range m.AllowedModIDs {
		allowed[id] = true
	}
	for _, id := range a.LoadedModIDs {
		if !allowed[id] {
			return Verdict{false, "unsupported_mod", id}, nil
		}
	}
	return Verdict{true, "accepted", ""}, nil
}

func Sign(m Manifest, secret []byte) (string, error) {
	m.Signature = ""
	sort.Slice(m.GameFiles, func(i, j int) bool { return m.GameFiles[i].Path < m.GameFiles[j].Path })
	sort.Slice(m.AllowedModFiles, func(i, j int) bool { return m.AllowedModFiles[i].Path < m.AllowedModFiles[j].Path })
	payload, err := json.Marshal(m)
	if err != nil {
		return "", err
	}
	mac := hmac.New(sha256.New, secret)
	_, _ = mac.Write(payload)
	return hex.EncodeToString(mac.Sum(nil)), nil
}
func clean(v string) string { return filepath.ToSlash(filepath.Clean(v)) }
