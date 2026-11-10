#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$BackupFile,
    [Security.SecureString]$Passphrase,
    [string]$EvidenceDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts/backup-restore-drill")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot "SmartPrint.BackupCrypto.psm1") -Force

$verifierSource = @'
using System;
using System.IO;
using System.Security.Cryptography;

public static class SmartPrintQsp1Verifier
{
    public static bool Verify(string path, byte[] key, string expectedSha256, long expectedSize)
    {
        if (key == null || key.Length != 32) throw new ArgumentException("A 32-byte document key is required.");
        var encrypted = File.ReadAllBytes(path);
        byte[] plaintext = null;
        try
        {
            if (encrypted.Length < 32 ||
                encrypted[0] != (byte)'Q' || encrypted[1] != (byte)'S' ||
                encrypted[2] != (byte)'P' || encrypted[3] != (byte)'1')
                return false;

            var nonce = new byte[12];
            var tag = new byte[16];
            Buffer.BlockCopy(encrypted, 4, nonce, 0, nonce.Length);
            Buffer.BlockCopy(encrypted, 16, tag, 0, tag.Length);
            var cipherLength = encrypted.Length - 32;
            var ciphertext = new byte[cipherLength];
            Buffer.BlockCopy(encrypted, 32, ciphertext, 0, cipherLength);
            plaintext = new byte[cipherLength];

            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(nonce, ciphertext, tag, plaintext);

            if (plaintext.LongLength != expectedSize) return false;
            var actual = SHA256.HashData(plaintext);
            byte[] expected;
            try { expected = Convert.FromHexString(expectedSha256); }
            catch (FormatException) { return false; }
            try { return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(actual, expected); }
            finally { CryptographicOperations.ZeroMemory(actual); CryptographicOperations.ZeroMemory(expected); }
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }
}
'@
if (-not ("SmartPrintQsp1Verifier" -as [type])) {
    Add-Type -TypeDefinition $verifierSource -Language CSharp -ErrorAction Stop
}

if (-not (Test-Path -LiteralPath $BackupFile -PathType Leaf)) {
    throw "Encrypted backup file was not found: $BackupFile"
}
$resolvedBackup = (Resolve-Path -LiteralPath $BackupFile).Path
if ([IO.Path]::GetExtension($resolvedBackup) -ne ".spbackup") {
    throw "Restore drills accept only authenticated .spbackup bundles."
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker was not found." }
docker version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker is unavailable." }

if ($null -eq $Passphrase) {
    $Passphrase = Read-Host "Enter the passphrase used to encrypt this SmartPrint backup" -AsSecureString
}
if ($Passphrase.Length -lt 16) { throw "The backup passphrase must contain at least 16 characters." }

$runId = [Guid]::NewGuid().ToString("N")
$stageDirectory = Join-Path ([IO.Path]::GetTempPath()) ("SmartPrintRestoreDrill-" + $runId)
New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $stageDirectory /inheritance:r /grant:r "$($identity):(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)" "*S-1-5-18:(OI)(CI)(F)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not restrict the temporary restore-drill directory." }

$plainBundle = Join-Path $stageDirectory "SmartPrint.bundle.zip"
$extractDirectory = Join-Path $stageDirectory "bundle"
New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
$databaseContainer = "smartprint-restore-drill-db-$($runId.Substring(0,12))"
$documentContainer = "smartprint-restore-drill-docs-$($runId.Substring(0,12))"
$documentVolume = "smartprint-restore-drill-volume-$($runId.Substring(0,12))"
$databaseCreated = $false
$documentsCreated = $false
$volumeCreated = $false
$completed = $false
$startedUtc = (Get-Date).ToUniversalTime()
$documentCount = 0
$tableCount = 0
$fileCount = 0
$backupCreatedUtc = $null
$reportPath = $null
$documentKey = $null

try {
    Write-Host "Decrypting and authenticating backup..." -ForegroundColor Cyan
    $decrypt = Unprotect-SmartPrintBackup -InputPath $resolvedBackup -OutputPath $plainBundle -Passphrase $Passphrase
    if (-not $decrypt.Authenticated) { throw "Backup authentication did not complete." }
    Expand-Archive -LiteralPath $plainBundle -DestinationPath $extractDirectory -Force

    $manifestPath = Join-Path $extractDirectory "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Backup manifest is missing." }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Format -ne "SmartPrint.BackupBundle.v2") { throw "Unsupported backup manifest format or incomplete recovery secret set." }
    $backupCreatedUtc = [DateTimeOffset]::Parse([string]$manifest.CreatedUtc)

    $databaseFile = Join-Path $extractDirectory ([string]$manifest.DatabaseFile)
    $documentsFile = Join-Path $extractDirectory ([string]$manifest.DocumentsFile)
    $secretsFile = Join-Path $extractDirectory ([string]$manifest.SecretsFile)
    $tunnelTokenFile = Join-Path $extractDirectory ([string]$manifest.TunnelTokenFile)
    foreach ($file in @($databaseFile, $documentsFile, $secretsFile, $tunnelTokenFile)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "A required backup component is missing: $([IO.Path]::GetFileName($file))" }
    }

    if ((Get-FileHash -LiteralPath $databaseFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.DatabaseSha256 -or
        (Get-FileHash -LiteralPath $documentsFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.DocumentsSha256 -or
        (Get-FileHash -LiteralPath $secretsFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.SecretsSha256 -or
        (Get-FileHash -LiteralPath $tunnelTokenFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.TunnelTokenSha256) {
        throw "A backup component checksum does not match the authenticated manifest."
    }

    $values = @{}
    foreach ($line in (Get-Content -LiteralPath $secretsFile)) {
        if ($line -match '^([A-Z0-9_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
    }
    foreach ($required in @("POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD", "DOCUMENTS_ENCRYPTION_KEY", "AUTH_SIGNING_KEY")) {
        if (-not $values.ContainsKey($required) -or [string]::IsNullOrWhiteSpace($values[$required])) {
            throw "The backup recovery secrets are missing $required."
        }
    }

    $dbName = $values["POSTGRES_DB"]
    $dbUser = $values["POSTGRES_USER"]
    $dbPassword = $values["POSTGRES_PASSWORD"]
    if ((Get-Item -LiteralPath $tunnelTokenFile).Length -lt 16) {
        throw "The authenticated Cloudflare Tunnel token is missing or unexpectedly short."
    }
    $documentKey = [Convert]::FromBase64String($values["DOCUMENTS_ENCRYPTION_KEY"])
    if ($documentKey.Length -ne 32) { throw "The backup document-encryption key is not 32 bytes." }

    Write-Host "Starting disposable PostgreSQL restore target (no published ports, isolated network)..." -ForegroundColor Cyan
    $databaseContainer = docker create --name $databaseContainer --network none -e "POSTGRES_DB=$dbName" -e "POSTGRES_USER=$dbUser" -e "POSTGRES_PASSWORD=$dbPassword" postgres:17-alpine
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($databaseContainer)) {
        throw "Could not create an isolated PostgreSQL restore container."
    }
    $databaseCreated = $true
    docker start $databaseContainer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not start the disposable PostgreSQL container." }

    $databaseReady = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        docker exec $databaseContainer pg_isready -U $dbUser -d $dbName | Out-Null
        if ($LASTEXITCODE -eq 0) { $databaseReady = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $databaseReady) { throw "The disposable PostgreSQL instance did not become ready." }

    docker cp $databaseFile ($databaseContainer + ":/tmp/smartprint-restore.dump")
    if ($LASTEXITCODE -ne 0) { throw "Could not copy the database dump into the isolated restore target." }
    docker exec $databaseContainer pg_restore --exit-on-error --no-owner --no-privileges -U $dbUser -d $dbName /tmp/smartprint-restore.dump
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL could not restore the backup into the isolated target." }

    $tableCountText = docker exec $databaseContainer psql -U $dbUser -d $dbName -At -c "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public';"
    if ($LASTEXITCODE -ne 0 -or -not [int]::TryParse(($tableCountText -join "").Trim(), [ref]$tableCount)) {
        throw "Could not verify the restored PostgreSQL schema."
    }
    if ($tableCount -le 0) { throw "The restored database contains no public application tables." }

    Write-Host "Restoring document archive into a disposable Docker volume..." -ForegroundColor Cyan
    $documentVolume = docker volume create $documentVolume
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($documentVolume)) {
        throw "Could not create an isolated document-volume restore target."
    }
    $volumeCreated = $true
    $documentContainer = docker create --name $documentContainer --network none --mount "type=volume,source=$documentVolume,destination=/restore" alpine:3.20 sh -c "mkdir -p /restore && tar -xzf /tmp/smartprint-documents.tar.gz -C /restore"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($documentContainer)) {
        throw "Could not create the isolated document restore helper."
    }
    $documentsCreated = $true
    docker cp $documentsFile ($documentContainer + ":/tmp/smartprint-documents.tar.gz")
    if ($LASTEXITCODE -ne 0) { throw "Could not copy the document archive into the isolated restore helper." }
    docker start -a $documentContainer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The document archive did not extract successfully." }

    $fileCountText = docker exec $documentContainer sh -c "find /restore -type f | wc -l"
    if ($LASTEXITCODE -ne 0 -or -not [int]::TryParse(($fileCountText -join "").Trim(), [ref]$fileCount)) {
        throw "Could not inspect the restored document archive."
    }

    $documentSql = 'SELECT "StorageKey" || chr(9) || "Sha256" || chr(9) || "SizeBytes" FROM "PrintDocuments" WHERE "StorageMode" = ''CLOUD'' AND "PurgedUtc" IS NULL ORDER BY "StorageKey";'
    $documentRows = @(docker exec $databaseContainer psql -U $dbUser -d $dbName -At -c $documentSql)
    if ($LASTEXITCODE -ne 0) { throw "Could not read active cloud document references from the restored database." }
    $tab = ([char]9).ToString()

    foreach ($row in $documentRows) {
        if ([string]::IsNullOrWhiteSpace($row)) { continue }
        $parts = $row -split [regex]::Escape($tab), 3
        if ($parts.Count -ne 3) { throw "The restored document metadata row is malformed." }
        $storageKey = $parts[0]
        $sha256 = $parts[1]
        [long]$sizeBytes = 0
        if (-not [long]::TryParse($parts[2], [ref]$sizeBytes)) { throw "A restored document has an invalid byte count." }
        if ($storageKey -notmatch '^[0-9a-f]{32}/[0-9a-f]{32}\.(pdf|jpg|jpeg|png)$') {
            throw "A restored cloud document has an unsafe or unexpected storage key."
        }

        $localDocument = Join-Path $stageDirectory ("document-" + [Guid]::NewGuid().ToString("N") + ".qsp")
        docker cp ($documentContainer + ":/restore/" + $storageKey) $localDocument
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $localDocument -PathType Leaf)) {
            throw "A database document reference is missing from the restored encrypted document archive: $storageKey"
        }
        if (-not [SmartPrintQsp1Verifier]::Verify($localDocument, $documentKey, $sha256, $sizeBytes)) {
            throw "A restored document failed its AES-GCM authentication, size or SHA-256 check: $storageKey"
        }
        Remove-Item -LiteralPath $localDocument -Force
        $documentCount++
    }

    if ($documentCount -gt $fileCount) {
        throw "The restored database references more cloud documents than the archive contains."
    }

    $completedUtc = (Get-Date).ToUniversalTime()
    $elapsedSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 2)
    $rpoMinutes = [Math]::Round(($startedUtc - $backupCreatedUtc.UtcDateTime).TotalMinutes, 2)
    New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
    $reportPath = Join-Path $EvidenceDirectory ("restore-drill-" + $completedUtc.ToString("yyyyMMdd-HHmmss") + ".json")
    @{
        Format = "SmartPrint.RestoreDrillEvidence.v1"
        BackupFile = [IO.Path]::GetFileName($resolvedBackup)
        BackupSha256 = (Get-FileHash -LiteralPath $resolvedBackup -Algorithm SHA256).Hash.ToLowerInvariant()
        BackupCreatedUtc = $backupCreatedUtc.ToUniversalTime().ToString("O")
        StartedUtc = $startedUtc.ToString("O")
        CompletedUtc = $completedUtc.ToString("O")
        RestoreDurationSeconds = $elapsedSeconds
        RecoveryPointAgeMinutes = $rpoMinutes
        RestoredPublicTableCount = $tableCount
        RestoredDocumentArchiveFileCount = $fileCount
        DatabaseReferencedDocumentsVerified = $documentCount
        DatabaseRestorePassed = $true
        DocumentArchiveRestorePassed = $true
        CloudflareTunnelRecoverySecretPresentAndAuthenticated = $true
        ReferencedQsp1AesGcmDocumentsAuthenticated = $true
        ProductionContainersOrVolumesTouched = $false
        ExternalNetworksOrPortsUsed = $false
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8

    $completed = $true
    Write-Host ""
    Write-Host "PASS: isolated SmartPrint backup restore drill completed." -ForegroundColor Green
    Write-Host "Database tables restored: $tableCount"
    Write-Host "Encrypted documents authenticated against database metadata: $documentCount"
    Write-Host "Restore duration: $elapsedSeconds seconds"
    Write-Host "Recovery-point age at drill start: $rpoMinutes minutes"
    Write-Host "Evidence: $reportPath"
}
finally {
    if ($documentsCreated) { docker rm -f $documentContainer | Out-Null }
    if ($databaseCreated) { docker rm -f $databaseContainer | Out-Null }
    if ($volumeCreated) { docker volume rm $documentVolume | Out-Null }
    if (Test-Path -LiteralPath $stageDirectory) {
        Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $Passphrase) { $Passphrase.Dispose() }
    [SmartPrintBackupCryptoV1]::Zero($documentKey)
    if (-not $completed) {
        Write-Warning "Restore drill did not pass. Any disposable Docker container and volume created by this run have been removed."
    }
}
