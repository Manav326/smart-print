[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Import-Module (Join-Path $repoRoot "scripts/SmartPrint.BackupCrypto.psm1") -Force

$root = Join-Path ([IO.Path]::GetTempPath()) ("smartprint-backup-crypto-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try { & $Action } catch { $threw = $true }
    Assert-True $threw $Message
}

try {
    $source = Join-Path $root "source.bundle"
    $encrypted = Join-Path $root "backup.spbackup"
    $restored = Join-Path $root "restored.bundle"
    $wrongOutput = Join-Path $root "wrong-passphrase.bundle"
    $tamperedOutput = Join-Path $root "tampered.bundle"
    $tampered = Join-Path $root "tampered.spbackup"

    # More than one 4 MiB encryption frame, including a short final frame.
    $payload = [byte[]]::new((5 * 1024 * 1024) + 731)
    [Security.Cryptography.RandomNumberGenerator]::Fill($payload)
    [IO.File]::WriteAllBytes($source, $payload)
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash

    $passphrase = ConvertTo-SecureString "correct horse battery stable 2026" -AsPlainText -Force
    $wrongPassphrase = ConvertTo-SecureString "incorrect horse battery stable 2026" -AsPlainText -Force
    $mismatch = ConvertTo-SecureString "correct horse battery stable 2027" -AsPlainText -Force

    Assert-True (Test-SmartPrintBackupPassphrasesEqual -First $passphrase -Second (ConvertTo-SecureString "correct horse battery stable 2026" -AsPlainText -Force)) "constant-time passphrase comparison should accept equal values"
    Assert-True (-not (Test-SmartPrintBackupPassphrasesEqual -First $passphrase -Second $mismatch)) "passphrase comparison should reject unequal values"
    Assert-True (-not (Test-SmartPrintBackupPassphrasesEqual -First (ConvertTo-SecureString "short" -AsPlainText -Force) -Second $passphrase)) "short passphrases must be rejected"

    $protection = Protect-SmartPrintBackup -InputPath $source -OutputPath $encrypted -Passphrase $passphrase
    Assert-True ($protection.Format -eq "SPBKUP01") "encrypted file should declare SPBKUP01 format"
    Assert-True ($protection.Cipher -eq "AES-256-GCM") "backup should use AES-256-GCM"
    Assert-True ($protection.Kdf -eq "PBKDF2-HMAC-SHA256") "backup should derive its key with PBKDF2-SHA256"
    Assert-True ($protection.Iterations -ge 600000) "backup should use the configured PBKDF2 work factor"
    Assert-True ($protection.ChunkCount -eq 2) "payload should be split across two authenticated chunks"
    Assert-True ((Get-FileHash -LiteralPath $encrypted -Algorithm SHA256).Hash -ne $sourceHash) "encrypted backup should differ from plaintext source"

    $unprotection = Unprotect-SmartPrintBackup -InputPath $encrypted -OutputPath $restored -Passphrase $passphrase
    Assert-True ($unprotection.Authenticated) "valid footer should authenticate"
    Assert-True ($unprotection.ChunkCount -eq 2) "decrypted footer should verify chunk count"
    Assert-True ((Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash -eq $sourceHash) "round-trip should reproduce exact source bytes"

    Assert-Throws { Unprotect-SmartPrintBackup -InputPath $encrypted -OutputPath $wrongOutput -Passphrase $wrongPassphrase } "wrong passphrase must fail authentication"
    Assert-True (-not (Test-Path -LiteralPath $wrongOutput)) "wrong passphrase must not leave a partial plaintext output"

    $tamperedBytes = [IO.File]::ReadAllBytes($encrypted)
    $tamperedBytes[50] = $tamperedBytes[50] -bxor 0x40
    [IO.File]::WriteAllBytes($tampered, $tamperedBytes)
    Assert-Throws { Unprotect-SmartPrintBackup -InputPath $tampered -OutputPath $tamperedOutput -Passphrase $passphrase } "tampered ciphertext must fail authentication"
    Assert-True (-not (Test-Path -LiteralPath $tamperedOutput)) "tampered ciphertext must not leave a partial plaintext output"

    $truncated = Join-Path $root "truncated.spbackup"
    [IO.File]::WriteAllBytes($truncated, [IO.File]::ReadAllBytes($encrypted)[0..([IO.File]::ReadAllBytes($encrypted).Length - 20)])
    Assert-Throws { Unprotect-SmartPrintBackup -InputPath $truncated -OutputPath (Join-Path $root "truncated.bundle") -Passphrase $passphrase } "truncated backup must fail footer authentication"

    Write-Host "PASS: backup crypto round-trip, multi-chunk framing, wrong passphrase, ciphertext tampering, truncated footer and no partial plaintext output."
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
