$cryptoSource = @'
using System;
using System.IO;
using System.Security.Cryptography;

public sealed class SmartPrintBackupAesGcm : IDisposable
{
    private readonly AesGcm aes;

    public SmartPrintBackupAesGcm(byte[] key)
    {
        if (key == null || key.Length != 32) throw new ArgumentException("A 32-byte AES-256 key is required.", nameof(key));
        aes = new AesGcm(key, 16);
    }

    public void Encrypt(byte[] nonce, byte[] plaintext, byte[] ciphertext, byte[] tag, byte[] associatedData)
        => aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

    public void Decrypt(byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext, byte[] associatedData)
        => aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

    public void Dispose() => aes.Dispose();
}

public static class SmartPrintBackupCryptoV1
{
    public static byte[] DeriveKey(byte[] password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] MakeNonce(byte[] prefix, uint counter)
    {
        if (prefix == null || prefix.Length != 8) throw new ArgumentException("An eight-byte nonce prefix is required.");
        var nonce = new byte[12];
        Buffer.BlockCopy(prefix, 0, nonce, 0, 8);
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);
        Buffer.BlockCopy(counterBytes, 0, nonce, 8, 4);
        return nonce;
    }

    public static byte[] ChunkAad(byte[] header, uint counter, uint length)
    {
        var aad = new byte[header.Length + 8];
        Buffer.BlockCopy(header, 0, aad, 0, header.Length);
        WriteUInt32(aad, header.Length, counter);
        WriteUInt32(aad, header.Length + 4, length);
        return aad;
    }

    public static byte[] FooterAad(byte[] header, ulong totalBytes, uint chunkCount)
    {
        var aad = new byte[header.Length + 16];
        Buffer.BlockCopy(header, 0, aad, 0, header.Length);
        aad[header.Length] = (byte)'E';
        aad[header.Length + 1] = (byte)'N';
        aad[header.Length + 2] = (byte)'D';
        aad[header.Length + 3] = (byte)'1';
        WriteUInt64(aad, header.Length + 4, totalBytes);
        WriteUInt32(aad, header.Length + 12, chunkCount);
        return aad;
    }

    public static bool FixedTimeEquals(byte[] left, byte[] right)
        => left != null && right != null &&
           left.Length == right.Length &&
           CryptographicOperations.FixedTimeEquals(left, right);

    public static void Zero(byte[] value)
    {
        if (value != null) CryptographicOperations.ZeroMemory(value);
    }

    private static void WriteUInt32(byte[] destination, int offset, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        Buffer.BlockCopy(bytes, 0, destination, offset, 4);
    }

    private static void WriteUInt64(byte[] destination, int offset, ulong value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        Buffer.BlockCopy(bytes, 0, destination, offset, 8);
    }
}
'@

if (-not ("SmartPrintBackupCryptoV1" -as [type])) {
    Add-Type -TypeDefinition $cryptoSource -Language CSharp -ErrorAction Stop
}

$script:SmartPrintBackupMagic = [System.Text.Encoding]::ASCII.GetBytes("SPBKUP01")
$script:SmartPrintBackupIterations = 600000
$script:SmartPrintBackupChunkSize = 4MB
$script:SmartPrintBackupTagSize = 16
$script:SmartPrintBackupMinimumPassphraseLength = 16

function Get-SmartPrintBackupPasswordBytes {
    [CmdletBinding()]
    param([Parameter(Mandatory)][Security.SecureString]$Passphrase)

    if ($Passphrase.Length -lt $script:SmartPrintBackupMinimumPassphraseLength) {
        throw "The backup passphrase must contain at least $($script:SmartPrintBackupMinimumPassphraseLength) characters."
    }

    $pointer = [IntPtr]::Zero
    $characters = $null
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Passphrase)
        $characters = [char[]]::new($Passphrase.Length)
        for ($index = 0; $index -lt $characters.Length; $index++) {
            $characters[$index] = [char][ushort][Runtime.InteropServices.Marshal]::ReadInt16($pointer, $index * 2)
        }

        $bytes = [System.Text.Encoding]::UTF8.GetBytes($characters)
        Write-Output -NoEnumerate $bytes
    }
    finally {
        if ($null -ne $characters) { [Array]::Clear($characters, 0, $characters.Length) }
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)
        }
    }
}

function Test-SmartPrintBackupPassphrasesEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Security.SecureString]$First,
        [Parameter(Mandatory)][Security.SecureString]$Second
    )

    if ($First.Length -lt $script:SmartPrintBackupMinimumPassphraseLength -or
        $Second.Length -lt $script:SmartPrintBackupMinimumPassphraseLength) {
        return $false
    }

    $left = $null
    $right = $null
    try {
        $left = [byte[]](Get-SmartPrintBackupPasswordBytes -Passphrase $First)
        $right = [byte[]](Get-SmartPrintBackupPasswordBytes -Passphrase $Second)
        return [SmartPrintBackupCryptoV1]::FixedTimeEquals($left, $right)
    }
    finally {
        [SmartPrintBackupCryptoV1]::Zero($left)
        [SmartPrintBackupCryptoV1]::Zero($right)
    }
}

