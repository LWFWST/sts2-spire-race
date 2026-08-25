package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	"github.com/mcc/sts2-spire-race/server/internal/integrity"
)

func main() {
	version := flag.String("version", "", "exact game version, such as v0.111.0")
	gameDir := flag.String("game-dir", "", "Slay the Spire 2 installation directory")
	modDLL := flag.String("mod-dll", "", "packaged sts2-spire-race.dll")
	modManifest := flag.String("mod-manifest", "", "packaged manifest.json")
	secret := flag.String("secret", "", "deployment manifest signing secret")
	output := flag.String("output", "", "manifest output path")
	flag.Parse()
	if *version == "" || *gameDir == "" || *modDLL == "" || *modManifest == "" || *secret == "" || *output == "" {
		flag.Usage()
		os.Exit(2)
	}
	gameFiles := []string{"SlayTheSpire2.exe", "SlayTheSpire2.pck", filepath.Join("data_sts2_windows_x86_64", "sts2.dll")}
	m := integrity.Manifest{GameVersion: *version, ManifestVersion: "sha256-1", AllowedModIDs: []string{"sts2-spire-race"}}
	for _, relative := range gameFiles {
		file, err := hashFile(filepath.Join(*gameDir, relative), filepath.ToSlash(relative))
		if err != nil {
			fatal(err)
		}
		m.GameFiles = append(m.GameFiles, file)
	}
	for _, entry := range []struct{ source, relative string }{{*modDLL, "mods/sts2-spire-race/sts2-spire-race.dll"}, {*modManifest, "mods/sts2-spire-race/manifest.json"}} {
		file, err := hashFile(entry.source, entry.relative)
		if err != nil {
			fatal(err)
		}
		m.AllowedModFiles = append(m.AllowedModFiles, file)
	}
	signature, err := integrity.Sign(m, []byte(*secret))
	if err != nil {
		fatal(err)
	}
	m.Signature = signature
	data, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		fatal(err)
	}
	data = append(data, '\n')
	if err := os.WriteFile(*output, data, 0644); err != nil {
		fatal(err)
	}
	fmt.Println("integrity manifest written:", *output)
}

func hashFile(path, relative string) (integrity.File, error) {
	f, err := os.Open(path)
	if err != nil {
		return integrity.File{}, err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return integrity.File{}, err
	}
	h := sha256.New()
	if _, err = io.Copy(h, f); err != nil {
		return integrity.File{}, err
	}
	return integrity.File{Path: strings.ReplaceAll(relative, "\\", "/"), SHA256: hex.EncodeToString(h.Sum(nil)), Size: info.Size()}, nil
}
func fatal(err error) { fmt.Fprintln(os.Stderr, err); os.Exit(1) }
