[CmdletBinding()]
param([string]$PublicBaseUrl = "")

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
$composeFile = Join-Path $repoRoot "compose.yml"
# Pin the production stack so inherited QuantaEdge COMPOSE_PROJECT_NAME cannot redirect it.
$composeProjectName = "qrtoprint"
$composeBaseArgs = @("--project-name", $composeProjectName, "--env-file", $envPath, "-f", $composeFile)
$exampleEnvPath = Join-Path $repoRoot ".env.example"

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
function Get-RepositoryFullName {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "Install Git or set GITHUB_REPOSITORY in .env so the release repository can be resolved."
    }
    $remoteOutput = @(& git -C $repoRoot remote get-url origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or $remoteOutput.Count -eq 0) {
        throw "Configure the repository origin remote so the release repository can be resolved."
    }
    $remoteUrl = [string]$remoteOutput[0]
    $marker = "github.com/"
    $markerIndex = $remoteUrl.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase)
    if ($markerIndex -lt 0) {
        $marker = "github.com:"
        $markerIndex = $remoteUrl.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase)
    }
    if ($markerIndex -lt 0) {
        throw "The repository origin must identify a GitHub owner and repository."
    }
    $repositoryPath = $remoteUrl.Substring($markerIndex + $marker.Length).TrimEnd("/")
    if ($repositoryPath.EndsWith(".git", [StringComparison]::OrdinalIgnoreCase)) {
        $repositoryPath = $repositoryPath.Substring(0, $repositoryPath.Length - 4)
    }
    $segments = $repositoryPath.Split("/")
    if ($segments.Length -ne 2 -or
        [string]::IsNullOrWhiteSpace($segments[0]) -or
        [string]::IsNullOrWhiteSpace($segments[1])) {
        throw "The repository origin must include exactly an owner and repository."
    }
    return "$($segments[0])/$($segments[1])"
}
function Get-RepositoryImageNamespace {
    $repository = Get-RepositoryFullName
    $owner = $repository.Split("/")[0]
    return ("ghcr.io/" + $owner.ToLowerInvariant())
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
    if ([string]::IsNullOrWhiteSpace($defaultBaseUrl)) {
        throw "A PUBLIC_BASE_URL is required; no deployment hostname is assumed."
    }
    $imageNamespace = Get-RepositoryImageNamespace
    $lines = @(
        "POSTGRES_DB=qrtoprint",
        "POSTGRES_USER=qrtoprint",
        "POSTGRES_PASSWORD=$(New-RandomHex 32)",
        "AUTH_SIGNING_KEY=$(New-RandomBase64 48)",
        "DOCUMENTS_ENCRYPTION_KEY=$(New-RandomBase64 32)",
        "PUBLIC_BASE_URL=$defaultBaseUrl",
        "IMAGE_NAMESPACE=$imageNamespace",
        "GITHUB_REPOSITORY=$(Get-RepositoryFullName)",
        "PUBLIC_DOWNLOAD_BASE_URL=",
        "IMAGE_TAG=latest"
    )
    Write-Host "Created .env with fresh production secrets and repository-derived GHCR and release configuration." -ForegroundColor Green
} else {
    $lines = @(Get-Content -LiteralPath $envPath)
    if (($lines -join "`n") -match "CHANGE_ME") { throw "The existing .env contains example secrets. Replace them before deployment." }
    foreach ($required in @("POSTGRES_PASSWORD=", "AUTH_SIGNING_KEY=", "DOCUMENTS_ENCRYPTION_KEY=")) {
        if (-not ($lines | Where-Object { $_.StartsWith($required, [StringComparison]::Ordinal) })) {
            throw ".env is missing $($required.TrimEnd('=')); existing secrets were not changed."
        }
    }
    Write-Host "Reusing the existing production database and document-encryption secrets." -ForegroundColor Yellow
}

