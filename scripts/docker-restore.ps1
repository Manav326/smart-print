[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$BackupDirectory,

    [Parameter(Mandatory = $true)]
    [switch]$ConfirmRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot '.env'
$composeFile = Join-Path $repoRoot 'compose.yml'
# Restore only into the existing production project and its persistent volumes.
$composeProjectName = 'qrtoprint'
$composeBaseArgs = @('--project-name', $composeProjectName, '--env-file', $envPath, '-f', $composeFile)

if (-not $ConfirmRestore) {
    throw 'Restore is destructive. Re-run with -ConfirmRestore after checking the selected backup set.'
}
if (-not (Test-Path -LiteralPath $envPath)) {
    throw 'A running SmartPrint installation with its original .env is required. Start it with scripts\docker-up.ps1 first.'
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker was not found.' }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose v2 is required.' }

$resolvedBackup = (Resolve-Path -LiteralPath $BackupDirectory).Path
$dbCandidates = @(Get-ChildItem -LiteralPath $resolvedBackup -Filter 'qrtoprint-database-*.dump' -File | Sort-Object Name -Descending)
if ($dbCandidates.Count -eq 0) { throw 'The backup directory contains no qrtoprint-database-*.dump file.' }
$databaseFile = $dbCandidates[0].FullName
$stamp = [System.IO.Path]::GetFileNameWithoutExtension($dbCandidates[0].Name).Substring('qrtoprint-database-'.Length)
if ($stamp -notmatch '^[0-9]{8}-[0-9]{6}$') { throw 'The selected database backup filename does not contain a valid UTC backup timestamp.' }
$documentsFile = Join-Path $resolvedBackup ("qrtoprint-documents-$stamp.tar.gz")
$secretsFile = Join-Path $resolvedBackup ("qrtoprint-secrets-$stamp.env")
foreach ($requiredFile in @($documentsFile, $secretsFile)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) { throw "The matching backup file is missing: $requiredFile" }
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
foreach ($key in @('POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD')) {
    if (-not $currentValues.ContainsKey($key) -or -not $backupValues.ContainsKey($key) -or
        $currentValues[$key] -cne $backupValues[$key]) {
        throw "The running database credentials ($key) do not match this backup. Do not overwrite .env or attempt an in-place restore with a different PostgreSQL identity."
    }
}
foreach ($key in @('POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD', 'AUTH_SIGNING_KEY', 'DOCUMENTS_ENCRYPTION_KEY')) {
    if (-not $backupValues.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($backupValues[$key])) { throw "The backup secrets file is missing $key." }
}

$confirmText = Read-Host "This will replace the live database and cloud documents with backup $stamp. Type RESTORE $stamp to continue"
if ($confirmText -cne "RESTORE $stamp") { throw 'Restore confirmation did not match. No data was changed.' }

$dbUser = $backupValues['POSTGRES_USER']
$dbName = $backupValues['POSTGRES_DB']
$temporaryDbPath = "/tmp/qrtoprint-restore-$stamp.dump"
$temporaryDocumentsPath = "/tmp/qrtoprint-restore-$stamp.tar.gz"
$temporaryEnvPath = Join-Path $repoRoot ('.env.pre-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$backupHelper = $null
$apiContainer = $null
$servicesStopped = $false
$restoreStarted = $false
$restoreSucceeded = $false
$envReplaced = $false

try {
    Copy-Item -LiteralPath $envPath -Destination $temporaryEnvPath -Force
    Copy-Item -LiteralPath $secretsFile -Destination $envPath -Force
    $envReplaced = $true
    Write-Host "Saved previous deployment secrets to $temporaryEnvPath" -ForegroundColor Yellow

    Write-Host 'Pausing API and portal writes...' -ForegroundColor Cyan
    docker compose @composeBaseArgs stop api portal
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop API and portal services.' }
    $servicesStopped = $true

    docker compose @composeBaseArgs up -d db
    if ($LASTEXITCODE -ne 0) { throw 'Could not start PostgreSQL with the backup environment.' }

    $apiContainer = docker compose @composeBaseArgs ps -a -q api
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($apiContainer)) {
        docker compose @composeBaseArgs create api
        if ($LASTEXITCODE -ne 0) { throw 'Could not create an API container to access the persistent document volume.' }
        $apiContainer = docker compose @composeBaseArgs ps -a -q api
    }
    if ([string]::IsNullOrWhiteSpace($apiContainer)) { throw 'The API container could not be located.' }

    Write-Host 'Validating PostgreSQL backup archive...' -ForegroundColor Cyan
    $validator = docker create postgres:17-alpine pg_restore --list $temporaryDbPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($validator)) { throw 'Could not create a PostgreSQL archive validator.' }
    try {
        docker cp $databaseFile ($validator.Trim() + ':' + $temporaryDbPath) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not copy the database archive to the validator.' }
        docker start -a $validator | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The PostgreSQL archive is invalid or unreadable.' }
    } finally { docker rm -f $validator | Out-Null }

    Write-Host 'Validating encrypted-document archive...' -ForegroundColor Cyan
    $validator = docker create alpine:3.20 tar -tzf $temporaryDocumentsPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($validator)) { throw 'Could not create a document archive validator.' }
    try {
        docker cp $documentsFile ($validator.Trim() + ':' + $temporaryDocumentsPath) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not copy the document archive to the validator.' }
        $archiveEntries = docker start -a $validator
        if ($LASTEXITCODE -ne 0) { throw 'The encrypted-document archive is invalid or unreadable.' }
        if ($archiveEntries -match '(^|/)\.?private-relay(/|$)') {
            throw 'This backup contains Private relay content. Restore is blocked because Private Printing documents must never be recovered from disaster backups.'
        }
    } finally { docker rm -f $validator | Out-Null }

    Write-Host 'Replacing PostgreSQL database...' -ForegroundColor Cyan
    $restoreStarted = $true
    docker compose @composeBaseArgs exec -T db dropdb --if-exists --force -U $dbUser $dbName
    if ($LASTEXITCODE -ne 0) { throw 'Could not drop the existing database. The API and portal remain stopped.' }
    docker compose @composeBaseArgs exec -T db createdb -U $dbUser $dbName
    if ($LASTEXITCODE -ne 0) { throw 'Could not recreate the database. The API and portal remain stopped.' }
    docker compose @composeBaseArgs cp $databaseFile ('db:' + $temporaryDbPath)
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the database backup into PostgreSQL.' }
    docker compose @composeBaseArgs exec -T db pg_restore --exit-on-error --no-owner --no-privileges -U $dbUser -d $dbName $temporaryDbPath
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL restore failed. The API and portal remain stopped; inspect PostgreSQL logs before retrying.' }
    docker compose @composeBaseArgs exec -T db rm -f $temporaryDbPath | Out-Null

    # Private relay ciphertext is intentionally absent from disaster backups.
    # Clear stale ciphertext references and fail only incomplete relay tickets;
    # keep job/payment/audit metadata and already-completed ticket history.
    $privateRelayRecoverySql = @'
