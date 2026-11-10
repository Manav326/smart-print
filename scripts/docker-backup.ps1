#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$BackupDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "backups"),
    [string]$OffsiteRemote = $env:SMARTPRINT_BACKUP_RCLONE_REMOTE,
    [switch]$AllowLocalOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repoRoot ".env"
$tunnelTokenSource = Join-Path $repoRoot ".secrets/cloudflare/tunnel-token"
$composeFile = Join-Path $repoRoot "compose.yml"
$composeProjectName = "qrtoprint"
$composeBaseArgs = @("--project-name", $composeProjectName, "--env-file", $envPath, "-f", $composeFile)

Import-Module (Join-Path $PSScriptRoot "SmartPrint.BackupCrypto.psm1") -Force

if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
    throw "No .env file exists. Start SmartPrint with scripts\docker-up.ps1 first."
}
if (-not (Test-Path -LiteralPath $tunnelTokenSource -PathType Leaf)) {
    throw "The Cloudflare Tunnel secret is missing at .secrets/cloudflare/tunnel-token. It is required for a complete production recovery set."
}
if ((Get-Item -LiteralPath $tunnelTokenSource).Length -lt 16) {
    throw "The Cloudflare Tunnel secret is unexpectedly short. Refusing to make an incomplete recovery set."
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker was not found." }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker Compose v2 is required." }

$values = @{}
foreach ($line in (Get-Content -LiteralPath $envPath)) {
    if ($line -match '^([A-Z0-9_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
}
foreach ($required in @("POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD", "DOCUMENTS_ENCRYPTION_KEY", "AUTH_SIGNING_KEY")) {
    if (-not $values.ContainsKey($required) -or [string]::IsNullOrWhiteSpace($values[$required])) {
        throw "The .env file is missing $required."
    }
}

if ([string]::IsNullOrWhiteSpace($OffsiteRemote) -and -not $AllowLocalOnly) {
    throw "Configure SMARTPRINT_BACKUP_RCLONE_REMOTE (for example remote:smartprint/backups), or explicitly pass -AllowLocalOnly for a local encrypted backup only."
}
if (-not [string]::IsNullOrWhiteSpace($OffsiteRemote)) {
    if (-not (Get-Command rclone -ErrorAction SilentlyContinue)) {
        throw "rclone is required for the configured off-site backup destination."
    }
    rclone version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "rclone could not be started." }
}

$passphrase = Read-Host "Enter a backup encryption passphrase (at least 16 characters). Store it separately; it cannot be recovered." -AsSecureString
$confirmation = $null
try {
    $confirmation = Read-Host "Re-enter the backup encryption passphrase" -AsSecureString
    if (-not (Test-SmartPrintBackupPassphrasesEqual -First $passphrase -Second $confirmation)) {
        throw "The backup passphrases did not match or were shorter than 16 characters. Nothing was stopped or changed."
    }
}
catch {
    $passphrase.Dispose()
    throw
}
finally {
    if ($null -ne $confirmation) { $confirmation.Dispose() }
}

