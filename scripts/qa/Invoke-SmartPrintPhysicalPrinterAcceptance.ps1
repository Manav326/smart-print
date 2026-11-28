[CmdletBinding()]
param(
    [string]$QueueName,

    [string]$AgentVersion,

    [ValidateSet("AUTO", "USB", "LAN", "IPP", "SHARED", "WINDOWS_QUEUE")]
    [string]$ExpectedConnectionType = "AUTO",

    [ValidateRange(30, 900)]
    [int]$TimeoutSeconds = 180,

    [string]$EvidenceDirectory = ".\artifacts\physical-printer-acceptance",

    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Help) {
    @'
Invoke-SmartPrintPhysicalPrinterAcceptance.ps1

Observe one real SmartPrint owner-initiated diagnostic on an already installed
physical Windows queue and record explicit operator confirmation of paper output.

Required:
  -QueueName "Exact Windows printer queue name"
  -AgentVersion "Installed SmartPrint Agent version"

Optional:
  -ExpectedConnectionType AUTO|USB|LAN|IPP|SHARED|WINDOWS_QUEUE
  -TimeoutSeconds 180
  -EvidenceDirectory .\artifacts\physical-printer-acceptance

The harness does not install drivers, change printer ports, create Windows test
jobs, or interpret a disappearing spooler job as proof of printed paper.
'@
    exit 0
}

if ([string]::IsNullOrWhiteSpace($QueueName)) {
    throw "QueueName is required. Run this script with -Help for usage."
}
if ([string]::IsNullOrWhiteSpace($AgentVersion)) {
    throw "AgentVersion is required. Run this script with -Help for usage."
}

function Get-OptionalPropertyValue {
    param([object]$InputObject, [string]$PropertyName)
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$PropertyName]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-QueueJobs {
    param([string]$Name)
    @(Get-PrintJob -PrinterName $Name -ErrorAction SilentlyContinue)
}

function Resolve-ConnectionType {
    param([object]$Printer)
    $portName = [string](Get-OptionalPropertyValue $Printer "PortName")
    $driverName = [string](Get-OptionalPropertyValue $Printer "DriverName")
    $shared = Get-OptionalPropertyValue $Printer "Shared"
    $shareName = [string](Get-OptionalPropertyValue $Printer "ShareName")
    $queueType = [string](Get-OptionalPropertyValue $Printer "Type")

    $queueName = [string](Get-OptionalPropertyValue $Printer "Name")
    if ($shared -eq $true -or -not [string]::IsNullOrWhiteSpace($shareName) -or
        $queueType -match '(?i)Connection' -or $queueName.StartsWith("\\") -or
        $portName.StartsWith("\\")) { return "SHARED" }
    if ($portName -match '(?i)^(ipp://|ipps://)') { return "IPP" }
    if ($portName -match '(?i)^https?://' -and $driverName -match '(?i)IPP') { return "IPP" }
    if ($driverName -match '(?i)IPP Class Driver' -and $portName -match '(?i)^WSD-') { return "IPP" }
    if ($portName -match '(?i)^(USB|DOT4_)') { return "USB" }
    if ($portName -match '(?i)^(IP_|TCP_|LPR_|WSD-)' -or $portName -match '(?i)WSD|TCP/IP|LPR') { return "LAN" }
    return "WINDOWS_QUEUE"
}

$startedAt = [DateTimeOffset]::UtcNow
$printer = Get-Printer -Name $QueueName -ErrorAction Stop
$driver = Get-PrinterDriver -Name $printer.DriverName -ErrorAction Stop
$port = Get-PrinterPort -Name $printer.PortName -ErrorAction Stop
if ([string]::IsNullOrWhiteSpace($printer.Name) -or
    [string]::IsNullOrWhiteSpace($printer.DriverName) -or
    [string]::IsNullOrWhiteSpace($printer.PortName)) {
    throw "Queue identity is incomplete; refusing to run acceptance."
}

$combinedIdentity = $printer.Name + " " + $printer.DriverName
$virtualMarkers = @("Microsoft Print to PDF", "Microsoft XPS Document Writer", "Adobe PDF", "Foxit PDF", "OneNote", "Fax")
if ($combinedIdentity -match '(?i)virtual') {
    throw "This queue appears to be virtual. Use the separate virtual-printer acceptance path."
}
foreach ($marker in $virtualMarkers) {
    if ($combinedIdentity.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "This queue appears to be a virtual/export destination ($marker). Physical acceptance is refused."
    }
}

$connectionType = Resolve-ConnectionType -Printer $printer
if ($ExpectedConnectionType -ne "AUTO" -and $connectionType -ne $ExpectedConnectionType) {
    throw "Expected connection type $ExpectedConnectionType but the installed Windows queue identifies as $connectionType. Recheck the selected queue and port."
}

$baseline = Get-QueueJobs -Name $QueueName
$baselineIds = @{}
foreach ($job in $baseline) { $baselineIds[[string]$job.ID] = $true }

