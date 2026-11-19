# Installs the official Epson L360 x64 driver into an isolated, non-default test queue.
# The queue writes driver output to a runner-local file port; it is not a physical printer.
[CmdletBinding()]
param(
    [string]$QueueName = "SmartPrint CI Epson L360",
    [string]$PackageUrl = "https://download-center.epson.com/f/module/39ac0a8a-10f0-4760-8949-cd1dd0ad646c/L360_x64_260JAHomeExportAsiaML.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows -and $env:OS -ne "Windows_NT") {
    throw "The Epson L360 driver qualification script requires Windows."
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw "RUNNER_TEMP is required; this script is intended for an isolated GitHub Windows runner."
}
if (Get-Printer -Name $QueueName -ErrorAction SilentlyContinue) {
    throw "Refusing to overwrite existing printer queue '$QueueName'."
}

$root = Join-Path $env:RUNNER_TEMP "smartprint-epson-l360-driver-qa"
if (Test-Path -LiteralPath $root) {
    throw "Refusing to reuse pre-existing Epson QA directory '$root'."
}
$extractRoot = Join-Path $root "extracted"
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

$packagePath = Join-Path $root "L360_x64_260JAHomeExportAsiaML.exe"
Write-Host "[EPSON-L360] Downloading the official Epson L360 x64 2.60.00 driver package."
$downloadHeaders = @{
    "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36"
    "Referer" = "https://www.epson.co.in/Support/Printers/All-In-One/L-Series/Epson-L360/s/SPT_C11CE55501?review-filter=Windows+10+64-bit"
    "Accept" = "application/octet-stream, application/x-msdownload, */*"
}
try {
    Invoke-WebRequest -Uri $PackageUrl -OutFile $packagePath -Headers $downloadHeaders -MaximumRedirection 10 -UseBasicParsing
}
catch {
    throw "The official Epson L360 v2.60.00 download was blocked or unavailable. Source: $PackageUrl. $($_.Exception.Message)"
}
$package = Get-Item -LiteralPath $packagePath
if ($package.Length -lt 5MB) {
    throw "The Epson driver package download is unexpectedly small ($($package.Length) bytes)."
}
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$signature = Get-AuthenticodeSignature -FilePath $packagePath
if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "(?i)Epson") {
    throw "The downloaded L360 package did not have a valid Epson Authenticode signature. Status=$($signature.Status); signer=$($signature.SignerCertificate.Subject)"
}
Write-Host "[EPSON-L360] Package bytes=$($package.Length); SHA256=$hash; Authenticode=$($signature.Status); signer=$($signature.SignerCertificate.Subject)"

$sevenZipCommand = Get-Command 7z.exe -ErrorAction SilentlyContinue
$sevenZip = if ($sevenZipCommand) { $sevenZipCommand.Source } else { $null }
if (-not $sevenZip) {
    $knownPaths = @(
        (Join-Path $env:ProgramFiles "7-Zip\7z.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe")
    )
    $sevenZip = $knownPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $sevenZip) {
    if (-not (Get-Command choco.exe -ErrorAction SilentlyContinue)) {
        throw "7-Zip is unavailable and Chocolatey is not installed; cannot safely inspect the Epson driver package."
    }
    choco install 7zip -y --no-progress
    if ($LASTEXITCODE -ne 0) {
        throw "Chocolatey could not install 7-Zip for Epson driver package extraction."
    }
    $sevenZipCommand = Get-Command 7z.exe -ErrorAction SilentlyContinue
    $sevenZip = if ($sevenZipCommand) { $sevenZipCommand.Source } else { $null }
    if (-not $sevenZip) {
        $sevenZip = Join-Path $env:ProgramFiles "7-Zip\7z.exe"
    }
}
if (-not (Test-Path -LiteralPath $sevenZip)) {
    throw "Could not locate 7z.exe after package setup."
}

Write-Host "[EPSON-L360] Inspecting/extracting the Epson package without launching its interactive USB setup wizard."
& $sevenZip x $packagePath "-o$extractRoot" -y | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "7-Zip could not extract the official Epson driver package (exit $LASTEXITCODE)."
}

