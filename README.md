# Spire Race / 尖塔竞速

An in-game, native-style competitive speedrun client and Go race server for Slay the Spire 2.

## Included UI

- Main-menu entry integrated below Multiplayer.
- Casual and ranked 1v1, 2v2, 3v3, and 4v4 flows.
- Version-isolated casual/ranked matchmaking, ready checks, millisecond server clock, surrender and result states.
- Certified finish/highest-floor adjudication, death restart, event/combat SL budgets, Elo and visible ranked points.
- Legend solo BO3 draft with persistent slot-one bans and game-one-only slot-two bans.
- Entertainment rooms joined by six-character code; A0-A10 and 0-9 SL customization, host-controlled start, per-player ready/character state, and original Steam P2P gameplay.
- Steam local identity with deterministic demo profiles, friends, leaderboards, titles, and events.
- Simplified Chinese and English UI.

The default client connects to `https://spirerace.xyz/`. A custom/self-hosted URL can be selected in Settings or set with `SPIRE_RACE_SERVER_URL`. `--spire-race-demo` enables the non-persistent demo flow. `--spire-race-dev-auth` is accepted only by a self-hosted server configured with `ALLOW_DEV_AUTH=true`.

Solo assignments launch a real server-seeded run. Team assignments elect one Steam host per team, exchange only Steam lobby identifiers through the race WebSocket, and then use the original game's Steam transport for all gameplay. The Go service never proxies gameplay packets. Direct entertainment P2P opens the original Steam custom-run lobby without creating an official race record; server-coordinated entertainment creates two independent original Steam cooperative rooms.

## Build and install

Run `tools/package.ps1`, then copy `dist/package/sts2-spire-race` into the game's `mods` directory. The same command creates `dist/server`.

For local backend development, run `docker compose up --build`. Production uses `deploy/docker-compose.prod.yml`, requires `TOKEN_SECRET`, `STEAM_WEB_API_KEY`, `POSTGRES_PASSWORD`, and `STEAM_ALLOWLIST`, and terminates TLS in Nginx. Generate exact integrity manifests with `go run ./cmd/integrity-manifest` from the `server` directory.

Official access requires a valid Steam session ticket and an allowlisted SteamID. Self-hosted instances can disable official mode. Credentials, Steam tickets, TLS keys, integrity secrets, and game files are intentionally excluded from this repository.

## Development checks

- `dotnet test tests/sts2-spire-race.Tests.csproj -c Release`
- `tools/check-api-compat.ps1`
- `cd server && go test ./...`
- `docker compose config`