Write-Host ""
Write-Host "SmartPrint physical printer acceptance" -ForegroundColor Cyan
Write-Host "Queue : $($printer.Name)"
Write-Host "Type  : $connectionType"
Write-Host "Driver: $($printer.DriverName)"
Write-Host "Port  : $($printer.PortName)"
Write-Host "Status: $($printer.PrinterStatus)"
Write-Host ""
Write-Host "1. Keep this window open."
Write-Host "2. In SmartPrint, select this queue and start exactly one owner-authorised printer diagnostic."
Write-Host "3. Do not submit a Windows test page or print from another application."
Write-Host ""
$null = Read-Host "Press Enter when you are ready to start the SmartPrint diagnostic"

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$observedJob = $null
$observedAt = $null
$observations = @()
while ([DateTimeOffset]::UtcNow -lt $deadline -and $null -eq $observedJob) {
    $jobs = Get-QueueJobs -Name $QueueName
    $matchingJobs = @($jobs | Where-Object {
        -not $baselineIds.ContainsKey([string]$_.ID) -and
        ([string](Get-OptionalPropertyValue $_ "DocumentName")) -like "SmartPrint-D*"
    })
    if ($matchingJobs.Count -gt 1) {
        throw "More than one new SmartPrint diagnostic job appeared on '$QueueName'; refusing to attach ambiguous evidence."
    }
    if ($matchingJobs.Count -eq 1) {
        $observedJob = $matchingJobs[0]
        $observedAt = [DateTimeOffset]::UtcNow
    }
    if ($null -eq $observedJob) { Start-Sleep -Milliseconds 250 }
}
if ($null -eq $observedJob) {
    throw "No new spooler job appeared on '$QueueName' within $TimeoutSeconds seconds. No printer compatibility pass can be claimed."
}

Write-Host ""
Write-Host "New Windows spooler job observed:" -ForegroundColor Green
Write-Host "Job ID       : $($observedJob.ID)"
Write-Host "Document name: $($observedJob.DocumentName)"
Write-Host "Submitted    : $($observedJob.SubmittedTime)"
Write-Host "Status       : $($observedJob.JobStatus)"
Write-Host ""
Write-Host "Inspect the actual output from the printer."
Write-Host "Check alignment, legibility, missing content, colour/monochrome and selected paper/duplex behaviour."
$paperResult = (Read-Host "Did the expected complete diagnostic physically print correctly? Type YES to confirm").Trim()
$decision = if ($paperResult -ceq "YES") { "CONFIRMED_CORRECT" } else { "NOT_CONFIRMED" }

$observeUntil = [DateTimeOffset]::UtcNow.AddSeconds(30)
while ([DateTimeOffset]::UtcNow -lt $observeUntil) {
    $current = Get-QueueJobs -Name $QueueName
    $match = $current | Where-Object { [string]$_.ID -eq [string]$observedJob.ID } | Select-Object -First 1
    if ($null -eq $match) {
        $observations += [pscustomobject]@{ observedAtUtc = [DateTimeOffset]::UtcNow.ToString("o"); state = "NO_LONGER_LISTED" }
        break
    }
    $observations += [pscustomobject]@{ observedAtUtc = [DateTimeOffset]::UtcNow.ToString("o"); state = [string]$match.JobStatus }
    Start-Sleep -Seconds 3
}

$passed = $decision -eq "CONFIRMED_CORRECT"
$report = [ordered]@{
    schemaVersion = 1
    test = "SMARTPRINT_PHYSICAL_PRINTER_E2E"
    result = $(if ($passed) { "PASS_OPERATOR_CONFIRMED" } else { "FAIL_PHYSICAL_OUTPUT_NOT_CONFIRMED" })
    startedAtUtc = $startedAt.ToString("o")
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    machine = $env:COMPUTERNAME
    windowsVersion = [Environment]::OSVersion.Version.ToString()
    agentVersion = $AgentVersion
    connectionType = $connectionType
    queue = [ordered]@{
        name = $printer.Name
        driver = $printer.DriverName
        driverVersion = Get-OptionalPropertyValue $driver "DriverVersion"
        port = $printer.PortName
        portMonitor = Get-OptionalPropertyValue $port "PortMonitor"
        initialStatus = [string]$printer.PrinterStatus
        isShared = Get-OptionalPropertyValue $printer "Shared"
        shareName = Get-OptionalPropertyValue $printer "ShareName"
    }
    spooler = [ordered]@{
        expectedDocumentNamePrefix = "SmartPrint-D"
        jobId = [int]$observedJob.ID
        documentName = [string]$observedJob.DocumentName
        submittedTime = [string]$observedJob.SubmittedTime
        firstObservedAtUtc = $observedAt.ToString("o")
        initialJobStatus = [string]$observedJob.JobStatus
        observations = @($observations)
    }
    physicalOutput = [ordered]@{
        confirmation = $decision
        confirmationMethod = "Explicit shop-operator visual inspection"
    }
    limitations = @(
        "A Windows spooler state or disappearing queue job is not treated as proof of paper output.",
        "The test uses an existing Windows-installed queue; it does not install drivers or modify printer ports.",
        "Record a separate run for each printer model, driver version, connection type and media profile."
    )
}
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$safeTimestamp = $startedAt.ToString("yyyyMMdd-HHmmss")
$reportPath = Join-Path $EvidenceDirectory "physical-printer-$safeTimestamp.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $reportPath -Encoding UTF8
Write-Host ""
Write-Host "Evidence: $reportPath" -ForegroundColor Cyan
if ($passed) {
    Write-Host "PASS: Windows queue submission was observed and physical output was operator-confirmed." -ForegroundColor Green
    exit 0
}
Write-Host "FAIL: Windows queue submission occurred, but physical output was not confirmed correct." -ForegroundColor Red
exit 1
