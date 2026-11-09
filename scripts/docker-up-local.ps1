[CmdletBinding()]
param([string]$PublicBaseUrl = "")

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env.local"
$composeFile = Join-Path $repoRoot "compose.local.yml"
# Never inherit COMPOSE_PROJECT_NAME from the caller: this is an isolated app stack.
$composeProjectName = "smart-print"
$composeBaseArgs = @("--project-name", $composeProjectName, "--env-file", $envPath, "-f", $composeFile)
$exampleEnvPath = Join-Path $repoRoot ".env.example"

function Assert-SmartPrintWorkspaceBuildIdentity {
    $rootManifestPath = Join-Path $repoRoot "package.json"
    $portalManifestPath = Join-Path $repoRoot "apps/portal/package.json"
    $desktopManifestPath = Join-Path $repoRoot "apps/desktop/package.json"
    $portalDockerfilePath = Join-Path $repoRoot "apps/portal/Dockerfile"
    $responsiveBrowserTestPath = Join-Path $repoRoot "apps/portal/tests/theme-responsive.browser.mjs"
    foreach ($requiredPath in @($rootManifestPath, $portalManifestPath, $desktopManifestPath, $portalDockerfilePath, $responsiveBrowserTestPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required SmartPrint workspace identity file is missing: $requiredPath"
        }
    }

    $rootManifest = Get-Content -LiteralPath $rootManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $portalManifest = Get-Content -LiteralPath $portalManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $desktopManifest = Get-Content -LiteralPath $desktopManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $portalDockerfile = Get-Content -LiteralPath $portalDockerfilePath -Raw
    $responsiveBrowserTest = Get-Content -LiteralPath $responsiveBrowserTestPath -Raw
    if ($rootManifest.name -ne "smart-print" -or
        $portalManifest.name -ne "@smart-print/portal" -or
        $desktopManifest.name -ne "@smart-print/desktop" -or
        $portalDockerfile -notmatch 'pnpm --filter @smart-print/portal build' -or
        $responsiveBrowserTest -notmatch '"--filter",\s*"@smart-print/portal"' -or
        $responsiveBrowserTest -notmatch '"--filter",\s*"@smart-print/desktop"') {
        throw "The checkout still contains an old workspace/package identity. Expected smart-print, @smart-print/portal, @smart-print/desktop, and matching Docker/browser QA package filters. Pull the current main branch before building."
    }
    Write-Host "Verified workspace identity: smart-print / @smart-print/portal / @smart-print/desktop."
}

function New-RandomHex([int]$Length) {
    $bytes = New-Object byte[] $Length
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [BitConverter]::ToString($bytes).Replace("-", "")
}
function New-RandomBase64([int]$Length) {
    $bytes = New-Object byte[] $Length
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}
function Get-EnvValue([string[]]$Lines, [string]$Name) {
    $prefix = "$Name="
    $match = $Lines | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } | Select-Object -First 1
    if ($null -eq $match) { return "" }
    return $match.Substring($prefix.Length).Trim()
}
function Set-EnvValue([string[]]$Lines, [string]$Name, [string]$Value) {
    $prefix = "$Name="
    $found = $false
    $result = @()
    foreach ($line in $Lines) {
        if ($line.StartsWith($prefix, [StringComparison]::Ordinal)) {
            if (-not $found) { $result += "$prefix$Value"; $found = $true }
        } else { $result += $line }
    }
    if (-not $found) { $result += "$prefix$Value" }
    return ,([string[]]$result)
}
function Save-Utf8NoBom([string]$Path, [string[]]$Lines) {
    [IO.File]::WriteAllLines($Path, $Lines, [Text.UTF8Encoding]::new($false))
}

$exampleLines = @(Get-Content -LiteralPath $exampleEnvPath)
$defaultBaseUrl = Get-EnvValue $exampleLines "PUBLIC_BASE_URL"
if (-not [string]::IsNullOrWhiteSpace($PublicBaseUrl)) {
    $defaultBaseUrl = $PublicBaseUrl.Trim()
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Install and start Docker Desktop first." }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Compose v2 is required." }
docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Desktop engine is not running." }

