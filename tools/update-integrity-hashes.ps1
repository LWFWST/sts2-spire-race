[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$GameVersion = 'v0.111.0',
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string]$ModDll = '',
    [string]$ModManifest = '',
    [Parameter(Mandatory = $true)]
    [string]$ProductionEnvFile,
    [string]$OutputPath = '',
    [switch]$BuildMod,
    [switch]$Deploy,
    [string]$ServerHost = '134.122.116.15',
    [string]$ServerUser = 'root',
    [string]$RemoteRoot = '/opt/sts2-spire-race',
    [string]$SshKeyPath = '',
    [string]$TlsSource = 'C:\CP\MCC2\spirerace.xyz_nginx',
    [switch]$BuildImageRemotely
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$serverRoot = Join-Path $projectRoot 'server'
if ($GameVersion -notmatch '^v\d+\.\d+\.\d+$') {
    throw 'GameVersion must use the form v0.111.0.'
}

$resolvedGameDir = (Resolve-Path -LiteralPath $GameDir).Path
$resolvedEnv = (Resolve-Path -LiteralPath $ProductionEnvFile).Path
if (-not $ModDll) { $ModDll = Join-Path $projectRoot 'bin\Release\net9.0\sts2-spire-race.dll' }
if (-not $ModManifest) { $ModManifest = Join-Path $projectRoot 'manifest.json' }
if (-not $OutputPath) { $OutputPath = Join-Path $serverRoot "config\integrity\$GameVersion.json" }

$releaseInfoPath = Join-Path $resolvedGameDir 'release_info.json'
if (Test-Path -LiteralPath $releaseInfoPath -PathType Leaf) {
    $installedVersion = ([System.IO.File]::ReadAllText($releaseInfoPath) | ConvertFrom-Json).version
    if ($installedVersion -ne $GameVersion) {
        throw "The selected game directory is $installedVersion, not $GameVersion. Refusing to sign a mislabeled manifest."
    }
}

if ($BuildMod) {
    & dotnet build (Join-Path $projectRoot 'sts2-spire-race.csproj') -c Release "-p:GameDir=$resolvedGameDir"
    if ($LASTEXITCODE -ne 0) { throw 'The Mod build failed; no hashes were changed.' }
}

$resolvedDll = (Resolve-Path -LiteralPath $ModDll).Path
$resolvedModManifest = (Resolve-Path -LiteralPath $ModManifest).Path
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
$deploymentOutput = [System.IO.Path]::GetFullPath((Join-Path $serverRoot "config\integrity\$GameVersion.json"))
if ($Deploy -and -not [string]::Equals($resolvedOutput, $deploymentOutput, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'When -Deploy is used, OutputPath must be the server/config/integrity manifest included in the deployment.'
}

$environmentText = [System.IO.File]::ReadAllText($resolvedEnv)
$secretMatches = [regex]::Matches($environmentText, '(?m)^TOKEN_SECRET=([^\r\n]*)')
if ($secretMatches.Count -ne 1 -or [string]::IsNullOrWhiteSpace($secretMatches[0].Groups[1].Value)) {
    throw 'The production environment file must contain exactly one non-empty TOKEN_SECRET entry.'
}
if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    throw 'Go is required to generate and sign the integrity manifest.'
}

$manifestReady = $false
if ($PSCmdlet.ShouldProcess($resolvedOutput, "Recalculate and sign integrity hashes for $GameVersion")) {
    if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $temporary = Join-Path $outputDirectory ('.integrity-' + [guid]::NewGuid().ToString('N') + '.json')
    $env:TOKEN_SECRET = $secretMatches[0].Groups[1].Value
    try {
        Push-Location $serverRoot
        try {
            & go run ./cmd/integrity-manifest -version $GameVersion -game-dir $resolvedGameDir `
                -mod-dll $resolvedDll -mod-manifest $resolvedModManifest -output $temporary
            if ($LASTEXITCODE -ne 0) { throw 'Integrity manifest generation failed.' }
        } finally {
            Pop-Location
        }
        $generated = [System.IO.File]::ReadAllText($temporary) | ConvertFrom-Json
        if ($generated.game_version -ne $GameVersion -or [string]::IsNullOrWhiteSpace($generated.signature) -or
            $generated.game_files.Count -lt 3 -or $generated.allowed_mod_files.Count -lt 2) {
            throw 'The generated integrity manifest failed validation.'
        }
        Move-Item -LiteralPath $temporary -Destination $resolvedOutput -Force
        $manifestReady = $true
    } finally {
        Remove-Item Env:TOKEN_SECRET -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    Write-Host "Updated and signed $GameVersion integrity data ($($generated.game_files.Count) game files, $($generated.allowed_mod_files.Count) Mod files)."
}

if (-not $Deploy) {
    Write-Host 'Remote server was not changed. Pass -Deploy to publish the signed manifest through the normal production deployment.'
    exit 0
}
if (-not $manifestReady) {
    Write-Host 'Remote server was not changed because the signed manifest update was not applied.'
    exit 0
}
if ($ServerHost -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$' -or $ServerUser -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
    $RemoteRoot -notmatch '^/[A-Za-z0-9._/-]+$' -or $RemoteRoot -match '(^|/)\.\.(/|$)') {
    throw 'The remote SSH target contains unsupported characters.'
}
if (-not $PSCmdlet.ShouldProcess("$ServerUser@$ServerHost", "Deploy the newly signed $GameVersion integrity manifest")) {
    exit 0
}

$deployArguments = @{
    ServerHost = $ServerHost
    ServerUser = $ServerUser
    RemoteRoot = $RemoteRoot
    ProductionEnvFile = $resolvedEnv
    TlsSource = $TlsSource
}
if ($SshKeyPath) { $deployArguments.SshKeyPath = $SshKeyPath }
if ($BuildImageRemotely) { $deployArguments.BuildImageRemotely = $true }
& (Join-Path $PSScriptRoot 'deploy-server.ps1') @deployArguments
if ($LASTEXITCODE -ne 0) { throw 'The integrity manifest was updated locally, but production deployment failed.' }
