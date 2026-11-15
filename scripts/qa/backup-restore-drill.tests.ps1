#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Import-Module (Join-Path $repoRoot "scripts/SmartPrint.BackupCrypto.psm1") -Force

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "Docker is required for the isolated restore-drill integration test." }
docker version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker daemon is not available for the restore-drill integration test." }

$runId = [Guid]::NewGuid().ToString("N")
$root = Join-Path ([IO.Path]::GetTempPath()) ("smartprint-restore-drill-test-" + $runId)
$documentsRoot = Join-Path $root "documents"
$bundleRoot = Join-Path $root "bundle-input"
$evidenceRoot = Join-Path $root "evidence"
New-Item -ItemType Directory -Path $documentsRoot, $bundleRoot, $evidenceRoot -Force | Out-Null

$sourceContainer = "smartprint-restore-drill-source-$($runId.Substring(0,12))"
$sourceStarted = $false
$documentKey = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$passphrase = ConvertTo-SecureString "SmartPrint restore drill test passphrase 2026" -AsPlainText -Force
$databaseName = "smartprint_restore_test"
$databaseUser = "smartprint_restore"
$databasePassword = "restore-drill-only-password"
$tenantId = [Guid]::NewGuid().ToString("N").ToLowerInvariant()
$documentId = [Guid]::NewGuid().ToString("N").ToLowerInvariant()
$storageKey = "$tenantId/$documentId.pdf"
$bundleFile = Join-Path $root "SmartPrint-synthetic.spbackup"
$plainDocument = $null
$encryptedDocument = $null
$ciphertext = $null
$tag = $null
$nonce = $null

