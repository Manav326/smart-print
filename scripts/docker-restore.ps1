[CmdletBinding()]
param(
    [string]$BackupDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "backups"),
    [string]$BackupFile = "",
    [Security.SecureString]$Passphrase,
    [switch]$ConfirmRestore
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
$tunnelTokenPath = Join-Path $repoRoot ".secrets/cloudflare/tunnel-token"
$composeFile = Join-Path $repoRoot "compose.yml"
$composeProjectName = "qrtoprint"
$composeBaseArgs = @("--project-name", $composeProjectName, "--env-file", $envPath, "-f", $composeFile)

Import-Module (Join-Path $PSScriptRoot "SmartPrint.BackupCrypto.psm1") -Force

if (-not $ConfirmRestore) {
    throw "Restore is destructive. Re-run with -ConfirmRestore after checking the encrypted backup set."
}
if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
    throw "A running SmartPrint installation with its original .env is required. Start it with scripts\docker-up.ps1 first."
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker was not found." }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Compose v2 is required." }

if ([string]::IsNullOrWhiteSpace($BackupFile)) {
    $resolvedBackupDirectory = (Resolve-Path -LiteralPath $BackupDirectory).Path
    $candidates = @(Get-ChildItem -LiteralPath $resolvedBackupDirectory -Filter "SmartPrint-*.spbackup" -File |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($candidates.Count -eq 0) { throw "No encrypted SmartPrint-*.spbackup file was found in $resolvedBackupDirectory." }
    $encryptedBackup = $candidates[0].FullName
} else {
    $encryptedBackup = (Resolve-Path -LiteralPath $BackupFile).Path
    if ([IO.Path]::GetExtension($encryptedBackup) -ne ".spbackup") {
        throw "Only authenticated .spbackup bundles are accepted. Legacy plaintext backup sets must be handled in a controlled migration, not restored directly."
    }
}
if ([IO.Path]::GetExtension($encryptedBackup) -ne ".spbackup") {
    throw "Only authenticated .spbackup bundles are accepted."
}

if ($null -eq $Passphrase) {
    $Passphrase = Read-Host "Enter the passphrase used to encrypt this SmartPrint backup" -AsSecureString
}
if ($Passphrase.Length -lt 16) { throw "The backup passphrase must contain at least 16 characters." }

$stageDirectory = Join-Path ([IO.Path]::GetTempPath()) ("SmartPrintRestore-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $stageDirectory /inheritance:r /grant:r "$($identity):(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" "*S-1-5-18:(OI)(CI)(F)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not restrict temporary decrypted backup permissions." }

$plainBundle = Join-Path $stageDirectory "SmartPrint.bundle.zip"
$extractDirectory = Join-Path $stageDirectory "bundle"
New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
$temporaryDbPath = "/tmp/qrtoprint-restore-" + [Guid]::NewGuid().ToString("N") + ".dump"
$temporaryDocumentsPath = "/tmp/qrtoprint-documents-restore-" + [Guid]::NewGuid().ToString("N") + ".tar.gz"
$temporaryEnvPath = Join-Path $repoRoot (".env.pre-restore-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$temporaryTunnelTokenPath = Join-Path $repoRoot (".secrets/cloudflare/tunnel-token.pre-restore-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$backupHelper = $null
$apiContainer = $null
$servicesStopped = $false
$restoreStarted = $false
$restoreSucceeded = $false
$envReplaced = $false

try {
    Write-Host "Authenticating and decrypting the complete backup bundle..." -ForegroundColor Cyan
    $decryptResult = Unprotect-SmartPrintBackup -InputPath $encryptedBackup -OutputPath $plainBundle -Passphrase $Passphrase
    if (-not $decryptResult.Authenticated) { throw "The backup bundle did not pass authenticated decryption." }
    Expand-Archive -LiteralPath $plainBundle -DestinationPath $extractDirectory -Force

    $manifestPath = Join-Path $extractDirectory "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "The backup bundle manifest is missing." }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Format -ne "SmartPrint.BackupBundle.v2") { throw "The backup manifest version is unsupported or does not contain the full recovery secret set." }

    $databaseFile = Join-Path $extractDirectory ([string]$manifest.DatabaseFile)
    $documentsFile = Join-Path $extractDirectory ([string]$manifest.DocumentsFile)
    $secretsFile = Join-Path $extractDirectory ([string]$manifest.SecretsFile)
    $tunnelTokenFile = Join-Path $extractDirectory ([string]$manifest.TunnelTokenFile)
    foreach ($file in @($databaseFile, $documentsFile, $secretsFile, $tunnelTokenFile)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "A required encrypted backup component is missing: $([IO.Path]::GetFileName($file))" }
    }

    if ((Get-FileHash -LiteralPath $databaseFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.DatabaseSha256 -or
        (Get-FileHash -LiteralPath $documentsFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.DocumentsSha256 -or
        (Get-FileHash -LiteralPath $secretsFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.SecretsSha256 -or
        (Get-FileHash -LiteralPath $tunnelTokenFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.TunnelTokenSha256) {
        throw "A backup component checksum does not match the authenticated manifest."
    }

    function Read-EnvFile([string]$Path) {
        $result = @{}
        foreach ($line in (Get-Content -LiteralPath $Path)) {
            if ($line -match '^([A-Z0-9_]+)=(.*)$') { $result[$Matches[1]] = $Matches[2] }
        }
        return $result
    }

    $currentValues = Read-EnvFile $envPath
    $backupValues = Read-EnvFile $secretsFile
    foreach ($key in @("POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD")) {
        if (-not $currentValues.ContainsKey($key) -or -not $backupValues.ContainsKey($key) -or
            $currentValues[$key] -cne $backupValues[$key]) {
            throw "The running database credentials ($key) do not match this backup. Do not overwrite .env or attempt an in-place restore with a different PostgreSQL identity."
        }
    }
    foreach ($key in @("POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD", "AUTH_SIGNING_KEY", "DOCUMENTS_ENCRYPTION_KEY")) {
        if (-not $backupValues.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($backupValues[$key])) {
            throw "The encrypted backup's secrets file is missing $key."
        }
    }

    $confirmText = Read-Host "This will replace the live database and cloud documents with encrypted backup $([IO.Path]::GetFileName($encryptedBackup)). Type RESTORE to continue"
    if ($confirmText -cne "RESTORE") { throw "Restore confirmation did not match. No data was changed." }

    $dbUser = $backupValues["POSTGRES_USER"]
    $dbName = $backupValues["POSTGRES_DB"]
    Copy-Item -LiteralPath $envPath -Destination $temporaryEnvPath -Force
    & icacls.exe $temporaryEnvPath /inheritance:r /grant:r "$($identity):(F)" "*S-1-5-32-544:(F)" "*S-1-5-18:(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict the pre-restore environment backup permissions." }

    if (Test-Path -LiteralPath $tunnelTokenPath -PathType Leaf) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $temporaryTunnelTokenPath) -Force | Out-Null
        Copy-Item -LiteralPath $tunnelTokenPath -Destination $temporaryTunnelTokenPath -Force
        & icacls.exe $temporaryTunnelTokenPath /inheritance:r /grant:r "$($identity):(F)" "*S-1-5-32-544:(F)" "*S-1-5-18:(F)" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not restrict the pre-restore Cloudflare Tunnel secret backup permissions." }
    }

    $envReplaced = $true
    Copy-Item -LiteralPath $secretsFile -Destination $envPath -Force
    New-Item -ItemType Directory -Path (Split-Path -Parent $tunnelTokenPath) -Force | Out-Null
    Copy-Item -LiteralPath $tunnelTokenFile -Destination $tunnelTokenPath -Force
    & icacls.exe $tunnelTokenPath /inheritance:r /grant:r "$($identity):(F)" "*S-1-5-32-544:(F)" "*S-1-5-18:(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict the restored Cloudflare Tunnel secret permissions." }
    Write-Host "Saved previous deployment secrets to $temporaryEnvPath" -ForegroundColor Yellow

    Write-Host "Pausing API and portal writes..." -ForegroundColor Cyan
    docker compose @composeBaseArgs stop api portal
    if ($LASTEXITCODE -ne 0) { throw "Could not stop API and portal services." }
    $servicesStopped = $true

    docker compose @composeBaseArgs up -d db
    if ($LASTEXITCODE -ne 0) { throw "Could not start PostgreSQL with the backup environment." }

    $apiContainer = docker compose @composeBaseArgs ps -a -q api
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($apiContainer)) {
        docker compose @composeBaseArgs create api
        if ($LASTEXITCODE -ne 0) { throw "Could not create an API container to access the persistent document volume." }
        $apiContainer = docker compose @composeBaseArgs ps -a -q api
    }
    if ([string]::IsNullOrWhiteSpace($apiContainer)) { throw "The API container could not be located." }

    Write-Host "Validating PostgreSQL archive..." -ForegroundColor Cyan
    $validator = docker create postgres:17-alpine pg_restore --list /tmp/smartprint-restore-check.dump
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($validator)) { throw "Could not create a PostgreSQL archive validator." }
    try {
        docker cp $databaseFile ($validator.Trim() + ":/tmp/smartprint-restore-check.dump") | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not copy the database archive to the validator." }
        docker start -a $validator | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The PostgreSQL archive is invalid or unreadable." }
    } finally { docker rm -f $validator | Out-Null }

    Write-Host "Validating encrypted-document archive..." -ForegroundColor Cyan
    $validator = docker create alpine:3.20 tar -tzf /tmp/smartprint-documents-check.tar.gz
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($validator)) { throw "Could not create a document archive validator." }
    try {
        docker cp $documentsFile ($validator.Trim() + ":/tmp/smartprint-documents-check.tar.gz") | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not copy the document archive to the validator." }
        docker start -a $validator | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The encrypted-document archive is invalid or unreadable." }
    } finally { docker rm -f $validator | Out-Null }

    Write-Host "Replacing PostgreSQL database..." -ForegroundColor Cyan
    $restoreStarted = $true
    docker compose @composeBaseArgs exec -T db dropdb --if-exists --force -U $dbUser $dbName
    if ($LASTEXITCODE -ne 0) { throw "Could not drop the existing database. The API and portal remain stopped." }
    docker compose @composeBaseArgs exec -T db createdb -U $dbUser $dbName
    if ($LASTEXITCODE -ne 0) { throw "Could not recreate the database. The API and portal remain stopped." }
    docker compose @composeBaseArgs cp $databaseFile ("db:" + $temporaryDbPath)
    if ($LASTEXITCODE -ne 0) { throw "Could not copy the database backup into PostgreSQL." }
    docker compose @composeBaseArgs exec -T db pg_restore --exit-on-error --no-owner --no-privileges -U $dbUser -d $dbName $temporaryDbPath
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL restore failed. The API and portal remain stopped; inspect PostgreSQL logs before retrying." }
    docker compose @composeBaseArgs exec -T db rm -f $temporaryDbPath | Out-Null

    Write-Host "Restoring encrypted cloud documents..." -ForegroundColor Cyan
    $backupHelper = docker create --volumes-from ($apiContainer.Trim() + ":rw") alpine:3.20 sh -c ("mkdir -p /var/lib/qrtoprint/documents && find /var/lib/qrtoprint/documents -mindepth 1 -maxdepth 1 -exec rm -rf {} + && tar -xzf " + $temporaryDocumentsPath + " -C /var/lib/qrtoprint/documents && chown -R 1654:1654 /var/lib/qrtoprint/documents")
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($backupHelper)) { throw "Could not create the document-volume restore helper." }
    docker cp $documentsFile ($backupHelper.Trim() + ":" + $temporaryDocumentsPath) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not copy the document archive to the restore helper." }
    docker start -a $backupHelper | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cloud document restore failed. The API and portal remain stopped." }
    docker rm -f $backupHelper | Out-Null
    $backupHelper = $null

    Write-Host "Starting SmartPrint services from the restored backup..." -ForegroundColor Cyan
    docker compose @composeBaseArgs up -d
    if ($LASTEXITCODE -ne 0) { throw "The restored data is in place, but Docker Compose could not restart the stack." }

    $healthy = $false
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        docker compose @composeBaseArgs exec -T api curl --fail --silent http://127.0.0.1:8080/health/ready | Out-Null
        if ($LASTEXITCODE -eq 0) { $healthy = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $healthy) { throw "The API did not become ready after restore. Inspect docker compose logs; the API and portal may need manual recovery." }

    $restoreSucceeded = $true
    Write-Host "SmartPrint database and encrypted cloud documents have been restored successfully." -ForegroundColor Green
    Write-Host "The previous .env was preserved at: $temporaryEnvPath"
    Write-Host "The Cloudflare Tunnel token has been restored at .secrets/cloudflare/tunnel-token. Restart the separately managed Cloudflare Tunnel connector to load it."
    Write-Host "The Windows Agent local DPAPI queue is not part of this Docker backup; it remains on each shop computer."
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($backupHelper)) { docker rm -f $backupHelper | Out-Null }
    if (-not $restoreStarted -and $envReplaced) {
        Write-Warning "Restore stopped before changing the database; restoring the original deployment secrets."
        if (Test-Path -LiteralPath $temporaryEnvPath) {
            Copy-Item -LiteralPath $temporaryEnvPath -Destination $envPath -Force
        }
        if (Test-Path -LiteralPath $temporaryTunnelTokenPath) {
            Copy-Item -LiteralPath $temporaryTunnelTokenPath -Destination $tunnelTokenPath -Force
        } else {
            Remove-Item -LiteralPath $tunnelTokenPath -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $restoreSucceeded -and $servicesStopped -and -not $restoreStarted) {
        Write-Warning "Restore stopped before replacing the database; restarting the original API and portal."
        docker compose @composeBaseArgs start api portal | Out-Null
    } elseif (-not $restoreSucceeded -and $restoreStarted) {
        Write-Warning "The restore started but did not complete. API and portal are intentionally left stopped to prevent serving a partial data restore."
    }
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $Passphrase) { $Passphrase.Dispose() }
}
