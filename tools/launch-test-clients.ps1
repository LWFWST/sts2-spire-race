param(
    [ValidateRange(1, 8)]
    [int]$ClientCount = 3,
    [string]$GameExe = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe',
    [string]$ServerUrl = 'http://127.0.0.1:8080/',
    [switch]$OpenRaceHub
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $GameExe -PathType Leaf)) {
    throw "Game executable not found: $GameExe"
}

$names = @('Alpha', 'Beta', 'Gamma', 'Delta', 'Epsilon', 'Zeta', 'Eta', 'Theta')
$testRoot = Join-Path (Split-Path -Parent $PSScriptRoot) '.test-users'
$templateSettings = Join-Path $testRoot 'alpha\AppData\Roaming\SlayTheSpire2\default\1\settings.save'

function Initialize-TestSettings([string]$appData) {
    $settingsPath = Join-Path $appData 'SlayTheSpire2\default\1\settings.save'
    if (-not (Test-Path -LiteralPath $settingsPath) -and (Test-Path -LiteralPath $templateSettings)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $settingsPath) | Out-Null
        Copy-Item -LiteralPath $templateSettings -Destination $settingsPath
    }
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return
    }

    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($null -eq $settings.mod_settings) {
        $settings | Add-Member -NotePropertyName mod_settings -NotePropertyValue ([pscustomobject]@{})
    }
    if ($null -eq $settings.mod_settings.PSObject.Properties['mods_enabled']) {
        $settings.mod_settings | Add-Member -NotePropertyName mods_enabled -NotePropertyValue $true
    } else {
        $settings.mod_settings.mods_enabled = $true
    }
    $settings.fullscreen = $false
    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
}

$common = @('--force-steam=off', '--spire-race-dev-auth')
if ($OpenRaceHub) {
    $common += '--spire-race-preview'
}

$workingDirectory = Split-Path -Parent $GameExe
$clients = for ($index = 0; $index -lt $ClientCount; $index++) {
    $name = $names[$index]
    $appData = Join-Path $testRoot "$($name.ToLowerInvariant())\AppData\Roaming"
    New-Item -ItemType Directory -Force -Path $appData | Out-Null
    Initialize-TestSettings $appData

    $process = Start-Process -FilePath $GameExe -WorkingDirectory $workingDirectory `
        -Environment @{ APPDATA = $appData; SPIRE_RACE_SERVER_URL = $ServerUrl } `
        -ArgumentList ($common + @(
            "--spire-race-dev-id=$([int64]90000000000000001 + $index)",
            "--spire-race-dev-name=Race%20$name"
        )) -PassThru

    [pscustomobject]@{
        Name = $name
        Pid = $process.Id
        SaveRoot = $appData
        Server = $ServerUrl
    }
}

$clients
