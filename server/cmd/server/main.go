package main

import (
	"context"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"github.com/mcc/sts2-spire-race/server/internal/auth"
	"github.com/mcc/sts2-spire-race/server/internal/config"
	"github.com/mcc/sts2-spire-race/server/internal/httpapi"
	"github.com/mcc/sts2-spire-race/server/internal/integrity"
	"github.com/mcc/sts2-spire-race/server/internal/matchmaking"
	"github.com/mcc/sts2-spire-race/server/internal/storage"
)

func main() {
	if len(os.Args) > 1 && os.Args[1] == "--healthcheck" {
		client := http.Client{Timeout: 2 * time.Second}
		resp, err := client.Get("http://127.0.0.1:8080/health")
		if err != nil || resp.StatusCode != http.StatusOK {
			os.Exit(1)
		}
		_ = resp.Body.Close()
		return
	}
	cfg := config.Load()
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	store, err := storage.Open(ctx, cfg.DatabaseURL)
	if err != nil {
		slog.Error("connect postgres", "error", err)
		os.Exit(1)
	}
	defer store.Close()
	if err := migrate(ctx, store, cfg); err != nil {
		slog.Error("migrate postgres", "error", err)
		os.Exit(1)
	}
	queue, err := matchmaking.Open(ctx, cfg.RedisURL)
	if err != nil {
		slog.Error("connect redis", "error", err)
		os.Exit(1)
	}
	defer queue.Close()
	tokens := auth.NewManager(cfg.TokenSecret)
	api := httpapi.New(tokens, auth.SteamVerifier{APIKey: cfg.SteamAPIKey, AppID: cfg.SteamAppID, AllowDev: cfg.AllowDevAuth && !cfg.OfficialServer},
		store, queue, integrity.Service{Directory: cfg.IntegrityDir, Secret: []byte(cfg.TokenSecret)}, cfg.OfficialServer, cfg.SteamAllowlist)
	server := &http.Server{Addr: cfg.Address, Handler: requestLog(api.Mux), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 15 * time.Second, WriteTimeout: 15 * time.Second, IdleTimeout: 60 * time.Second}
	go func() {
		slog.Info("spire race server listening", "address", cfg.Address)
		if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			slog.Error("http server", "error", err)
			cancel()
		}
	}()
	<-ctx.Done()
	shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer shutdownCancel()
	_ = server.Shutdown(shutdownCtx)
}

func migrate(ctx context.Context, store *storage.Postgres, cfg config.Config) error {
	dir := os.Getenv("MIGRATIONS_DIR")
	if dir == "" {
		dir = "./migrations"
	}
	files, err := filepath.Glob(filepath.Join(dir, "*.sql"))
	if err != nil {
		return err
	}
	for _, file := range files {
		sql, err := os.ReadFile(file)
		if err != nil {
			return err
		}
		if _, err = store.Pool.Exec(ctx, string(sql)); err != nil {
			return err
		}
	}
	return nil
}

func requestLog(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		slog.Info("request", "method", r.Method, "path", r.URL.Path, "duration", time.Since(start))
	})
}