$infCandidates = Get-ChildItem -LiteralPath $extractRoot -Filter "*.inf" -File -Recurse |
    Where-Object {
        $text = Get-Content -LiteralPath $_.FullName -Raw
        $text -match "(?i)L360" -and $text -match "(?i)EPSON"
    } |
    Sort-Object @{ Expression = { if ($_.Name -match "(?i)e_wf1n0e\.inf") { 0 } else { 1 } } }, FullName

if (-not $infCandidates -or $infCandidates.Count -eq 0) {
    $files = Get-ChildItem -LiteralPath $extractRoot -File -Recurse |
        Select-Object -First 40 -ExpandProperty FullName
    throw "No Epson L360 x64 INF was found in the official driver package. Extracted files: $($files -join '; ')"
}
$infPath = $infCandidates[0].FullName
Write-Host "[EPSON-L360] Selected INF: $infPath"

$driverNameCandidates = @("EPSON L360 Series", "Epson L360 Series", "EPSON L360")
$installedDriver = Get-PrinterDriver -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "(?i)EPSON.*L360|L360.*EPSON" } |
    Select-Object -First 1

if (-not $installedDriver) {
    & pnputil.exe /add-driver $infPath /install | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "pnputil could not stage/install the Epson L360 INF (exit $LASTEXITCODE)."
    }

    foreach ($candidate in $driverNameCandidates) {
        try {
            Add-PrinterDriver -Name $candidate -InfPath $infPath -PrinterEnvironment "Windows x64" -ErrorAction Stop | Out-Null
            $installedDriver = Get-PrinterDriver -Name $candidate -ErrorAction Stop
            break
        }
        catch {
            Write-Host "[EPSON-L360] Driver name candidate '$candidate' was not accepted: $($_.Exception.Message)"
        }
    }
}

if (-not $installedDriver) {
    $available = Get-PrinterDriver -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "(?i)EPSON|L360" } |
        Select-Object -ExpandProperty Name
    throw "The L360 driver package was staged, but Windows did not register the L360 printer driver. Related drivers: $($available -join '; ')"
}
$driverName = $installedDriver.Name
if ($driverName -notmatch "(?i)EPSON" -or $driverName -notmatch "(?i)L360") {
    throw "Windows resolved an unexpected printer driver name '$driverName'."
}
Write-Host "[EPSON-L360] Registered Windows driver: $driverName"

# A path-backed local port captures the exact data emitted by the Epson print
# processor. It lets us exercise PrintTicket/DEVMODE validation and spooler
# submission without claiming a USB-connected device printed physical paper.
$spoolerOutputPath = Join-Path $root "epson-l360-spooler-output.prn"
if (Get-PrinterPort -Name $spoolerOutputPath -ErrorAction SilentlyContinue) {
    throw "Refusing to reuse an existing local printer port '$spoolerOutputPath'."
}
Add-PrinterPort -Name $spoolerOutputPath
Add-Printer -Name $QueueName -DriverName $driverName -PortName $spoolerOutputPath -PrintProcessor "WinPrint" -ErrorAction Stop

$printer = Get-Printer -Name $QueueName -ErrorAction Stop
if ($printer.DriverName -ne $driverName -or $printer.PortName -ne $spoolerOutputPath) {
    throw "The isolated queue does not use the selected Epson driver and output file port."
}
$metadataPath = Join-Path $root "epson-l360-driver-metadata.json"
[ordered]@{
    manufacturer = "Epson"
    model = "L360"
    packageVersion = "2.60.00"
    packageUrl = $PackageUrl
    packageSha256 = $hash
    packageAuthenticodeStatus = [string]$signature.Status
    infPath = $infPath
    driverName = $driverName
    queueName = $QueueName
    portName = $spoolerOutputPath
    physicalPrinterConnected = $false
    qualificationBoundary = "Genuine Epson driver PrintTicket/DEVMODE and Windows spooler file-port acceptance only; not physical USB output certification."
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $metadataPath -Encoding utf8

if ($env:GITHUB_OUTPUT) {
    "queue_name=$QueueName" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "driver_name=$driverName" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "spooler_output_path=$spoolerOutputPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "metadata_path=$metadataPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}

Write-Host "[EPSON-L360] Isolated queue ready: $QueueName"
Write-Host "[EPSON-L360] Driver: $driverName"
Write-Host "[EPSON-L360] Spooler output port: $spoolerOutputPath"
Write-Host "[EPSON-L360] No default printer, physical queue, USB device or Spooler service setting was changed."
