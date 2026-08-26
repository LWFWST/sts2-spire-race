[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProductionEnvFile,

    [Parameter(Mandatory = $true)]
    [string[]]$SteamId,

    [ValidateSet('Replace', 'Add', 'Remove')]
    [string]$Mode = 'Add',

    [switch]$AllowEmpty,
    [switch]$ApplyRemote,
    [string]$ServerHost = '134.122.116.15',
    [string]$ServerUser = 'root',
    [ValidateRange(1, 65535)]
    [int]$SshPort = 22,
    [string]$SshKeyPath = '',
    [string]$RemoteRoot = '/opt/sts2-spire-race'
)

$ErrorActionPreference = 'Stop'

function Assert-SteamId64([string]$Value) {
    [System.UInt64]$parsed = 0
    $candidate = $Value.Trim()
    if ($candidate -notmatch '^\d{17}$' -or -not [System.UInt64]::TryParse($candidate, [ref]$parsed)) {
        throw 'Every Steam ID must be a 17-digit SteamID64 value.'
    }
    return $candidate
}

function Get-SshOptions {
    $options = @('-o', 'StrictHostKeyChecking=accept-new', '-p', $SshPort.ToString())
    if ($SshKeyPath) {
        $options += @('-i', (Resolve-Path -LiteralPath $SshKeyPath).Path)
    }
    return $options
}

function Get-ScpOptions {
    $options = @('-o', 'StrictHostKeyChecking=accept-new', '-P', $SshPort.ToString())
    if ($SshKeyPath) {
        $options += @('-i', (Resolve-Path -LiteralPath $SshKeyPath).Path)
    }
    return $options
}

$resolvedEnv = (Resolve-Path -LiteralPath $ProductionEnvFile).Path
$original = [System.IO.File]::ReadAllText($resolvedEnv)
$lineMatches = [regex]::Matches($original, '(?m)^STEAM_ALLOWLIST=[^\r\n]*')
if ($lineMatches.Count -gt 1) {
    throw 'The production environment file contains more than one STEAM_ALLOWLIST entry.'
}

$requested = @($SteamId | ForEach-Object { Assert-SteamId64 $_ } | Select-Object -Unique)
$current = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
if ($lineMatches.Count -eq 1) {
    $currentValue = $lineMatches[0].Value.Substring('STEAM_ALLOWLIST='.Length)
    foreach ($entry in $currentValue.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries)) {
        [void]$current.Add((Assert-SteamId64 $entry))
    }
}

switch ($Mode) {
    'Replace' {
        $current.Clear()
        foreach ($entry in $requested) { [void]$current.Add($entry) }
    }
    'Add' {
        foreach ($entry in $requested) { [void]$current.Add($entry) }
    }
    'Remove' {
        foreach ($entry in $requested) { [void]$current.Remove($entry) }
    }
}

if ($current.Count -eq 0 -and -not $AllowEmpty) {
    throw 'The resulting allowlist is empty. Pass -AllowEmpty if this is intentional.'
}

$newEntry = 'STEAM_ALLOWLIST=' + (($current | Sort-Object) -join ',')
if ($lineMatches.Count -eq 1) {
    $updated = $original.Substring(0, $lineMatches[0].Index) + $newEntry +
        $original.Substring($lineMatches[0].Index + $lineMatches[0].Length)
} else {
    $newline = if ($original.Contains("`r`n")) { "`r`n" } else { "`n" }
    $updated = $original.TrimEnd("`r", "`n") + $newline + $newEntry + $newline
}

$localReady = $updated -eq $original
if ($updated -ne $original) {
    if ($PSCmdlet.ShouldProcess($resolvedEnv, "Update Steam allowlist to $($current.Count) entries")) {
        $temporary = Join-Path (Split-Path -Parent $resolvedEnv) ('.steam-allowlist-' + [guid]::NewGuid().ToString('N') + '.tmp')
        try {
            [System.IO.File]::WriteAllText($temporary, $updated, [System.Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath $temporary -Destination $resolvedEnv -Force
            $localReady = $true
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        }
        Write-Host "Updated the local Steam allowlist ($($current.Count) entries)."
    }
} else {
    Write-Host "The local Steam allowlist is already current ($($current.Count) entries)."
}

if (-not $ApplyRemote) {
    Write-Host 'Remote server was not changed. Pass -ApplyRemote to recreate only the server container with this allowlist.'
    exit 0
}
if (-not $localReady) {
    Write-Host 'Remote server was not changed because the local allowlist update was not applied.'
    exit 0
}

if ($ServerHost -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$' -or $ServerUser -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
    $RemoteRoot -notmatch '^/[A-Za-z0-9._/-]+$' -or $RemoteRoot -match '(^|/)\.\.(/|$)') {
    throw 'The remote SSH target contains unsupported characters.'
}
if (-not $PSCmdlet.ShouldProcess("$ServerUser@$ServerHost", 'Install the production environment file and recreate the race server container')) {
    exit 0
}

$target = "$ServerUser@$ServerHost"
$remoteTemporary = "/tmp/spire-race-allowlist-$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()).env"
$remoteBackup = "$remoteTemporary.backup"
$sshOptions = Get-SshOptions
$scpOptions = Get-ScpOptions
& scp @scpOptions $resolvedEnv "${target}:$remoteTemporary"
if ($LASTEXITCODE -ne 0) { throw 'Failed to upload the production environment file.' }

$remoteCommand = @"
set -euo pipefail
trap 'sudo rm -f "$remoteTemporary" "$remoteBackup"' EXIT
sudo install -m 0600 "$RemoteRoot/shared/.env.production" "$remoteBackup"
sudo install -m 0600 "$remoteTemporary" "$RemoteRoot/shared/.env.production"
compose_file="$RemoteRoot/current/deploy/docker-compose.prod.yml"
test -f "`$compose_file"
sudo docker compose -p spire-race --env-file "$RemoteRoot/shared/.env.production" -f "`$compose_file" up -d --no-deps --force-recreate server
healthy=false
for attempt in `$(seq 1 30); do
  if sudo docker compose -p spire-race --env-file "$RemoteRoot/shared/.env.production" -f "`$compose_file" exec -T server /app/spire-race-server --healthcheck >/dev/null 2>&1; then
    healthy=true
    break
  fi
  sleep 2
done
if [[ "`$healthy" != true ]]; then
  sudo install -m 0600 "$remoteBackup" "$RemoteRoot/shared/.env.production"
  sudo docker compose -p spire-race --env-file "$RemoteRoot/shared/.env.production" -f "`$compose_file" up -d --no-deps --force-recreate server
  exit 1
fi
"@
& ssh @sshOptions $target $remoteCommand
if ($LASTEXITCODE -ne 0) { throw 'The allowlist was uploaded, but the remote server failed to restart or pass its health check.' }
Write-Host "Remote Steam allowlist applied successfully ($($current.Count) entries)."
