#!/usr/bin/env bash
set -euo pipefail

release_dir=""
root_dir="/opt/sts2-spire-race"
compose_project="spire-race"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --release-dir) release_dir="$2"; shift 2 ;;
    --root-dir) root_dir="$2"; shift 2 ;;
    --project) compose_project="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$release_dir" ]]; then
  release_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fi

shared_dir="$root_dir/shared"
env_file="$shared_dir/.env.production"
tls_dir="$shared_dir/tls"
compose_file="$release_dir/deploy/docker-compose.prod.yml"
backup_dir="$root_dir/backups"

command -v docker >/dev/null 2>&1 || { echo "Docker is required." >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "Docker Compose v2 is required." >&2; exit 1; }
[[ -f "$compose_file" ]] || { echo "Missing $compose_file" >&2; exit 1; }
[[ -f "$env_file" ]] || { echo "Missing $env_file" >&2; exit 1; }
[[ -f "$tls_dir/spirerace.xyz_bundle.crt" ]] || { echo "Missing TLS certificate." >&2; exit 1; }
[[ -f "$tls_dir/spirerace.xyz.key" ]] || { echo "Missing TLS private key." >&2; exit 1; }

mkdir -p "$backup_dir" "$release_dir/deploy/tls"
install -m 0644 "$tls_dir/spirerace.xyz_bundle.crt" "$release_dir/deploy/tls/spirerace.xyz_bundle.crt"
install -m 0600 "$tls_dir/spirerace.xyz.key" "$release_dir/deploy/tls/spirerace.xyz.key"

compose=(docker compose -p "$compose_project" --env-file "$env_file" -f "$compose_file")
if "${compose[@]}" ps --status running --services 2>/dev/null | grep -qx postgres; then
  backup_file="$backup_dir/spire_race_$(date -u +%Y%m%dT%H%M%SZ).sql.gz"
  echo "Backing up PostgreSQL to $backup_file"
  "${compose[@]}" exec -T postgres pg_dump -U spire_race -d spire_race | gzip -9 > "$backup_file"
fi

for legacy_service in spire-race.service spire-race-server.service; do
  if systemctl list-unit-files "$legacy_service" --no-legend 2>/dev/null | grep -q "$legacy_service"; then
    systemctl disable --now "$legacy_service"
  fi
done

echo "Building and starting Spire Race"
"${compose[@]}" up -d --build --remove-orphans

for attempt in $(seq 1 30); do
  if "${compose[@]}" exec -T server /app/spire-race-server --healthcheck >/dev/null 2>&1; then
    ln -sfn "$release_dir" "$root_dir/current"
    "${compose[@]}" ps
    echo "Deployment completed: $release_dir"
    exit 0
  fi
  sleep 2
done

"${compose[@]}" logs --tail=200 server nginx >&2 || true
echo "Deployment failed health checks; current symlink was not changed." >&2
exit 1
