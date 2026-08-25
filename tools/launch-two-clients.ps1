param(
    [string]$GameExe = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe',
    [switch]$OpenRaceHub
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $GameExe)) {
    throw "Game executable not found: $GameExe"
}

$testRoot = Join-Path (Split-Path -Parent $PSScriptRoot) '.test-users'
$alphaAppData = Join-Path $testRoot 'alpha\AppData\Roaming'
$betaAppData = Join-Path $testRoot 'beta\AppData\Roaming'
New-Item -ItemType Directory -Force -Path $alphaAppData, $betaAppData | Out-Null

function Initialize-TestSettings([string]$appData) {
    $userRoot = Join-Path $appData 'SlayTheSpire2'
    $settingsPath = Join-Path $userRoot 'default\1\settings.save'
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        $source = Get-ChildItem (Join-Path $userRoot 'steam') -Filter 'settings.save' -File -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($source) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $settingsPath) | Out-Null
            Copy-Item -LiteralPath $source.FullName -Destination $settingsPath
        }
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

Initialize-TestSettings $alphaAppData
Initialize-TestSettings $betaAppData

# Test clients use isolated APPDATA roots and skip Steam cloud entirely. The
# development identity flags below still provide distinct race accounts.
$common = @('--force-steam=off', '--spire-race-dev-auth')
if ($OpenRaceHub) {
    $common += '--spire-race-preview'
}

$workingDirectory = Split-Path -Parent $GameExe
$alpha = Start-Process -FilePath $GameExe -WorkingDirectory $workingDirectory -Environment @{ APPDATA = $alphaAppData; SPIRE_RACE_SERVER_URL = 'http://127.0.0.1:8080/' } `
    -ArgumentList ($common + @('--spire-race-dev-id=90000000000000001', '--spire-race-dev-name=Race%20Alpha')) -PassThru
$beta = Start-Process -FilePath $GameExe -WorkingDirectory $workingDirectory -Environment @{ APPDATA = $betaAppData; SPIRE_RACE_SERVER_URL = 'http://127.0.0.1:8080/' } `
    -ArgumentList ($common + @('--spire-race-dev-id=90000000000000002', '--spire-race-dev-name=Race%20Beta')) -PassThru

[pscustomobject]@{
    AlphaPid = $alpha.Id
    AlphaSaveRoot = $alphaAppData
    BetaPid = $beta.Id
    BetaSaveRoot = $betaAppData
}
