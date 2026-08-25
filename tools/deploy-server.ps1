param(
    [string]$ServerHost = '82.156.34.63',
    [string]$ServerUser = 'ubuntu',
    [string]$RemoteRoot = '/opt/sts2-spire-race',
    [string]$SshKeyPath = '',
    [string]$ProductionEnvFile = '',
    [string]$TlsSource = 'C:\CP\MCC2\spirerace.xyz_nginx'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$timestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$archive = Join-Path ([System.IO.Path]::GetTempPath()) "spire-race-$timestamp.tar.gz"
$remoteArchive = "/tmp/spire-race-$timestamp.tar.gz"
$target = "$ServerUser@$ServerHost"
$sshOptions = @('-o', 'StrictHostKeyChecking=accept-new')
if ($SshKeyPath) {
    $resolvedKey = (Resolve-Path -LiteralPath $SshKeyPath).Path
    $sshOptions += @('-i', $resolvedKey)
}

$certificate = Join-Path $TlsSource 'spirerace.xyz_bundle.crt'
$privateKey = Join-Path $TlsSource 'spirerace.xyz.key'
foreach ($required in @($certificate, $privateKey)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required TLS file not found: $required"
    }
}
if ($ProductionEnvFile -and -not (Test-Path -LiteralPath $ProductionEnvFile -PathType Leaf)) {
    throw "Production environment file not found: $ProductionEnvFile"
}

try {
    & tar -czf $archive --exclude='.git' --exclude='bin' --exclude='obj' --exclude='dist' `
        --exclude='.test-users' --exclude='artifacts' --exclude='*.log' --exclude='.env' `
        --exclude='.env.*' --exclude='*.pem' --exclude='*.key' --exclude='*.crt' `
        --exclude='*.p12' --exclude='*.pfx' --exclude='stsrace_credentials.json' -C $projectRoot .
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create deployment archive.' }

    & ssh @sshOptions $target "sudo mkdir -p '$RemoteRoot/releases' '$RemoteRoot/shared/tls' '$RemoteRoot/backups' && sudo chown -R '$ServerUser':'$ServerUser' '$RemoteRoot'"
    if ($LASTEXITCODE -ne 0) { throw 'Failed to prepare the remote deployment directory.' }
    & scp @sshOptions $archive "${target}:$remoteArchive"
    & scp @sshOptions $certificate "${target}:/tmp/spirerace.xyz_bundle.crt"
    & scp @sshOptions $privateKey "${target}:/tmp/spirerace.xyz.key"
    if ($ProductionEnvFile) {
        & scp @sshOptions $ProductionEnvFile "${target}:/tmp/spire-race.env.production"
    }
    if ($LASTEXITCODE -ne 0) { throw 'Failed to upload deployment files.' }

    $envInstall = if ($ProductionEnvFile) {
        "sudo install -m 0600 /tmp/spire-race.env.production '$RemoteRoot/shared/.env.production' &&"
    } else { '' }
    $remoteCommand = @"
set -euo pipefail
trap 'rm -f "$remoteArchive" /tmp/spirerace.xyz_bundle.crt /tmp/spirerace.xyz.key /tmp/spire-race.env.production' EXIT
release='$RemoteRoot/releases/$timestamp'
mkdir -p "`$release"
tar -xzf '$remoteArchive' -C "`$release"
sudo install -m 0644 /tmp/spirerace.xyz_bundle.crt '$RemoteRoot/shared/tls/spirerace.xyz_bundle.crt'
sudo install -m 0600 /tmp/spirerace.xyz.key '$RemoteRoot/shared/tls/spirerace.xyz.key'
$envInstall sudo bash "`$release/deploy/deploy-server.sh" --release-dir "`$release" --root-dir '$RemoteRoot'
"@
    & ssh @sshOptions -t $target $remoteCommand
    if ($LASTEXITCODE -ne 0) { throw 'Remote deployment failed.' }
}
finally {
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
}
