param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot "sts2-spire-race.csproj"
$output = Join-Path $projectRoot "dist\package\sts2-spire-race"
$serverRoot = Join-Path $projectRoot "server"
$serverOutput = Join-Path $projectRoot "dist\server"

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot "bin\$Configuration\net9.0\sts2-spire-race.dll") -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "manifest.json") -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "COMPATIBILITY.md") -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") -Destination $output -Force
Push-Location $serverRoot
try {
    go test ./...
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    New-Item -ItemType Directory -Force -Path $serverOutput | Out-Null
    go build -trimpath -o (Join-Path $serverOutput "spire-race-server.exe") ./cmd/server
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
Copy-Item -LiteralPath (Join-Path $projectRoot "docker-compose.yml") -Destination $serverOutput -Force
Copy-Item -LiteralPath (Join-Path $projectRoot ".env.example") -Destination $serverOutput -Force
Copy-Item -LiteralPath (Join-Path $serverRoot "migrations") -Destination $serverOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $serverRoot "config") -Destination $serverOutput -Recurse -Force
Write-Host "Package ready: $output"
Write-Host "Server bundle ready: $serverOutput"
