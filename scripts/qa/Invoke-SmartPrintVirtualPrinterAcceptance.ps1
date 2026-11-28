[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$QueueName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$AgentVersion,

    [ValidateRange(30, 900)]
    [int]$TimeoutSeconds = 180,

    [string]$EvidenceDirectory = ".\artifacts\virtual-printer-acceptance"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-QueueJobs {
    param([string]$Name)
    @(Get-PrintJob -PrinterName $Name -ErrorAction SilentlyContinue)
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

$baseline = Get-QueueJobs -Name $QueueName
$baselineIds = @{}
foreach ($job in $baseline) { $baselineIds[[string]$job.ID] = $true }

Write-Host ""
Write-Host "SmartPrint Android Virtual Printer acceptance" -ForegroundColor Cyan
Write-Host "Queue : $($printer.Name)"
Write-Host "Driver: $($printer.DriverName)"
Write-Host "Port  : $($printer.PortName)"
Write-Host "Status: $($printer.PrinterStatus)"
Write-Host "Access : Available by default unless a platform hold is active"
Write-Host ""
Write-Host "1. Keep this window open."
Write-Host "2. Confirm Platform Operations shows this queue as Available (not On hold)." 
Write-Host "3. In the shop portal, start exactly one virtual-printer diagnostic for this queue."
Write-Host "4. Do not submit a Windows test page or print from another application."
Write-Host ""

$null = Read-Host "Press Enter when you are ready to start the SmartPrint diagnostic"
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$observedJob = $null
$observedAt = $null

while ([DateTimeOffset]::UtcNow -lt $deadline -and $null -eq $observedJob) {
    $jobs = Get-QueueJobs -Name $QueueName
    foreach ($job in $jobs) {
        if (-not $baselineIds.ContainsKey([string]$job.ID)) {
            $observedJob = $job
            $observedAt = [DateTimeOffset]::UtcNow
            break
        }
    }
    if ($null -eq $observedJob) { Start-Sleep -Seconds 2 }
}

if ($null -eq $observedJob) {
    throw "No new spooler job appeared on '$QueueName' within $TimeoutSeconds seconds. This is a failed acceptance, not a receiver test pass."
}

Write-Host ""
Write-Host "New Windows spooler job observed:" -ForegroundColor Green
Write-Host "Job ID       : $($observedJob.ID)"
Write-Host "Document name: $($observedJob.DocumentName)"
Write-Host "Submitted    : $($observedJob.SubmittedTime)"
Write-Host "Status        : $($observedJob.JobStatus)"
Write-Host ""
Write-Host "Now verify the Android Virtual Printer receiver itself."
Write-Host "Check that the complete SmartPrint diagnostic arrived and can be opened/rendered."
$receiverConfirmed = (Read-Host "Did the Android receiver receive the complete diagnostic? Type YES to confirm").Trim()
if ($receiverConfirmed -cne "YES") {
    $receiverResult = "NOT_CONFIRMED"
} else {
    $receiverResult = "CONFIRMED_BY_OPERATOR"
}

$observations = @()
$observeUntil = [DateTimeOffset]::UtcNow.AddSeconds(30)
while ([DateTimeOffset]::UtcNow -lt $observeUntil) {
    $current = Get-QueueJobs -Name $QueueName
    $match = $current | Where-Object { [string]$_.ID -eq [string]$observedJob.ID } | Select-Object -First 1
    if ($null -eq $match) {
        $observations += [pscustomobject]@{
            observedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
            state = "NO_LONGER_LISTED"
        }
        break
    }
    $observations += [pscustomobject]@{
        observedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
        state = [string]$match.JobStatus
    }
    Start-Sleep -Seconds 3
}

$passed = ($receiverResult -eq "CONFIRMED_BY_OPERATOR")
$report = [ordered]@{
    schemaVersion = 1
    test = "SMARTPRINT_TO_ANDROID_VIRTUAL_PRINTER"
    result = $(if ($passed) { "PASS_OPERATOR_CONFIRMED" } else { "FAIL_RECEIVER_NOT_CONFIRMED" })
    startedAtUtc = $startedAt.ToString("o")
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    machine = $env:COMPUTERNAME
    windowsVersion = [Environment]::OSVersion.Version.ToString()
    agentVersion = $AgentVersion
    platformAccessPolicy = "DEFAULT_ALLOW_EXPLICIT_PLATFORM_HOLD"
    holdState = "MANUALLY_VERIFIED_AVAILABLE_BEFORE_TEST"
    queue = [ordered]@{
        name = $printer.Name
        driver = $printer.DriverName
        driverVersion = $driver.DriverVersion
        port = $printer.PortName
        portMonitor = $port.PortMonitor
        initialStatus = [string]$printer.PrinterStatus
    }
    spooler = [ordered]@{
        jobId = [int]$observedJob.ID
        documentName = [string]$observedJob.DocumentName
        submittedTime = [string]$observedJob.SubmittedTime
        firstObservedAtUtc = $observedAt.ToString("o")
        initialJobStatus = [string]$observedJob.JobStatus
        observations = @($observations)
    }
    androidReceiver = [ordered]@{
        confirmation = $receiverResult
        confirmationMethod = "Explicit shop-operator visual confirmation"
    }
    limitations = @(
        "A Windows spooler job proves queue submission/acceptance only, not Android receipt.",
        "Android receiver confirmation is operator-attested; hosted CI cannot observe the shop LAN receiver.",
        "This script does not submit a synthetic job. The diagnostic must be started in SmartPrint."
    )
}

New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$safeTimestamp = $startedAt.ToString("yyyyMMdd-HHmmss")
$reportPath = Join-Path $EvidenceDirectory "virtual-printer-$safeTimestamp.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Evidence: $reportPath" -ForegroundColor Cyan
if ($passed) {
    Write-Host "PASS: Windows queue submission was observed and Android receipt was operator-confirmed." -ForegroundColor Green
    exit 0
}

Write-Host "FAIL: Windows queue submission occurred, but Android receiver receipt was not confirmed." -ForegroundColor Red
exit 1