UPDATE "LocalPrintUploadTickets"
SET "PrivateRelayStorageKey" = NULL,
    "PrivateRelayWrappedKey" = NULL,
    "PrivateRelayIv" = NULL,
    "PrivateRelayUploadedUtc" = NULL,
    "PrivateRelayFailedUtc" = CASE
        WHEN "CompletedUtc" IS NULL THEN COALESCE("PrivateRelayFailedUtc", NOW())
        ELSE "PrivateRelayFailedUtc"
    END,
    "PrivateRelayFailureCode" = CASE
        WHEN "CompletedUtc" IS NULL THEN 'RESTORE_CONTENT_EXCLUDED'
        ELSE "PrivateRelayFailureCode"
    END,
    "PrivateRelayFailureMessage" = CASE
        WHEN "CompletedUtc" IS NULL THEN 'Private relay content is intentionally excluded from disaster recovery. Re-upload the original file if the shop Agent had not received it.'
        ELSE "PrivateRelayFailureMessage"
    END,
    "PrivateRelayStage" = CASE
        WHEN "CompletedUtc" IS NULL THEN 'FAILED'
        ELSE "PrivateRelayStage"
    END,
    "PrivateRelayStageUpdatedUtc" = CASE
        WHEN "CompletedUtc" IS NULL THEN NOW()
        ELSE "PrivateRelayStageUpdatedUtc"
    END