if (-not (Test-Path -LiteralPath $envPath)) {
    if ([string]::IsNullOrWhiteSpace($defaultBaseUrl)) {
        $defaultBaseUrl = (Read-Host "Enter the canonical public HTTPS origin for this deployment").Trim()
    }
    if ([string]::IsNullOrWhiteSpace($defaultBaseUrl)) { throw "A PUBLIC_BASE_URL is required; no deployment hostname is assumed." }
    $lines = @(
        "POSTGRES_DB=qrtoprint",
        "POSTGRES_USER=qrtoprint",
        "POSTGRES_PASSWORD=$(New-RandomHex 32)",
        "AUTH_SIGNING_KEY=$(New-RandomBase64 48)",
        "DOCUMENTS_ENCRYPTION_KEY=$(New-RandomBase64 32)",
        "PUBLIC_BASE_URL=$defaultBaseUrl"
    )
    Write-Host "Created .env.local with fresh local secrets." -ForegroundColor Green
} else {
    $lines = @(Get-Content -LiteralPath $envPath)
    if (($lines -join "`n") -match "CHANGE_ME") { throw ".env.local contains example secrets. Replace them before starting the local stack." }
    foreach ($required in @("POSTGRES_PASSWORD=", "AUTH_SIGNING_KEY=", "DOCUMENTS_ENCRYPTION_KEY=")) {
        if (-not ($lines | Where-Object { $_.StartsWith($required, [StringComparison]::Ordinal) })) {
            throw ".env.local is missing $($required.TrimEnd('=')); existing secrets were not changed."
        }
    }
    Write-Host "Reusing the existing local database and document-encryption secrets." -ForegroundColor Yellow
}

