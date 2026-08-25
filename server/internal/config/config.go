package config

import (
	"os"
	"strconv"
	"strings"
)

type Config struct {
	Address        string
	DatabaseURL    string
	RedisURL       string
	TokenSecret    string
	SteamAPIKey    string
	SteamAppID     string
	AllowDevAuth   bool
	IntegrityDir   string
	OfficialServer bool
	SteamAllowlist map[string]bool
}

func Load() Config {
	return Config{
		Address:        value("RACE_ADDRESS", ":8080"),
		DatabaseURL:    value("DATABASE_URL", "postgres://spire_race:spire_race@localhost:5432/spire_race?sslmode=disable"),
		RedisURL:       value("REDIS_URL", "redis://localhost:6379/0"),
		TokenSecret:    value("TOKEN_SECRET", "development-secret-change-me"),
		SteamAPIKey:    os.Getenv("STEAM_WEB_API_KEY"),
		SteamAppID:     value("STEAM_APP_ID", "2868840"),
		AllowDevAuth:   boolean("ALLOW_DEV_AUTH", false),
		IntegrityDir:   value("INTEGRITY_DIR", "./config/integrity"),
		OfficialServer: boolean("OFFICIAL_SERVER", false),
		SteamAllowlist: allowlist(os.Getenv("STEAM_ALLOWLIST")),
	}
}

func allowlist(value string) map[string]bool {
	result := map[string]bool{}
	for _, item := range strings.Split(value, ",") {
		if id := strings.TrimSpace(item); id != "" {
			result[id] = true
		}
	}
	return result
}

func value(name, fallback string) string {
	if v := os.Getenv(name); v != "" {
		return v
	}
	return fallback
}
func boolean(name string, fallback bool) bool {
	v := os.Getenv(name)
	if v == "" {
		return fallback
	}
	b, err := strconv.ParseBool(v)
	if err != nil {
		return fallback
	}
	return b
}