$currentBaseUrl = Get-EnvValue $lines "PUBLIC_BASE_URL"
if (-not [string]::IsNullOrWhiteSpace($PublicBaseUrl)) { $currentBaseUrl = $PublicBaseUrl.Trim() }
if ([string]::IsNullOrWhiteSpace($currentBaseUrl)) {
    $currentBaseUrl = (Read-Host "Enter the canonical public HTTPS origin for this deployment").Trim()
}
if ([string]::IsNullOrWhiteSpace($currentBaseUrl)) {
    throw "A PUBLIC_BASE_URL is required; no deployment hostname is assumed."
}
$imageNamespace = Get-EnvValue $lines "IMAGE_NAMESPACE"
if ([string]::IsNullOrWhiteSpace($imageNamespace) -or $imageNamespace -match 'your-github-owner') {
    $imageNamespace = Get-RepositoryImageNamespace
}
$lines = Set-EnvValue $lines "IMAGE_NAMESPACE" $imageNamespace
$repositoryFullName = Get-RepositoryFullName
$configuredRepository = Get-EnvValue $lines "GITHUB_REPOSITORY"
if (-not [string]::IsNullOrWhiteSpace($configuredRepository) -and
    -not $configuredRepository.Equals($repositoryFullName, [StringComparison]::OrdinalIgnoreCase)) {
    throw "GITHUB_REPOSITORY in .env must match the checked-out repository origin ($repositoryFullName)."
}
$lines = Set-EnvValue $lines "GITHUB_REPOSITORY" $repositoryFullName
$downloadBaseUrl = Get-EnvValue $lines "PUBLIC_DOWNLOAD_BASE_URL"
if (-not [string]::IsNullOrWhiteSpace($downloadBaseUrl)) {
    $downloadUri = $null
    if (-not [Uri]::TryCreate($downloadBaseUrl, [UriKind]::Absolute, [ref]$downloadUri) -or
        $downloadUri.Scheme -ne "https" -or $downloadUri.AbsolutePath -ne "/" -or
        $downloadUri.Query -or $downloadUri.Fragment -or $downloadUri.UserInfo) {
        throw "PUBLIC_DOWNLOAD_BASE_URL must be a canonical HTTPS origin without a path, query string, fragment, or credentials."
    }
    $downloadBaseUrl = $downloadUri.GetLeftPart([UriPartial]::Authority).TrimEnd("/")
}
$lines = Set-EnvValue $lines "PUBLIC_DOWNLOAD_BASE_URL" $downloadBaseUrl
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
    throw "The shared Docker network 'cloudflare-edge' is missing. Start it from QuantaEdge-Production with .\scripts\local-ingress.ps1 up."
}
$connectorState = & docker inspect quantaedge-ingress-cloudflared --format '{{.State.Status}}' 2>$null
if ($LASTEXITCODE -ne 0 -or $connectorState -ne "running") {
    throw "The central Cloudflare connector is not running. Start it from QuantaEdge-Production with .\scripts\local-ingress.ps1 up."
}
Save-Utf8NoBom $envPath $lines

docker compose @composeBaseArgs config --quiet
if ($LASTEXITCODE -ne 0) { throw "Production Cloudflare Compose configuration is invalid." }

Write-Host "Pulling published images and starting SmartPrint production services on the shared Cloudflare edge network..." -ForegroundColor Cyan
docker compose @composeBaseArgs pull
$imagesPulled = ($LASTEXITCODE -eq 0)
if (-not $imagesPulled -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Warning "GHCR may require authentication. Trying the existing GitHub CLI login..."
    gh auth status --hostname github.com 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $ghUser = gh api user --jq .login 2>$null
        $ghToken = gh auth token --hostname github.com 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($ghUser) -and -not [string]::IsNullOrWhiteSpace($ghToken)) {
            $ghToken | docker login ghcr.io --username $ghUser --password-stdin | Out-Null
            $loginExitCode = $LASTEXITCODE
            $ghToken = $null
            if ($loginExitCode -eq 0) {
                docker compose @composeBaseArgs pull
                $imagesPulled = ($LASTEXITCODE -eq 0)
            }
        }
        $ghToken = $null
    }
}
if (-not $imagesPulled) { throw "GHCR images could not be pulled. Sign in with GitHub CLI or verify package access, then retry." }
docker compose @composeBaseArgs up -d --no-build --remove-orphans
if ($LASTEXITCODE -ne 0) { throw "Production stack failed to start." }

$apiHealthy = $false
for ($attempt = 0; $attempt -lt 120; $attempt++) {
    docker compose @composeBaseArgs exec -T api curl --fail --silent http://127.0.0.1:8080/health/ready | Out-Null
    if ($LASTEXITCODE -eq 0) { $apiHealthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $apiHealthy) {
    docker compose @composeBaseArgs logs --tail 100 api db
    throw "API did not become ready. Resolve startup or migration errors."
}

$portalHealthy = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    docker compose @composeBaseArgs exec -T portal wget -q -O /dev/null http://127.0.0.1/
    if ($LASTEXITCODE -eq 0) { $portalHealthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $portalHealthy) {
    docker compose @composeBaseArgs logs --tail 80 portal
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
    docker compose @composeBaseArgs logs --tail 100 portal api
    throw "Containers are healthy, but $currentBaseUrl is not reachable. Confirm the central Tunnel is running and its Published Application route targets http://smart-print-portal:80."
}

docker compose @composeBaseArgs ps
Write-Host ""
Write-Host "SmartPrint production deployment is reachable through the centrally managed Cloudflare Tunnel." -ForegroundColor Green
Write-Host "Single public application URL: $currentBaseUrl"
Write-Host "Portal, QR links and API:      $currentBaseUrl"
Write-Host "Agent local listener:         TCP 17842 (Private firewall profile)"
Write-Host "No customer certificate installation is required."
Write-Host "Do not run docker compose down -v; the database and documents are persistent."