$servicesStopped = $false
$backupContainer = $null
$stagingDirectory = $null
$remoteDestination = $null
try {
    New-Item -ItemType Directory -Force -Path $BackupDirectory | Out-Null
    $resolvedBackup = (Resolve-Path -LiteralPath $BackupDirectory).Path
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $resolvedBackup /inheritance:r /grant:r "$($identity):(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" "*S-1-5-18:(OI)(CI)(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not restrict backup-directory permissions. Choose a protected backup directory and retry."
    }

    $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
    $bundleName = "SmartPrint-$stamp.spbackup"
    $bundlePath = Join-Path $resolvedBackup $bundleName
    $stagingDirectory = Join-Path $resolvedBackup (".staging-$stamp-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    & icacls.exe $stagingDirectory /inheritance:r /grant:r "$($identity):(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" "*S-1-5-18:(OI)(CI)(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict temporary backup staging permissions." }

    $dbFile = Join-Path $stagingDirectory "qrtoprint-database-$stamp.dump"
    $docsFile = Join-Path $stagingDirectory "qrtoprint-documents-$stamp.tar.gz"
    $secretsFile = Join-Path $stagingDirectory "qrtoprint-secrets-$stamp.env"
    $tunnelTokenFile = Join-Path $stagingDirectory "cloudflare-tunnel-token"
    $manifestFile = Join-Path $stagingDirectory "manifest.json"
    $plainBundle = Join-Path $stagingDirectory "SmartPrint-$stamp.bundle.zip"
    $tempDbFile = "/tmp/qrtoprint-backup-$stamp.dump"
    $tempDocsFile = "/tmp/qrtoprint-documents-$stamp.tar.gz"

    Write-Host "Pausing API and portal writes for a consistent backup..." -ForegroundColor Cyan
    docker compose @composeBaseArgs stop api portal
    if ($LASTEXITCODE -ne 0) { throw "Could not pause API and portal services." }
    $servicesStopped = $true
    $snapshotUtc = (Get-Date).ToUniversalTime()

    Write-Host "Exporting PostgreSQL..." -ForegroundColor Cyan
    $dbUser = $values["POSTGRES_USER"]
    $dbName = $values["POSTGRES_DB"]
    docker compose @composeBaseArgs exec -T db pg_dump -U $dbUser -d $dbName -Fc -f $tempDbFile
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL export failed." }
    docker compose @composeBaseArgs cp "db:$tempDbFile" $dbFile
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dbFile -PathType Leaf)) {
        throw "Could not copy the PostgreSQL backup into the restricted staging area."
    }
    docker compose @composeBaseArgs exec -T db rm -f $tempDbFile | Out-Null

    Write-Host "Archiving encrypted cloud documents..." -ForegroundColor Cyan
    $apiContainer = docker compose @composeBaseArgs ps -a -q api
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($apiContainer)) {
        throw "Could not locate the API container's persistent document volume."
    }

    $documentsVolumeFormat = '{{range .Mounts}}{{if eq .Destination "/var/lib/qrtoprint/documents"}}{{.Name}}{{end}}{{end}}'
    $documentsVolume = docker inspect --format $documentsVolumeFormat $apiContainer.Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($documentsVolume)) {
        throw "Could not identify the API's persistent cloud-document volume."
    }
    $backupContainer = docker create --mount "type=volume,source=$($documentsVolume.Trim()),destination=/var/lib/qrtoprint/documents,readonly" alpine:3.20 sh -c "tar -czf $tempDocsFile -C /var/lib/qrtoprint/documents ."
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($backupContainer)) {
        throw "Could not create the read-only document archive helper."
    }
    docker start -a $backupContainer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cloud document archive failed." }
    docker cp ($backupContainer.Trim() + ":" + $tempDocsFile) $docsFile
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $docsFile -PathType Leaf)) {
        throw "Could not copy the document archive into the restricted staging area."
    }
    docker rm -f $backupContainer | Out-Null
    $backupContainer = $null

    Copy-Item -LiteralPath $envPath -Destination $secretsFile -Force
    Copy-Item -LiteralPath $tunnelTokenSource -Destination $tunnelTokenFile -Force

    $dbHash = (Get-FileHash -LiteralPath $dbFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $docsHash = (Get-FileHash -LiteralPath $docsFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $secretsHash = (Get-FileHash -LiteralPath $secretsFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $tunnelTokenHash = (Get-FileHash -LiteralPath $tunnelTokenFile -Algorithm SHA256).Hash.ToLowerInvariant()
    @{
        Format = "SmartPrint.BackupBundle.v2"
        CreatedUtc = (Get-Date).ToUniversalTime().ToString("O")
        SnapshotUtc = $snapshotUtc.ToString("O")
        DatabaseFile = [IO.Path]::GetFileName($dbFile)
        DatabaseSha256 = $dbHash
        DocumentsFile = [IO.Path]::GetFileName($docsFile)
        DocumentsSha256 = $docsHash
        SecretsFile = [IO.Path]::GetFileName($secretsFile)
        SecretsSha256 = $secretsHash
        TunnelTokenFile = [IO.Path]::GetFileName($tunnelTokenFile)
        TunnelTokenSha256 = $tunnelTokenHash
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestFile -Encoding utf8

    Write-Host "Creating the complete streamed backup archive..." -ForegroundColor Cyan
    $archiveResult = New-SmartPrintBackupArchive -InputFiles @($dbFile, $docsFile, $secretsFile, $tunnelTokenFile, $manifestFile) -OutputPath $plainBundle
    if (-not (Test-Path -LiteralPath $plainBundle -PathType Leaf)) {
        throw "Could not assemble the complete backup archive."
    }

    Write-Host "Encrypting the complete recovery set..." -ForegroundColor Cyan
    $encryptedResult = Protect-SmartPrintBackup -InputPath $plainBundle -OutputPath $bundlePath -Passphrase $passphrase
    $bundleHash = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant()

    if (-not [string]::IsNullOrWhiteSpace($OffsiteRemote)) {
        $remoteDestination = $OffsiteRemote.TrimEnd("/") + "/" + $bundleName
        Write-Host "Uploading the encrypted bundle to off-site storage..." -ForegroundColor Cyan
        rclone copyto --immutable $bundlePath $remoteDestination
        if ($LASTEXITCODE -ne 0) {
            throw "Off-site copy failed. The encrypted local bundle remains at $bundlePath; retry the copy before treating the backup as complete."
        }

        $remoteJson = rclone lsjson --stat $remoteDestination
        if ($LASTEXITCODE -ne 0) { throw "Could not verify that the off-site backup object exists." }
        $remoteInfo = ($remoteJson -join [Environment]::NewLine) | ConvertFrom-Json
        $localSize = (Get-Item -LiteralPath $bundlePath).Length
        if ([int64]$remoteInfo.Size -ne [int64]$localSize) {
            throw "The off-site backup size does not match the encrypted local bundle. Do not count this as a completed off-site backup."
        }
        rclone check --download $bundlePath $remoteDestination
        if ($LASTEXITCODE -ne 0) {
            throw "Off-site copy content verification failed. Do not count this as a completed off-site backup."
        }
    }
    elseif (-not $AllowLocalOnly) {
        throw "An encrypted local bundle was created, but an off-site target was not configured."
    }

    Write-Host ""
    Write-Host "SmartPrint encrypted backup created:" -ForegroundColor Green
    Write-Host "File: $bundlePath"
    Write-Host "SHA-256: $bundleHash"
    Write-Host "Plaintext input bytes protected: $($encryptedResult.PlaintextBytes)"
    Write-Host "Archive entries: $($archiveResult.EntryCount)"
    if ($remoteDestination) {
        Write-Host "Off-site copy size and content verified: $remoteDestination"
    } else {
        Write-Warning "LOCAL-ONLY BACKUP: this file is encrypted but is not off-site. Copy it to independent protected storage."
    }
    Write-Host "Keep the passphrase separately. SmartPrint cannot recover a forgotten backup passphrase."
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($backupContainer)) { docker rm -f $backupContainer | Out-Null }
    if ($servicesStopped) {
        Write-Host "Restarting API and portal..." -ForegroundColor Cyan
        docker compose @composeBaseArgs start api portal
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Backup finished but the API/portal restart failed. Run docker compose up -d api portal."
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($stagingDirectory) -and (Test-Path -LiteralPath $stagingDirectory)) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    $passphrase.Dispose()
}