try {
    Write-Host "Creating a disposable PostgreSQL source database..." -ForegroundColor Cyan
    $created = docker create --name $sourceContainer --network none -e "POSTGRES_DB=$databaseName" -e "POSTGRES_USER=$databaseUser" -e "POSTGRES_PASSWORD=$databasePassword" postgres:17-alpine
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($created)) { throw "Could not create source PostgreSQL container." }
    docker start $sourceContainer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not start source PostgreSQL container." }
    $sourceStarted = $true

    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        docker exec $sourceContainer pg_isready -U $databaseUser -d $databaseName | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw "Source PostgreSQL did not become ready." }

    $createTable = 'CREATE TABLE "PrintDocuments" ("Id" uuid PRIMARY KEY, "StorageKey" text NOT NULL, "Sha256" text NOT NULL, "SizeBytes" bigint NOT NULL, "StorageMode" text NOT NULL, "PurgedUtc" timestamptz NULL);'
    docker exec $sourceContainer psql -v ON_ERROR_STOP=1 -U $databaseUser -d $databaseName -c $createTable | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create synthetic PrintDocuments table." }

    $plainDocument = [System.Text.Encoding]::UTF8.GetBytes("SmartPrint isolated restore drill document payload $runId")
    $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($plainDocument)).ToLowerInvariant()
    $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
    $tag = [byte[]]::new(16)
    $ciphertext = [byte[]]::new($plainDocument.Length)
    $aes = [Security.Cryptography.AesGcm]::new($documentKey, 16)
    try { $aes.Encrypt($nonce, $plainDocument, $ciphertext, $tag) }
    finally { $aes.Dispose() }

    $encryptedDocument = [byte[]]::new(4 + 12 + 16 + $ciphertext.Length)
    [System.Text.Encoding]::ASCII.GetBytes("QSP1").CopyTo($encryptedDocument, 0)
    $nonce.CopyTo($encryptedDocument, 4)
    $tag.CopyTo($encryptedDocument, 16)
    $ciphertext.CopyTo($encryptedDocument, 32)
    $documentPath = Join-Path $documentsRoot $storageKey.Replace("/", [IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path (Split-Path -Parent $documentPath) -Force | Out-Null
    [IO.File]::WriteAllBytes($documentPath, $encryptedDocument)

    $insert = @"
INSERT INTO "PrintDocuments" ("Id", "StorageKey", "Sha256", "SizeBytes", "StorageMode", "PurgedUtc")
VALUES ('$documentId'::uuid, '$storageKey', '$sha256', $($plainDocument.Length), 'CLOUD', NULL);
"@
    docker exec $sourceContainer psql -v ON_ERROR_STOP=1 -U $databaseUser -d $databaseName -c $insert | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not seed synthetic document metadata." }

    Write-Host "Creating a PostgreSQL custom-format dump..." -ForegroundColor Cyan
    docker exec $sourceContainer pg_dump -U $databaseUser -d $databaseName -Fc -f /tmp/smartprint-restore-drill.dump
    if ($LASTEXITCODE -ne 0) { throw "Could not create synthetic PostgreSQL dump." }
    $databaseFile = Join-Path $bundleRoot "qrtoprint-database-$runId.dump"
    docker cp ($sourceContainer + ":/tmp/smartprint-restore-drill.dump") $databaseFile
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $databaseFile -PathType Leaf)) { throw "Could not copy synthetic database dump." }

    $documentsFile = Join-Path $bundleRoot "qrtoprint-documents-$runId.tar.gz"
    & tar -czf $documentsFile -C $documentsRoot .
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $documentsFile -PathType Leaf)) { throw "Could not archive synthetic encrypted document." }

    $secretsFile = Join-Path $bundleRoot "qrtoprint-secrets-$runId.env"
    $documentKeyBase64 = [Convert]::ToBase64String($documentKey)
    @(
        "POSTGRES_DB=$databaseName"
        "POSTGRES_USER=$databaseUser"
        "POSTGRES_PASSWORD=$databasePassword"
        "DOCUMENTS_ENCRYPTION_KEY=$documentKeyBase64"
        "AUTH_SIGNING_KEY=synthetic-restore-drill-auth-signing-key-2026"
    ) | Set-Content -LiteralPath $secretsFile -Encoding utf8

    $tunnelTokenFile = Join-Path $bundleRoot "cloudflare-tunnel-token"
    [IO.File]::WriteAllText($tunnelTokenFile, "synthetic-cloudflare-tunnel-token-$runId")

    $manifestFile = Join-Path $bundleRoot "manifest.json"
    @{
        Format = "SmartPrint.BackupBundle.v2"
        CreatedUtc = (Get-Date).ToUniversalTime().ToString("O")
        SnapshotUtc = (Get-Date).ToUniversalTime().AddMinutes(-1).ToString("O")
        DatabaseFile = [IO.Path]::GetFileName($databaseFile)
        DatabaseSha256 = (Get-FileHash -LiteralPath $databaseFile -Algorithm SHA256).Hash.ToLowerInvariant()
        DocumentsFile = [IO.Path]::GetFileName($documentsFile)
        DocumentsSha256 = (Get-FileHash -LiteralPath $documentsFile -Algorithm SHA256).Hash.ToLowerInvariant()
        SecretsFile = [IO.Path]::GetFileName($secretsFile)
        SecretsSha256 = (Get-FileHash -LiteralPath $secretsFile -Algorithm SHA256).Hash.ToLowerInvariant()
        TunnelTokenFile = [IO.Path]::GetFileName($tunnelTokenFile)
        TunnelTokenSha256 = (Get-FileHash -LiteralPath $tunnelTokenFile -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestFile -Encoding utf8

    $plainBundle = Join-Path $root "SmartPrint-synthetic.bundle.zip"
    New-SmartPrintBackupArchive -InputFiles @($databaseFile, $documentsFile, $secretsFile, $tunnelTokenFile, $manifestFile) -OutputPath $plainBundle | Out-Null
    Protect-SmartPrintBackup -InputPath $plainBundle -OutputPath $bundleFile -Passphrase $passphrase | Out-Null

    Write-Host "Running the production restore-drill script against disposable Docker resources..." -ForegroundColor Cyan
    & (Join-Path $repoRoot "scripts/docker-restore-drill.ps1") -BackupFile $bundleFile -Passphrase $passphrase -EvidenceDirectory $evidenceRoot

    $evidence = Get-ChildItem -LiteralPath $evidenceRoot -Filter "restore-drill-*.json" -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $evidence) { throw "The restore drill did not produce an evidence report." }
    $report = Get-Content -LiteralPath $evidence.FullName -Raw | ConvertFrom-Json
    if ($report.DatabaseRestorePassed -ne $true -or
        $report.DocumentArchiveRestorePassed -ne $true -or
        $report.ReferencedQsp1AesGcmDocumentsAuthenticated -ne $true -or
        $report.CloudflareTunnelRecoverySecretPresentAndAuthenticated -ne $true -or
        $report.DatabaseReferencedDocumentsVerified -ne 1 -or
        $report.ProductionContainersOrVolumesTouched -ne $false) {
        throw "The isolated restore evidence did not satisfy every required recovery assertion."
    }

    Write-Host "PASS: synthetic PostgreSQL + encrypted-document restore drill verified one document end to end."
}
finally {
    if ($sourceStarted) { docker rm -f $sourceContainer | Out-Null }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
    [SmartPrintBackupCryptoV1]::Zero($documentKey)
    if ($null -ne $plainDocument) { [SmartPrintBackupCryptoV1]::Zero($plainDocument) }
    if ($null -ne $encryptedDocument) { [SmartPrintBackupCryptoV1]::Zero($encryptedDocument) }
    if ($null -ne $ciphertext) { [SmartPrintBackupCryptoV1]::Zero($ciphertext) }
    if ($null -ne $tag) { [SmartPrintBackupCryptoV1]::Zero($tag) }
    if ($null -ne $nonce) { [SmartPrintBackupCryptoV1]::Zero($nonce) }
    $passphrase.Dispose()
}