WHERE "PrivatePrinting" = TRUE AND "PrivateRelayStorageKey" IS NOT NULL;
'@
    docker compose @composeBaseArgs exec -T db psql -v ON_ERROR_STOP=1 -U $dbUser -d $dbName -c $privateRelayRecoverySql
    if ($LASTEXITCODE -ne 0) { throw 'Could not safely invalidate Private relay references excluded from this restore.' }

    Write-Host 'Restoring encrypted cloud documents...' -ForegroundColor Cyan
    $backupHelper = docker create --volumes-from ($apiContainer.Trim() + ':rw') alpine:3.20 sh -c ('mkdir -p /var/lib/qrtoprint/documents && find /var/lib/qrtoprint/documents -mindepth 1 -maxdepth 1 -exec rm -rf {} + && tar -xzf ' + $temporaryDocumentsPath + ' -C /var/lib/qrtoprint/documents && chown -R 1654:1654 /var/lib/qrtoprint/documents')
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($backupHelper)) { throw 'Could not create the document-volume restore helper.' }
    docker cp $documentsFile ($backupHelper.Trim() + ':' + $temporaryDocumentsPath) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the document archive to the restore helper.' }
    docker start -a $backupHelper | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cloud document restore failed. The API and portal remain stopped.' }
    docker rm -f $backupHelper | Out-Null
    $backupHelper = $null

    Write-Host 'Starting SmartPrint services from the restored backup...' -ForegroundColor Cyan
    docker compose @composeBaseArgs up -d
    if ($LASTEXITCODE -ne 0) { throw 'The restored data is in place, but Docker Compose could not restart the stack.' }

    $healthy = $false
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        docker compose @composeBaseArgs exec -T api curl --fail --silent http://127.0.0.1:8080/health/ready | Out-Null
        if ($LASTEXITCODE -eq 0) { $healthy = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $healthy) { throw 'The API did not become ready after restore. Inspect docker compose logs; the API and portal may need manual recovery.' }

    $restoreSucceeded = $true
    Write-Host 'SmartPrint database and encrypted cloud documents have been restored successfully.' -ForegroundColor Green
    Write-Host "The previous .env was preserved at: $temporaryEnvPath"
    Write-Host 'Private document content was not restored. Incomplete Private relay tickets were marked failed; re-upload is required only when the Agent had not received the protected file.'
    Write-Host 'The Windows Agent local DPAPI queue is not part of this Docker backup; it remains on each shop computer.'
} finally {
    if (-not [string]::IsNullOrWhiteSpace($backupHelper)) { docker rm -f $backupHelper | Out-Null }
    if (-not $restoreSucceeded -and $servicesStopped -and -not $restoreStarted) {
        Write-Warning 'Restore stopped before replacing the database; restarting the original API and portal.'
        if ($envReplaced -and (Test-Path -LiteralPath $temporaryEnvPath)) { Copy-Item -LiteralPath $temporaryEnvPath -Destination $envPath -Force }
        docker compose @composeBaseArgs start api portal | Out-Null
    } elseif (-not $restoreSucceeded -and $restoreStarted) {
        Write-Warning 'The restore started but did not complete. API and portal are intentionally left stopped to prevent serving a partial data restore.'
    }
}
