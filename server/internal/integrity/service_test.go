package integrity

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestVerifyIgnoresPhysicalPaths(t *testing.T) {
	service, manifest := testService(t)
	attestation := Attestation{
		GameVersion: manifest.GameVersion,
		Files: []File{
			{Path: `D:/SteamLibrary/steamapps/common/Slay the Spire 2/SlayTheSpire2.exe`, SHA256: "game-hash", Size: 100},
			{Path: `E:/SteamLibrary/steamapps/workshop/content/2868840/123456/sts2-spire-race.dll`, SHA256: "mod-hash", Size: 200},
		},
		LoadedModIDs: []string{"sts2-spire-race"},
	}

	verdict, err := service.Verify(context.Background(), attestation)
	if err != nil {
		t.Fatal(err)
	}
	if !verdict.Accepted {
		t.Fatalf("expected path-independent verification, got %+v", verdict)
	}
}

func TestVerifyStillRejectsWrongHashMissingAndDuplicateFiles(t *testing.T) {
	service, manifest := testService(t)
	tests := []struct {
		name  string
		files []File
		code  string
	}{
		{"wrong hash", []File{{Path: "anything", SHA256: "wrong", Size: 100}, {Path: "anything-else", SHA256: "mod-hash", Size: 200}}, "modified_file"},
		{"missing", []File{{Path: "anything", SHA256: "game-hash", Size: 100}}, "missing_file"},
		{"duplicate", []File{{Path: "a", SHA256: "game-hash", Size: 100}, {Path: "b", SHA256: "game-hash", Size: 100}}, "modified_file"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			verdict, err := service.Verify(context.Background(), Attestation{
				GameVersion:  manifest.GameVersion,
				Files:        test.files,
				LoadedModIDs: []string{"sts2-spire-race"},
			})
			if err != nil {
				t.Fatal(err)
			}
			if verdict.Accepted || verdict.Code != test.code {
				t.Fatalf("expected %s, got %+v", test.code, verdict)
			}
		})
	}
}

func TestVerifyStillRejectsUnsupportedMod(t *testing.T) {
	service, manifest := testService(t)
	verdict, err := service.Verify(context.Background(), Attestation{
		GameVersion:  manifest.GameVersion,
		Files:        append(append([]File{}, manifest.GameFiles...), manifest.AllowedModFiles...),
		LoadedModIDs: []string{"sts2-spire-race", "unapproved-mod"},
	})
	if err != nil {
		t.Fatal(err)
	}
	if verdict.Accepted || verdict.Code != "unsupported_mod" {
		t.Fatalf("expected unsupported_mod, got %+v", verdict)
	}
}

func testService(t *testing.T) (Service, Manifest) {
	t.Helper()
	secret := []byte("test-integrity-secret")
	manifest := Manifest{
		GameVersion:     "v0.test.0",
		ManifestVersion: "sha256-2-path-independent",
		GameFiles:       []File{{Path: "SlayTheSpire2.exe", SHA256: "game-hash", Size: 100}},
		AllowedModFiles: []File{{Path: "mods/sts2-spire-race/sts2-spire-race.dll", SHA256: "mod-hash", Size: 200}},
		AllowedModIDs:   []string{"sts2-spire-race"},
	}
	var err error
	manifest.Signature, err = Sign(manifest, secret)
	if err != nil {
		t.Fatal(err)
	}
	directory := t.TempDir()
	encoded, err := json.Marshal(manifest)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(directory, manifest.GameVersion+".json"), encoded, 0600); err != nil {
		t.Fatal(err)
	}
	return Service{Directory: directory, Secret: secret}, manifest
}