$currentBaseUrl = Get-EnvValue $lines "PUBLIC_BASE_URL"
if (-not [string]::IsNullOrWhiteSpace($PublicBaseUrl)) { $currentBaseUrl = $PublicBaseUrl.Trim() }
if ([string]::IsNullOrWhiteSpace($currentBaseUrl)) {
    $currentBaseUrl = (Read-Host "Enter the canonical public HTTPS origin for this deployment").Trim()
}
if ([string]::IsNullOrWhiteSpace($currentBaseUrl)) { throw "A PUBLIC_BASE_URL is required; no deployment hostname is assumed." }
$uri = $null
if (-not [Uri]::TryCreate($currentBaseUrl, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -ne "https" -or $uri.AbsolutePath -ne "/" -or
    $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
    throw "PUBLIC_BASE_URL must be a canonical HTTPS origin without a path, query string, fragment, or credentials."
}
$currentBaseUrl = $uri.GetLeftPart([UriPartial]::Authority).TrimEnd("/")
$lines = Set-EnvValue $lines "PUBLIC_BASE_URL" $currentBaseUrl
$lines = @($lines | Where-Object {
    -not $_.StartsWith("LOCAL_PORTAL_ORIGIN=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("LOCAL_API_BASE_URL=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("LOCAL_LAN_IP=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("PORTAL_DOMAIN=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("API_DOMAIN=", [StringComparison]::Ordinal) -and
    -not $_.StartsWith("ACME_EMAIL=", [StringComparison]::Ordinal)
})

& docker network inspect cloudflare-edge *> $null
if ($LASTEXITCODE -ne 0) {
    throw "The shared Docker network 'cloudflare-edge' is missing. Start it from the QuantaEdge-Production checkout with .\scripts\local-ingress.ps1 up."
}
$connectorState = & docker inspect quantaedge-ingress-cloudflared --format '{{.State.Status}}' 2>$null
if ($LASTEXITCODE -ne 0 -or $connectorState -ne "running") {
    throw "The central Cloudflare connector is not running. Start it from QuantaEdge-Production with .\scripts\local-ingress.ps1 up."
}
Save-Utf8NoBom $envPath $lines

& docker compose @composeBaseArgs config --quiet
if ($LASTEXITCODE -ne 0) { throw "Local Cloudflare Compose configuration is invalid." }

Assert-SmartPrintWorkspaceBuildIdentity

$expectedImages = @{
    db = "postgres:17-alpine"
    api = "localhost/smart-print/smart-print-api:local"
    portal = "localhost/smart-print/smart-print-portal:local"
}
$configArgs = $composeBaseArgs + @("config", "--format", "json")
$configOutput = & docker compose @configArgs
if ($LASTEXITCODE -ne 0) { throw "Could not resolve the SmartPrint local Compose configuration." }
$resolvedConfig = ($configOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
foreach ($serviceName in @("db", "api", "portal")) {
    $configuredImage = [string]$resolvedConfig.services.$serviceName.image
    if ($configuredImage -ne $expectedImages[$serviceName]) {
        throw "Compose image mismatch for '$serviceName'. Expected '$($expectedImages[$serviceName])', found '$configuredImage'. Refusing to build or start a stale image configuration."
    }
    Write-Host ("Confirmed {0} image target: {1}" -f $serviceName, $configuredImage)
}

Write-Host "Building and verifying the exact SmartPrint API and portal images..." -ForegroundColor Cyan
$buildArgs = $composeBaseArgs + @("build", "--progress", "plain", "api", "portal")
& docker compose @buildArgs
if ($LASTEXITCODE -ne 0) {
    & docker compose @composeBaseArgs logs --tail 100
    throw "SmartPrint API or portal image build failed."
}
foreach ($serviceName in @("api", "portal")) {
    $expectedImage = $expectedImages[$serviceName]
    $tagOutput = & docker image inspect --format '{{json .RepoTags}}' $expectedImage
    if ($LASTEXITCODE -ne 0) { throw "The expected SmartPrint image was not created: $expectedImage" }
    $repoTags = ($tagOutput -join "") | ConvertFrom-Json -ErrorAction Stop
    if (@($repoTags) -notcontains $expectedImage) {
        throw "Docker image inspection did not confirm the required tag '$expectedImage'."
    }
    Write-Host ("Verified built image: {0}" -f $expectedImage) -ForegroundColor Green
}

Write-Host "Starting the verified SmartPrint images and existing local PostgreSQL volume..." -ForegroundColor Cyan
$upArgs = $composeBaseArgs + @("up", "-d", "--no-build")
& docker compose @upArgs
if ($LASTEXITCODE -ne 0) {
    & docker compose @composeBaseArgs logs --tail 100
    throw "The verified SmartPrint local stack failed to start."
}

$apiHealthy = $false
for ($attempt = 0; $attempt -lt 120; $attempt++) {
    & docker compose @composeBaseArgs exec -T api curl --fail --silent http://127.0.0.1:8080/health/ready | Out-Null
    if ($LASTEXITCODE -eq 0) { $apiHealthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $apiHealthy) {
    & docker compose @composeBaseArgs logs --tail 100 api db
    throw "API did not become ready. Resolve startup or migration errors."
}

$portalHealthy = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    & docker compose @composeBaseArgs exec -T portal wget -q -O /dev/null http://127.0.0.1/
    if ($LASTEXITCODE -eq 0) { $portalHealthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $portalHealthy) {
    & docker compose @composeBaseArgs logs --tail 80 portal
    throw "Portal did not become ready."
}

$publicHealthy = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri "$currentBaseUrl/health/ready" -UseBasicParsing -TimeoutSec 12
        if ($response.StatusCode -eq 200) { $publicHealthy = $true; break }
    } catch { }
    Start-Sleep -Seconds 3
}
if (-not $publicHealthy) {
    & docker compose @composeBaseArgs logs --tail 100 portal api
    throw "Containers are healthy, but $currentBaseUrl is not reachable. Confirm the central Tunnel is running and its Published Application route targets http://smart-print-portal:80."
}

& docker compose @composeBaseArgs ps
Write-Host ""
Write-Host "SmartPrint is reachable through the centrally managed Cloudflare Tunnel." -ForegroundColor Green
Write-Host "Single public application URL: $currentBaseUrl"
Write-Host "Portal, QR links and API:      $currentBaseUrl"
Write-Host "Agent local listener:         TCP 17842 (Private firewall profile)"
Write-Host "No customer certificate installation is required."
Write-Host "Do not run docker compose down -v; the database and documents are persistent."