function Write-SmartPrintUInt32BigEndian {
    param([Parameter(Mandatory)][IO.Stream]$Stream, [Parameter(Mandatory)][uint32]$Value)
    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Write-SmartPrintUInt64BigEndian {
    param([Parameter(Mandatory)][IO.Stream]$Stream, [Parameter(Mandatory)][uint64]$Value)
    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Read-SmartPrintExactly {
    param([Parameter(Mandatory)][IO.Stream]$Stream, [Parameter(Mandatory)][int]$Count)
    $bytes = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($bytes, $offset, $Count - $offset)
        if ($read -le 0) { throw "The encrypted backup is truncated." }
        $offset += $read
    }
    Write-Output -NoEnumerate $bytes
}

function Read-SmartPrintUInt32BigEndian {
    param([Parameter(Mandatory)][IO.Stream]$Stream)
    $bytes = [byte[]](Read-SmartPrintExactly -Stream $Stream -Count 4)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    return [uint32][BitConverter]::ToUInt32($bytes, 0)
}

function Read-SmartPrintUInt64BigEndian {
    param([Parameter(Mandatory)][IO.Stream]$Stream)
    $bytes = [byte[]](Read-SmartPrintExactly -Stream $Stream -Count 8)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    return [uint64][BitConverter]::ToUInt64($bytes, 0)
}

function New-SmartPrintBackupHeader {
    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $noncePrefix = [Security.Cryptography.RandomNumberGenerator]::GetBytes(8)
    $stream = [IO.MemoryStream]::new()
    try {
        $stream.Write($script:SmartPrintBackupMagic, 0, $script:SmartPrintBackupMagic.Length)
        Write-SmartPrintUInt32BigEndian -Stream $stream -Value ([uint32]$script:SmartPrintBackupIterations)
        Write-SmartPrintUInt32BigEndian -Stream $stream -Value ([uint32]$script:SmartPrintBackupChunkSize)
        $stream.Write($salt, 0, $salt.Length)
        $stream.Write($noncePrefix, 0, $noncePrefix.Length)
        return @{
            Header = $stream.ToArray()
            Salt = $salt
            NoncePrefix = $noncePrefix
        }
    }
    finally { $stream.Dispose() }
}

function Protect-SmartPrintBackup {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$InputPath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputPath,
        [Parameter(Mandatory)][Security.SecureString]$Passphrase
    )

    $sourcePath = (Resolve-Path -LiteralPath $InputPath).Path
    $destinationPath = [IO.Path]::GetFullPath($OutputPath)
    if ([string]::Equals($sourcePath, $destinationPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The encrypted backup output must be different from its plaintext input."
    }
    if (Test-Path -LiteralPath $destinationPath) {
        throw "The encrypted backup already exists: $destinationPath"
    }

    $destinationDirectory = [IO.Path]::GetDirectoryName($destinationPath)
    if (-not (Test-Path -LiteralPath $destinationDirectory)) {
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    }
    $temporaryPath = "$destinationPath.partial-$([Guid]::NewGuid().ToString('N'))"
    $passwordBytes = $null
    $key = $null
    $aes = $null
    $plain = $null
    $cipher = $null
    $tag = $null
    $nonce = $null
    $aad = $null
    $input = $null
    $output = $null

    try {
        $passwordBytes = [byte[]](Get-SmartPrintBackupPasswordBytes -Passphrase $Passphrase)
        $metadata = New-SmartPrintBackupHeader
        $header = [byte[]]$metadata.Header
        $key = [SmartPrintBackupCryptoV1]::DeriveKey($passwordBytes, [byte[]]$metadata.Salt, $script:SmartPrintBackupIterations)
        $aes = [SmartPrintBackupAesGcm]::new($key)
        $input = [IO.File]::OpenRead($sourcePath)
        if ($input.Length -le 0) { throw "Refusing to encrypt an empty backup bundle." }
        $output = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $output.Write($header, 0, $header.Length)

        $buffer = [byte[]]::new($script:SmartPrintBackupChunkSize)
        [uint64]$totalBytes = 0
        [uint32]$chunkCount = 0

        while ($true) {
            $filled = 0
            while ($filled -lt $buffer.Length) {
                $read = $input.Read($buffer, $filled, $buffer.Length - $filled)
                if ($read -le 0) { break }
                $filled += $read
            }
            if ($filled -eq 0) { break }
            if ($chunkCount -eq [uint32]::MaxValue) { throw "The backup contains too many encryption chunks." }

            $plain = [byte[]]::new($filled)
            [Array]::Copy($buffer, 0, $plain, 0, $filled)
            $cipher = [byte[]]::new($filled)
            $tag = [byte[]]::new($script:SmartPrintBackupTagSize)
            $nonce = [SmartPrintBackupCryptoV1]::MakeNonce([byte[]]$metadata.NoncePrefix, $chunkCount)
            $aad = [SmartPrintBackupCryptoV1]::ChunkAad($header, $chunkCount, [uint32]$filled)
            $aes.Encrypt($nonce, $plain, $cipher, $tag, $aad)

            Write-SmartPrintUInt32BigEndian -Stream $output -Value ([uint32]$filled)
            $output.Write($cipher, 0, $cipher.Length)
            $output.Write($tag, 0, $tag.Length)
            $totalBytes = [checked]($totalBytes + [uint64]$filled)
            $chunkCount++

            [SmartPrintBackupCryptoV1]::Zero($plain)
            [SmartPrintBackupCryptoV1]::Zero($cipher)
            [SmartPrintBackupCryptoV1]::Zero($tag)
            [SmartPrintBackupCryptoV1]::Zero($nonce)
            [SmartPrintBackupCryptoV1]::Zero($aad)
            $plain = $null; $cipher = $null; $tag = $null; $nonce = $null; $aad = $null
        }

        # Authenticated footer makes deletion/reordering/truncation detectable.
        $tag = [byte[]]::new($script:SmartPrintBackupTagSize)
        $nonce = [SmartPrintBackupCryptoV1]::MakeNonce([byte[]]$metadata.NoncePrefix, $chunkCount)
        $aad = [SmartPrintBackupCryptoV1]::FooterAad($header, $totalBytes, $chunkCount)
        $empty = [byte[]]::new(0)
        $aes.Encrypt($nonce, $empty, $empty, $tag, $aad)
        Write-SmartPrintUInt32BigEndian -Stream $output -Value ([uint32]::MaxValue)
        Write-SmartPrintUInt64BigEndian -Stream $output -Value $totalBytes
        Write-SmartPrintUInt32BigEndian -Stream $output -Value $chunkCount
        $output.Write($tag, 0, $tag.Length)
        $output.Flush($true)
        $output.Dispose()
        $output = $null

        [IO.File]::Move($temporaryPath, $destinationPath)
        return [pscustomobject]@{
            Path = $destinationPath
            PlaintextBytes = $totalBytes
            EncryptedBytes = (Get-Item -LiteralPath $destinationPath).Length
            ChunkCount = $chunkCount
            Format = "SPBKUP01"
            Kdf = "PBKDF2-HMAC-SHA256"
            Iterations = $script:SmartPrintBackupIterations
            Cipher = "AES-256-GCM"
        }
    }
    catch {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue }
        throw
    }
    finally {
        if ($null -ne $input) { $input.Dispose() }
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $aes) { $aes.Dispose() }
        foreach ($bytes in @($passwordBytes, $key, $plain, $cipher, $tag, $nonce, $aad)) {
            [SmartPrintBackupCryptoV1]::Zero($bytes)
        }
    }
}

