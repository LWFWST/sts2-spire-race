# Spire Race / 尖塔竞速

An in-game, native-style competitive speedrun client and Go race server for Slay the Spire 2.

## Included UI

- Main-menu entry integrated below Multiplayer.
- Casual and ranked 1v1, 2v2, 3v3, and 4v4 flows.
- Version-isolated casual/ranked matchmaking, ready checks, millisecond server clock, surrender and result states.
- Certified finish/highest-floor adjudication, death restart, event/combat SL budgets, Elo and visible ranked points.
- Legend solo BO3 draft with persistent slot-one bans and game-one-only slot-two bans.
- Entertainment rooms with A0-A10 and 0-9 SL customization, host-controlled start, per-player ready/character state, Steam friend invitations, and optional server coordination.
- Steam local identity with deterministic demo profiles, friends, leaderboards, titles, and events.
- Simplified Chinese and English UI.

The default client starts disconnected, so Steam P2P friend races remain available without any race server. The official `https://spirerace.xyz/` service or a custom/self-hosted URL can be selected explicitly in Settings or set with `SPIRE_RACE_SERVER_URL`. `--spire-race-demo` enables the non-persistent demo flow. `--spire-race-dev-auth` is accepted only by a self-hosted server configured with `ALLOW_DEV_AUTH=true`.

Solo assignments launch a real shared-seed race. Team assignments elect one Steam host per team and use the original game's Steam transport for all gameplay; the Go service never proxies gameplay packets. A direct entertainment P2P room is a Steam coordination lobby, not one shared cooperative run: 1v1 launches two independent same-seed, same-character runs, while 2v2-4v4 creates one original Steam cooperative lobby for each team and races those two independent teams. Lobby member data carries ready state, team choice, progress and the casual settlement. Direct P2P creates no official record and deliberately does not use the official whitelist, integrity checks, Elo or ranked points.

## Build and install

Run `tools/package.ps1`, then copy `dist/package/sts2-spire-race` into the game's `mods` directory. The same command creates `dist/server`.

For local backend development, run `docker compose up --build`. Production uses `deploy/docker-compose.prod.yml`, requires `TOKEN_SECRET`, `STEAM_WEB_API_KEY`, `POSTGRES_PASSWORD`, and `STEAM_ALLOWLIST`, and terminates TLS in Nginx. Generate exact integrity manifests with `go run ./cmd/integrity-manifest` from the `server` directory; provide the signing key through the `TOKEN_SECRET` environment variable so it is not exposed in process arguments.

For a production upload from Windows, prepare the shared production environment file and run `tools/deploy-server.ps1`. The wrapper uploads a versioned source archive and TLS files, then invokes `deploy/deploy-server.sh` on Ubuntu. The server script backs up a running PostgreSQL database, disables legacy systemd race services, starts the fixed `spire-race` Compose project, and changes the `current` symlink only after the health check passes. Password authentication is prompted interactively and is never stored by the script.

Production maintenance helpers keep credentials outside Git. `tools/update-steam-allowlist.ps1` validates SteamID64 values and supports additive, removal, or full replacement updates; pass `-ApplyRemote` to recreate only the Go server container. `tools/update-integrity-hashes.ps1` hashes the selected retail build and Mod files, signs the result using `TOKEN_SECRET` from the production environment file, rejects a mismatched game version, and optionally performs the normal production deployment with `-Deploy`. Both scripts avoid printing IDs, tokens, or environment contents.

```powershell
# Add one or more testers, then atomically update only the production server container.
.\tools\update-steam-allowlist.ps1 `
  -ProductionEnvFile C:\secure\spire-race-production.env `
  -SteamId 76561198000000001,76561198000000002 `
  -Mode Add -ApplyRemote `
  -ServerHost 134.122.116.15

# Build the Mod, sign the exact retail/Mod hashes, and run the normal production deployment.
.\tools\update-integrity-hashes.ps1 `
  -ProductionEnvFile C:\secure\spire-race-production.env `
  -GameVersion v0.111.0 -BuildMod -Deploy `
  -ServerHost 134.122.116.15
```

With no `-SshKeyPath`, SSH and SCP use normal interactive password authentication. Add `-SshKeyPath` only after that public key has been installed on the server.

Official access requires a valid Steam session ticket and an allowlisted SteamID. Integrity verification resolves Mod files through the original game's loaded `Mod.path`, so local and Steam Workshop installations share the same signed hashes even when their physical directories or Steam libraries differ. The server deliberately ignores submitted paths while still requiring the exact SHA-256, size, file count, and loaded-Mod ID set. Self-hosted instances can disable official mode. Credentials, Steam tickets, TLS keys, integrity secrets, and game files are intentionally excluded from this repository.

## Development checks

- `dotnet test tests/sts2-spire-race.Tests.csproj -c Release`
- `tools/check-api-compat.ps1`
- `cd server && go test ./...`
- `docker compose config`