function Unprotect-SmartPrintBackup {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$InputPath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputPath,
        [Parameter(Mandatory)][Security.SecureString]$Passphrase
    )

    $sourcePath = (Resolve-Path -LiteralPath $InputPath).Path
    $destinationPath = [IO.Path]::GetFullPath($OutputPath)
    if ([string]::Equals($sourcePath, $destinationPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The decrypted backup output must be different from its encrypted input."
    }
    if (Test-Path -LiteralPath $destinationPath) {
        throw "The decrypted backup destination already exists: $destinationPath"
    }

    $destinationDirectory = [IO.Path]::GetDirectoryName($destinationPath)
    if (-not (Test-Path -LiteralPath $destinationDirectory)) {
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    }
    $temporaryPath = "$destinationPath.partial-$([Guid]::NewGuid().ToString('N'))"
    $passwordBytes = $null
    $key = $null
    $aes = $null
    $plain = $null
    $cipher = $null
    $tag = $null
    $nonce = $null
    $aad = $null
    $input = $null
    $output = $null
    $header = $null

    try {
        $passwordBytes = [byte[]](Get-SmartPrintBackupPasswordBytes -Passphrase $Passphrase)
        $input = [IO.File]::OpenRead($sourcePath)
        $header = [byte[]](Read-SmartPrintExactly -Stream $input -Count 40)
        if (-not [SmartPrintBackupCryptoV1]::FixedTimeEquals(
            [byte[]]$header[0..7], $script:SmartPrintBackupMagic)) {
            throw "The backup file format is unsupported."
        }

        $headerStream = [IO.MemoryStream]::new($header, $false)
        try {
            $magic = [byte[]](Read-SmartPrintExactly -Stream $headerStream -Count 8)
            $iterations = Read-SmartPrintUInt32BigEndian -Stream $headerStream
            $chunkSize = Read-SmartPrintUInt32BigEndian -Stream $headerStream
            $salt = [byte[]](Read-SmartPrintExactly -Stream $headerStream -Count 16)
            $noncePrefix = [byte[]](Read-SmartPrintExactly -Stream $headerStream -Count 8)
        }
        finally { $headerStream.Dispose() }

        if ($iterations -lt 200000 -or $iterations -gt 1000000 -or
            $chunkSize -lt 65536 -or $chunkSize -gt 8MB) {
            throw "The encrypted backup uses unsupported or unsafe cryptographic parameters."
        }

        $key = [SmartPrintBackupCryptoV1]::DeriveKey($passwordBytes, $salt, [int]$iterations)
        $aes = [SmartPrintBackupAesGcm]::new($key)
        $output = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        [uint64]$totalBytes = 0
        [uint32]$chunkCount = 0

        while ($true) {
            $recordLength = Read-SmartPrintUInt32BigEndian -Stream $input
            if ($recordLength -eq [uint32]::MaxValue) {
                $footerBytes = Read-SmartPrintUInt64BigEndian -Stream $input
                $footerChunks = Read-SmartPrintUInt32BigEndian -Stream $input
                $tag = [byte[]](Read-SmartPrintExactly -Stream $input -Count $script:SmartPrintBackupTagSize)
                if ($footerBytes -ne $totalBytes -or $footerChunks -ne $chunkCount) {
                    throw "The encrypted backup footer does not match its contents."
                }

                $nonce = [SmartPrintBackupCryptoV1]::MakeNonce($noncePrefix, $chunkCount)
                $aad = [SmartPrintBackupCryptoV1]::FooterAad($header, $footerBytes, $footerChunks)
                $empty = [byte[]]::new(0)
                $aes.Decrypt($nonce, $empty, $tag, $empty, $aad)
                if ($input.Position -ne $input.Length) {
                    throw "The encrypted backup contains unexpected trailing data."
                }

                $output.Flush($true)
                $output.Dispose()
                $output = $null
                [IO.File]::Move($temporaryPath, $destinationPath)
                return [pscustomobject]@{
                    Path = $destinationPath
                    PlaintextBytes = $totalBytes
                    EncryptedBytes = $input.Length
                    ChunkCount = $chunkCount
                    Format = "SPBKUP01"
                    Authenticated = $true
                }
            }

            if ($recordLength -eq 0 -or $recordLength -gt $chunkSize -or $chunkCount -eq [uint32]::MaxValue) {
                throw "The encrypted backup contains an invalid chunk length or counter."
            }

            $cipher = [byte[]](Read-SmartPrintExactly -Stream $input -Count ([int]$recordLength))
            $tag = [byte[]](Read-SmartPrintExactly -Stream $input -Count $script:SmartPrintBackupTagSize)
            $plain = [byte[]]::new([int]$recordLength)
            $nonce = [SmartPrintBackupCryptoV1]::MakeNonce($noncePrefix, $chunkCount)
            $aad = [SmartPrintBackupCryptoV1]::ChunkAad($header, $chunkCount, $recordLength)
            $aes.Decrypt($nonce, $cipher, $tag, $plain, $aad)
            $output.Write($plain, 0, $plain.Length)
            $totalBytes = [checked]($totalBytes + [uint64]$recordLength)
            $chunkCount++

            [SmartPrintBackupCryptoV1]::Zero($cipher)
            [SmartPrintBackupCryptoV1]::Zero($tag)
            [SmartPrintBackupCryptoV1]::Zero($plain)
            [SmartPrintBackupCryptoV1]::Zero($nonce)
            [SmartPrintBackupCryptoV1]::Zero($aad)
            $cipher = $null; $tag = $null; $plain = $null; $nonce = $null; $aad = $null
        }
    }
    catch {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue }
        throw
    }
    finally {
        if ($null -ne $input) { $input.Dispose() }
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $aes) { $aes.Dispose() }
        foreach ($bytes in @($passwordBytes, $key, $plain, $cipher, $tag, $nonce, $aad, $header)) {
            [SmartPrintBackupCryptoV1]::Zero($bytes)
        }
    }
}

Export-ModuleMember -Function Protect-SmartPrintBackup, Unprotect-SmartPrintBackup, Test-SmartPrintBackupPassphrasesEqual
