<#
.SYNOPSIS
Exercise SmartPrint Windows installer install, upgrade, rollback and uninstall scenarios.

.DESCRIPTION
Runs a destructive lifecycle test against two versioned SmartPrint Windows
installers. It seeds synthetic machine identity, network preference and
encrypted local-queue fixtures; creates a secondary Microsoft Print to PDF
queue; then installs/reinstalls/upgrades/rolls back/uninstalls the Agent and
verifies the test-owned data is preserved or removed as expected.

SAFETY: this script installs and uninstalls the SmartPrint Agent, registers
Windows services/firewall rules, writes under ProgramData/Program Files, and
creates/removes a printer queue. Run it only on a disposable Windows VM or
isolated CI runner with Administrator rights. Do not run it on your everyday
shop computer or a machine with real print jobs in its queue.

.PARAMETER Help
Show this guide without modifying Windows services, ProgramData, printers or
firewall rules.

.PARAMETER InstallerV1
Path to the earlier-version Windows setup executable (upgrade source).

.PARAMETER InstallerV2
Path to the later-version Windows setup executable (upgrade target).

.EXAMPLE
.\scripts\qa\windows-installer-lifecycle.ps1 -Help
Show test scenarios and safety requirements without running installers.

.EXAMPLE
.\scripts\qa\windows-installer-lifecycle.ps1 -InstallerV1 'artifacts/SmartPrint-v1.exe' -InstallerV2 'artifacts/SmartPrint-v2.exe'
Run the complete lifecycle against two test setup packages on a disposable
elevated Windows VM.

.NOTES
The script owns and removes only the fixtures and resources it creates. It
does not intentionally change the pre-existing default printer. It must run
elevated because service, firewall and machine-wide printer operations are
required.
#>

[CmdletBinding()]
param(
    [switch]$Help,
    [string]$InstallerV1 = "",
    [string]$InstallerV2 = ""
)

if ($Help) {
    Get-Help -Name $PSCommandPath -Full | Out-String -Width 180 | Write-Host
    exit 0
}
foreach ($entry in @(@{Name="InstallerV1";Path=$InstallerV1}, @{Name="InstallerV2";Path=$InstallerV2})) {
    if ([string]::IsNullOrWhiteSpace($entry.Path)) {
        throw ("-" + $entry.Name + " is required. Run this script with -Help to see usage examples.")
    }
    if (-not (Test-Path -LiteralPath $entry.Path -PathType Leaf)) {
        throw ("-" + $entry.Name + " does not exist: " + $entry.Path)
    }
}

$ErrorActionPreference = "Stop"
$serviceName = "QRtoPrintSmartPrintAgent"
$workerServiceName = "QRtoPrintSmartPrintDocumentWorker"
$workerFirewallPrefix = "QRtoPrint Smart Print Document Worker outbound block"
$firewallPrefix = "QRtoPrint Smart Print Agent Local API"
$testPrinterName = "SmartPrint Lifecycle QA Queue"
$fixtureProject = Join-Path $env:RUNNER_TEMP "SmartPrintLifecycleFixture"
$brokenProject = Join-Path $env:RUNNER_TEMP "SmartPrintBrokenAgent"
$programDataRoot = Join-Path $env:ProgramData "QRtoPrint"
$identityPath = Join-Path $programDataRoot "agent.identity"
$queueManifest = Join-Path $programDataRoot "LocalQueue\11111111111141118111111111111111.json"
$networkPreferencePath = Join-Path $programDataRoot "local-network.preference"
$expectedIdentityId = "22222222-2222-4222-8222-222222222222"
$expectedNetworkId = "LIFECYCLE-QA-SHOP-NETWORK"
$results = [System.Collections.Generic.List[object]]::new()
$identityHash = $null
$originalDefaultPrinter = $null
$fixturePrinter = $null
$installRoot = Join-Path $env:ProgramFiles "SmartPrint"
$ownsInstall = $false
$ownsPrinter = $false
$ownsTask = $false

$script:caseNumber = 0
function Add-Result([string]$Name, [string]$Status, [string]$Detail) {
    $results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail })
    $stamp = Get-Date -Format "HH:mm:ss"
    if ($Status -eq "PASS") {
        Write-Host "[QA-RESULT] $stamp | PASS: $Name - $Detail" -ForegroundColor Green
    }
    elseif ($Status -eq "SKIP") {
        Write-Host "[QA-RESULT] $stamp | SKIP: $Name - $Detail" -ForegroundColor Yellow
    }
    else {
        Write-Host "[QA-RESULT] $stamp | FAIL: $Name - $Detail" -ForegroundColor Red
    }
}

function Invoke-Check([string]$Name, [scriptblock]$Action) {
    $script:caseNumber += 1
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host ("[QA-CASE-START] {0:00} | {1} | {2}" -f $script:caseNumber, (Get-Date -Format "HH:mm:ss"), $Name) -ForegroundColor Cyan
    try {
        & $Action
        $timer.Stop()
        Add-Result $Name "PASS" "All assertions passed."
        Write-Host ("[QA-CASE-END] {0:00} | PASS | elapsed={1:n1}s | {2}" -f $script:caseNumber, $timer.Elapsed.TotalSeconds, $Name) -ForegroundColor Green
        return $true
    }
    catch {
        $timer.Stop()
        Add-Result $Name "FAIL" $_.Exception.Message
        Write-Host ("[QA-CASE-END] {0:00} | FAIL | elapsed={1:n1}s | {2}" -f $script:caseNumber, $timer.Elapsed.TotalSeconds, $Name) -ForegroundColor Red
        return $false
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Wait-Service([ValidateSet("Running", "Stopped", "Absent")][string]$State, [int]$TimeoutSeconds = 45) {
    Write-Host "[QA-WAIT] Agent Windows service -> $State (timeout $TimeoutSeconds seconds)." -ForegroundColor DarkCyan
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $poll = 0
    do {
        $poll++
        if ($poll -eq 1 -or $poll % 10 -eq 0) {
            Write-Host "[QA-PROGRESS] Agent service state | target=$State | poll=$poll | elapsed=$($poll)s." -ForegroundColor DarkCyan
        }
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($State -eq "Absent" -and -not $service) { return }
        if ($State -eq "Stopped" -and $service -and $service.Status -eq "Stopped") { return }
        if ($State -eq "Running" -and $service -and $service.Status -eq "Running") { return }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    $last = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $lastState = if ($last) { $last.Status } else { "Absent" }
    throw "Agent service did not reach $State within $TimeoutSeconds seconds. Current state: $lastState."
}

function Invoke-Setup([string]$Path) {
    Write-Host "[QA-ACTION] Execute installer: $Path" -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Installer not found: $Path"
    }
    $process = Start-Process -FilePath $Path -ArgumentList @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"
    ) -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Installer exited with code $($process.ExitCode): $Path"
    }
    Wait-Service "Running"
}

function Invoke-Uninstall {
    $uninstaller = Join-Path $installRoot "unins000.exe"
    if (Test-Path -LiteralPath $uninstaller) {
        $process = Start-Process -FilePath $uninstaller -ArgumentList @(
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"
        ) -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "SmartPrint uninstaller exited with code $($process.ExitCode)."
        }
    }
    elseif (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        throw "The Agent service exists but its owned SmartPrint uninstaller is missing; refusing a partial cleanup."
    }
    Wait-Service "Absent"
}

function Get-ServiceBinaryPath {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (-not $service) { return $null }
    return $service.PathName.Trim().Trim('"')
}

function Wait-LocalApiReady([int]$TimeoutSeconds = 45) {
    Write-Host "[QA-WAIT] Agent local API and preserved device identity (timeout $TimeoutSeconds seconds)." -ForegroundColor DarkCyan
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $uri = "http://127.0.0.1:17842/api/v1/local/status"
    $poll = 0
    do {
        $poll++
        if ($poll -eq 1 -or $poll % 10 -eq 0) {
            Write-Host "[QA-PROGRESS] Agent local API and identity readiness | poll=$poll | elapsed=$($poll)s." -ForegroundColor DarkCyan
        }
        try {
            $status = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 3
            if ($status.status -eq "LOCAL_ENDPOINT_READY" -and
                $status.agentDeviceId -eq $expectedIdentityId) {
                return
            }
        }
        catch {
            # The Windows service can report Running before the LAN listener finishes starting.
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "The Agent service is running, but its local print API did not become ready with the preserved shop identity."
}

function Assert-ServiceVersion([string]$ExpectedVersion) {
    Wait-Service "Running"
    $path = Get-ServiceBinaryPath
    Assert-True (-not [string]::IsNullOrWhiteSpace($path)) "Windows service registration is missing."
    Assert-True ($path -match [regex]::Escape("\agent\$ExpectedVersion-")) "Service points to the wrong Agent payload: $path"
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "The Agent executable configured in the Windows service does not exist: $path"
    Wait-LocalApiReady
}

function Assert-OptionalParserService {
    $worker = Get-Service -Name $workerServiceName -ErrorAction SilentlyContinue
    Assert-True ($null -ne $worker) "The optional isolated document parser service was not installed."
    Assert-True ($worker.Status -eq "Stopped") "The optional parser must remain stopped until an upload needs analysis; current state: $($worker.Status)."

    $registration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$workerServiceName'" -ErrorAction SilentlyContinue
    Assert-True ($null -ne $registration) "Windows Service Control Manager has no isolated parser registration."
    Assert-True ($registration.StartMode -eq "Manual") "The parser service must be demand-started, not started automatically with the print Agent."
    Assert-True ($registration.StartName -eq "NT AUTHORITY\LocalService") "The parser must run as the restricted LocalService identity, not LocalSystem."
    $workerPath = ([string]$registration.PathName).Trim().Trim('"')
    Assert-True (Test-Path -LiteralPath $workerPath -PathType Leaf) "The parser service executable is missing: $workerPath"

    $agent = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    Assert-True ($agent -and $agent.Status -eq "Running") "The primary printing Agent must remain Running while the optional parser is stopped."
    Wait-LocalApiReady
}

function Assert-DocumentWorkerRuntimeIsolation {
    Write-Host "[QA-ACTION] Exercise the on-demand LocalService parser over its restricted named pipe." -ForegroundColor Cyan
    $taskName = "SmartPrintLifecycleParserAcceptance"
    $probeTaskName = "SmartPrintLifecyclePipeAclProbe"
    $probeRoot = Join-Path $env:RUNNER_TEMP "SmartPrintParserAcceptance"
    $systemResultPath = Join-Path $probeRoot "system-analysis.json"
    $networkProbeDirectory = Join-Path $probeRoot "network-service"
    $networkResultPath = Join-Path $networkProbeDirectory "pipe-probe.txt"
    $assemblyPath = Join-Path $fixtureProject "bin\Release\net10.0-windows\SmartPrintLifecycleFixture.dll"
    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    Assert-True (Test-Path -LiteralPath $assemblyPath) "The parser acceptance fixture assembly was not built."

    New-Item -ItemType Directory -Force -Path $probeRoot, $networkProbeDirectory | Out-Null
    $script:ownsParserAcceptanceDirectory = $true

    try {
        Remove-Item -LiteralPath $systemResultPath -Force -ErrorAction SilentlyContinue
        $systemArguments = '"' + $assemblyPath + '" analyze-worker "' + $systemResultPath + '"'
        $systemAction = New-ScheduledTaskAction -Execute $dotnetPath -Argument $systemArguments
        $systemPrincipal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
        Register-ScheduledTask -TaskName $taskName -Action $systemAction -Principal $systemPrincipal | Out-Null
        Start-ScheduledTask -TaskName $taskName

        $deadline = (Get-Date).AddSeconds(35)
        while (-not (Test-Path -LiteralPath $systemResultPath) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        Assert-True (Test-Path -LiteralPath $systemResultPath) "The LocalSystem Agent could not complete a real LocalService document-worker analysis."
        $analysis = Get-Content -LiteralPath $systemResultPath -Raw | ConvertFrom-Json
        Assert-True ($analysis.pageCount -eq 1 -and $analysis.mediaType -eq "image/png") "The restricted parser returned unexpected analysis metadata."

        $worker = Get-Service -Name $workerServiceName -ErrorAction Stop
        Assert-True ($worker.Status -eq "Running") "The document worker did not start on demand."
        $registration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$workerServiceName'"
        Assert-True ($registration.StartName -eq "NT AUTHORITY\LocalService") "The parser service is not running as LocalService."

        # Network Service is a distinct unprivileged local identity. It must not
        # be able to connect to a pipe whose DACL grants only SYSTEM and LocalService.
        & icacls.exe (Split-Path -Parent $assemblyPath) /grant "NT AUTHORITY\NETWORK SERVICE:(OI)(CI)(RX)" /T | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not grant Network Service read/execute access to the isolated fixture assembly." }
        & icacls.exe $networkProbeDirectory /grant "NT AUTHORITY\NETWORK SERVICE:(OI)(CI)(M)" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not grant the isolated Network Service probe its result-file access." }
        $probeArguments = '"' + $assemblyPath + '" probe-worker-pipe "' + $networkResultPath + '"'
        $probeAction = New-ScheduledTaskAction -Execute $dotnetPath -Argument $probeArguments
        $probePrincipal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\NETWORK SERVICE" -LogonType ServiceAccount -RunLevel Limited
        Register-ScheduledTask -TaskName $probeTaskName -Action $probeAction -Principal $probePrincipal | Out-Null
        Start-ScheduledTask -TaskName $probeTaskName

        $probeDeadline = (Get-Date).AddSeconds(20)
        while (-not (Test-Path -LiteralPath $networkResultPath) -and (Get-Date) -lt $probeDeadline) {
            Start-Sleep -Milliseconds 250
        }
        Assert-True (Test-Path -LiteralPath $networkResultPath) "The unprivileged Network Service pipe probe did not produce a result."
        $probeResult = (Get-Content -LiteralPath $networkResultPath -Raw).Trim()
        Assert-True ($probeResult -eq "REJECTED") "The isolated worker pipe did not reject Network Service: $probeResult"

        Write-Host "[QA-RESULT] PASS: LocalSystem can use the LocalService parser; Network Service is denied by the worker pipe ACL." -ForegroundColor Green
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $probeTaskName -Confirm:$false -ErrorAction SilentlyContinue
        $worker = Get-Service -Name $workerServiceName -ErrorAction SilentlyContinue
        if ($worker -and $worker.Status -ne "Stopped") {
            Stop-Service -Name $workerServiceName -Force
            (Get-Service -Name $workerServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        }
        if ($script:ownsParserAcceptanceDirectory) {
            Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
            $script:ownsParserAcceptanceDirectory = $false
        }
        $agent = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        Assert-True ($agent -and $agent.Status -eq "Running") "The primary print Agent stopped during isolated parser acceptance."
        Wait-LocalApiReady
    }
}

function Invoke-Fixture([string]$Mode) {
    Write-Host "[QA-ACTION] Run isolated Agent data-preservation fixture: mode=$Mode" -ForegroundColor Cyan
    & dotnet run --project $fixtureProject --configuration Release -- $Mode | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Lifecycle fixture '$Mode' failed with exit code $LASTEXITCODE." }
}

function Assert-PreservedFixtures {
    Assert-True (Test-Path -LiteralPath $identityPath -PathType Leaf) "The machine Agent identity was removed."
    $currentHash = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash
    Assert-True ($currentHash -eq $identityHash) "The machine Agent identity changed during install/uninstall."
    Assert-True (Test-Path -LiteralPath $networkPreferencePath -PathType Leaf) "The shop's saved local-network selection was removed."
    Invoke-Fixture "verify"
    Assert-True (Test-Path -LiteralPath $queueManifest -PathType Leaf) "The protected local print queue manifest was removed."
}

function Add-QueueFixture {
    $default = Get-CimInstance Win32_Printer | Where-Object { $_.Default } | Select-Object -First 1
    $script:originalDefaultPrinter = if ($default) { $default.Name } else { $null }

    $source = Get-Printer -Name "Microsoft Print to PDF" -ErrorAction Stop
    Add-Printer -Name $testPrinterName -DriverName $source.DriverName -PortName $source.PortName -ErrorAction Stop
    $script:ownsPrinter = $true
    $script:fixturePrinter = Get-Printer -Name $testPrinterName -ErrorAction Stop
}

function Assert-SystemCanDiscoverFixturePrinter {
    Write-Host "[QA-ACTION] Verify Agent printer discovery as LocalSystem using the temporary lifecycle queue." -ForegroundColor Cyan
    $taskName = "SmartPrintLifecycleSystemPrinterProbe"
    $resultPath = Join-Path $env:RUNNER_TEMP "smartprint-system-printer-probe.txt"
    $assemblyPath = Join-Path $fixtureProject "bin\Release\net10.0-windows\SmartPrintLifecycleFixture.dll"
    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    Assert-True (Test-Path -LiteralPath $assemblyPath) "The Agent discovery fixture assembly was not built."

    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    $arguments = '"' + $assemblyPath + '" discover-printer "' + $resultPath + '" "' + $testPrinterName + '"'
    $action = New-ScheduledTaskAction -Execute $dotnetPath -Argument $arguments
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    try {
        Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
        $script:ownsTask = $true
        Start-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddSeconds(40)
        $poll = 0
        while (-not (Test-Path -LiteralPath $resultPath) -and (Get-Date) -lt $deadline) {
            $poll++
            if ($poll -eq 1 -or $poll % 10 -eq 0) {
                Write-Host "[QA-PROGRESS] LocalSystem printer discovery task | poll=$poll | elapsed=$([Math]::Round($poll / 2, 1))s." -ForegroundColor DarkCyan
            }
            Start-Sleep -Milliseconds 500
        }
        Assert-True (Test-Path -LiteralPath $resultPath) "The LocalSystem printer-discovery task did not produce a result."
        $result = Get-Content -LiteralPath $resultPath -Raw
        Assert-True ($result.Trim() -eq "FOUND") "The Agent's LocalSystem printer-discovery result was unexpected: $result"
    }
    finally {
        if ($script:ownsTask) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            $script:ownsTask = $false
        }
        Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-QueueUntouched {
    $printer = Get-Printer -Name $testPrinterName -ErrorAction SilentlyContinue
    Assert-True ($null -ne $printer) "SmartPrint installation/uninstallation removed an existing Windows printer queue."
    Assert-True ($printer.DriverName -eq $fixturePrinter.DriverName) "The existing printer driver changed."
    Assert-True ($printer.PortName -eq $fixturePrinter.PortName) "The existing printer port changed."
    $default = Get-CimInstance Win32_Printer | Where-Object { $_.Default } | Select-Object -First 1
    $currentDefault = if ($default) { $default.Name } else { $null }
    Assert-True ($currentDefault -eq $originalDefaultPrinter) "SmartPrint changed the Windows default printer."
}

function Write-StepSummary {
    if (-not $env:GITHUB_STEP_SUMMARY) { return }
    $lines = @(
        "## Windows installer lifecycle QA",
        "",
        "| Scenario | Result | Details |",
        "|---|---|---|"
    )
    foreach ($item in $results) {
        $detail = ($item.Detail -replace '\|', '\|' -replace "[\r\n]+", " ")
        $lines += "| $($item.Name) | **$($item.Status)** | $detail |"
    }
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($lines -join [Environment]::NewLine)
}

$firstInstallOk = $false
$servicePathAfterFirstInstall = $null
$v2Installed = $false

try {
    Write-Host "[QA-PREFLIGHT] Confirming this runner has no existing SmartPrint install, Agent service, fixture printer, task, firewall rule, or machine data to overwrite." -ForegroundColor Cyan
    Assert-True (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "Refusing to test on a runner that already has the SmartPrint Agent service."
    Assert-True (-not (Get-Service -Name $workerServiceName -ErrorAction SilentlyContinue)) "Refusing to test on a runner that already has the optional document worker service."
    Assert-True (-not (Test-Path -LiteralPath $installRoot)) "Refusing to test on a runner that already has a SmartPrint installation directory."
    Assert-True (-not (Get-Printer -Name $testPrinterName -ErrorAction SilentlyContinue)) "Refusing to overwrite an existing printer queue named '$testPrinterName'."
    Assert-True (-not (Get-ScheduledTask -TaskName "SmartPrintLifecycleSystemPrinterProbe" -ErrorAction SilentlyContinue)) "Refusing to replace an existing lifecycle printer-probe task."
    $existingRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like "$firewallPrefix*" })
    Assert-True ($existingRules.Count -eq 0) "Refusing to alter existing SmartPrint firewall rules."
    Assert-True (-not (Test-Path -LiteralPath $identityPath)) "Refusing to overwrite an existing machine Agent identity fixture."
    Assert-True (-not (Test-Path -LiteralPath $networkPreferencePath)) "Refusing to overwrite an existing shop network preference."
    Assert-True (-not (Test-Path -LiteralPath $queueManifest)) "Refusing to overwrite an existing local print queue fixture."

    New-Item -ItemType Directory -Force -Path $fixtureProject | Out-Null
    $fixtureProjectXml = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="__AGENT_PROJECT__" />
  </ItemGroup>
</Project>
'@
    $fixtureProjectXml = $fixtureProjectXml.Replace("__AGENT_PROJECT__", (Join-Path (Get-Location) "src\SmartPrint.Agent\SmartPrint.Agent.csproj"))
    $fixtureProjectXml | Set-Content -LiteralPath (Join-Path $fixtureProject "SmartPrintLifecycleFixture.csproj") -Encoding utf8

    @'
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;

const string expectedIdentityId = "22222222-2222-4222-8222-222222222222";
const string expectedNetworkId = "LIFECYCLE-QA-SHOP-NETWORK";
const string expectedJobId = "11111111-1111-4111-8111-111111111111";
const string expectedDocument = "SmartPrint lifecycle queue fixture - preserve me.";
var mode = args.FirstOrDefault() ?? "verify";
var credentialStore = new AgentCredentialStore();
var queueStore = new AgentLocalQueueStore();
var networkPreferenceStore = new LocalNetworkPreferenceStore();

if (mode == "seed")
{
    credentialStore.Save(new AgentCredentials(
        "https://127.0.0.1:1",
        Guid.Parse(expectedIdentityId),
        "lifecycle-only-invalid-secret",
        DateTimeOffset.UtcNow));
    networkPreferenceStore.SetPreferredNetworkId(expectedNetworkId);
    await queueStore.InitializeAsync(CancellationToken.None);
    var bytes = Encoding.UTF8.GetBytes(expectedDocument);
    var job = new AgentClaimedJob(
        Guid.Parse(expectedJobId),
        "LIFECYCLE-QA-0001",
        "PAID",
        1,
        1,
        "Color",
        "A4",
        false,
        "INR",
        100,
        null,
        "Lifecycle fixture printer",
        "Microsoft Print to PDF",
        "lifecycle-fixture.pdf",
        "application/pdf",
        bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        DateTimeOffset.UtcNow.AddHours(2));
    await queueStore.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);
    Console.WriteLine("Seeded a synthetic DPAPI identity and encrypted staged queue item.");
}
else if (mode == "verify")
{
    var credentials = credentialStore.Load()
        ?? throw new InvalidOperationException("The synthetic machine identity is missing.");
    if (credentials.AgentDeviceId != Guid.Parse(expectedIdentityId))
        throw new InvalidOperationException("The synthetic machine identity was replaced.");
    if (credentials.ApiBaseUrl != "https://127.0.0.1:1")
        throw new InvalidOperationException("The synthetic Agent API address changed.");
    if (networkPreferenceStore.GetPreferredNetworkId() != expectedNetworkId)
        throw new InvalidOperationException("The shop's saved local-network selection changed or was removed.");

    var entry = queueStore.Find(Guid.Parse(expectedJobId))
        ?? throw new InvalidOperationException("The staged local queue manifest is missing.");
    if (!File.Exists(entry.DocumentPath))
        throw new InvalidOperationException("The protected local queue document is missing.");
    var protectedBytes = await File.ReadAllBytesAsync(entry.DocumentPath);
    var clearBytes = ProtectedData.Unprotect(
        protectedBytes,
        Encoding.UTF8.GetBytes("QRtoPrint.Agent.LocalQueue.Document.v1"),
        DataProtectionScope.LocalMachine);
    try
    {
        if (Encoding.UTF8.GetString(clearBytes) != expectedDocument)
            throw new InvalidOperationException("The encrypted local queue document did not survive intact.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(clearBytes);
        CryptographicOperations.ZeroMemory(protectedBytes);
    }
    Console.WriteLine("Verified the same DPAPI identity and decrypted local queue document.");
}
else if (mode == "discover-printer")
{
    if (args.Length < 3)
        throw new ArgumentException("discover-printer requires an output path and expected queue name.");
    var discovery = new WindowsPrinterDiscovery(NullLogger<WindowsPrinterDiscovery>.Instance);
    if (!discovery.TryDiscover(out var printers))
        throw new InvalidOperationException("The Agent's Windows WMI printer discovery failed under LocalSystem.");
    var expectedQueue = args[2];
    if (!printers.Any(printer => string.Equals(printer.QueueName, expectedQueue, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException($"The Agent's LocalSystem discovery did not find machine-wide printer queue '{expectedQueue}'.");
    await File.WriteAllTextAsync(args[1], "FOUND");
    Console.WriteLine($"LocalSystem Agent discovery found '{expectedQueue}'.");
}
else if (mode == "analyze-worker")
{
    if (args.Length < 2)
        throw new ArgumentException("analyze-worker requires a result path.");

    // DocumentAnalyzer validates these fixed PNG header fields; the isolated
    // worker still receives the bytes through its real service and child-process path.
    byte[] png =
    [
        137, 80, 78, 71, 13, 10, 26, 10,
        0, 0, 0, 13, 73, 72, 68, 82,
        0, 0, 0, 1, 0, 0, 0, 1
    ];
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:DocumentWorkerPipeName"] = WindowsLocalDocumentAnalyzer.DefaultPipeName,
            ["Agent:DocumentWorkerTimeoutSeconds"] = "20"
        })
        .Build();
    var analyzer = new WindowsLocalDocumentAnalyzer(
        configuration, new WindowsLocalDocumentWorkerServiceStarter());
    var analysis = await analyzer.AnalyzeAsync(png, ".png", CancellationToken.None);
    if (analysis.PageCount != 1 ||
        analysis.MediaType != "image/png" ||
        !analysis.PageSizesJson.Contains("\"pixelWidth\":1", StringComparison.Ordinal))
        throw new InvalidOperationException("The LocalService worker returned unexpected PNG analysis.");
    await File.WriteAllTextAsync(args[1], System.Text.Json.JsonSerializer.Serialize(analysis));
    Console.WriteLine("The LocalSystem Agent client successfully used the LocalService worker and restricted parser child.");
}
else if (mode == "probe-worker-pipe")
{
    if (args.Length < 2)
        throw new ArgumentException("probe-worker-pipe requires a result path.");

    using var pipe = new System.IO.Pipes.NamedPipeClientStream(
        ".", WindowsLocalDocumentAnalyzer.DefaultPipeName,
        System.IO.Pipes.PipeDirection.InOut,
        System.IO.Pipes.PipeOptions.Asynchronous);
    try
    {
        await pipe.ConnectAsync(2500);
        throw new InvalidOperationException("Network Service unexpectedly connected to the restricted document-worker pipe.");
    }
    catch (UnauthorizedAccessException)
    {
        await File.WriteAllTextAsync(args[1], "REJECTED");
    }
    catch (IOException exception) when ((exception.HResult & 0xFFFF) == 5)
    {
        await File.WriteAllTextAsync(args[1], "REJECTED");
    }
}
else
{
    throw new ArgumentException($"Unknown fixture mode: {mode}");
}
'@ | Set-Content -LiteralPath (Join-Path $fixtureProject "Program.cs") -Encoding utf8

    $seedOk = Invoke-Check "Seed isolated machine identity and pending queue" {
        Invoke-Fixture "seed"
        Assert-True (Test-Path -LiteralPath $identityPath) "The synthetic Agent identity was not created."
        $script:identityHash = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash
        Assert-True (Test-Path -LiteralPath $queueManifest) "The staged queue manifest was not created."
        Add-QueueFixture
        Assert-SystemCanDiscoverFixturePrinter
    }

    if ($seedOk) {
        $firstInstallOk = Invoke-Check "Fresh Windows installation (N)" {
            $script:ownsInstall = $true
            Invoke-Setup $InstallerV1
            Assert-ServiceVersion "0.1.10"
            Assert-OptionalParserService
            Assert-DocumentWorkerRuntimeIsolation
            Assert-OptionalParserService
            Assert-PreservedFixtures
            Assert-QueueUntouched
            $script:servicePathAfterFirstInstall = Get-ServiceBinaryPath
        }
    }

    if ($firstInstallOk) {
        Invoke-Check "Reinstall the exact same Windows installer while Agent service is running" {
            Invoke-Setup $InstallerV1
            Assert-ServiceVersion "0.1.10"
            Assert-OptionalParserService
            Assert-PreservedFixtures
            Assert-QueueUntouched
        } | Out-Null
    }

    $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $serviceHealthy = $currentService -and $currentService.Status -eq "Running"
    if ($firstInstallOk -and $serviceHealthy) {
        $upgradeOk = Invoke-Check "Upgrade Windows installation N to N+1" {
            Invoke-Setup $InstallerV2
            Assert-ServiceVersion "0.1.11"
            Assert-OptionalParserService
            Assert-PreservedFixtures
            Assert-QueueUntouched
            $script:v2Installed = $true
            $oldPayloads = @(Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles "SmartPrint\agent") -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like "0.1.10-*" })
            Assert-True ($oldPayloads.Count -eq 0) "The old Agent payload was not cleaned up after a successful upgrade."
        }
        if (-not $upgradeOk) { $script:v2Installed = $false }
    }
    else {
        Add-Result "Upgrade Windows installation N to N+1" "SKIP" "The previous installer stage did not leave a healthy Agent service."
    }

    $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $serviceHealthy = $currentService -and $currentService.Status -eq "Running"
    if ($serviceHealthy -and $v2Installed) {
        Invoke-Check "Failed Agent upgrade restores the previous running service" {
            $previousPath = Get-ServiceBinaryPath
            $previousHash = (Get-FileHash -LiteralPath $previousPath -Algorithm SHA256).Hash

            New-Item -ItemType Directory -Force -Path $brokenProject | Out-Null
            @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <AssemblyName>SmartPrint.Agent</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $brokenProject "SmartPrint.Agent.csproj") -Encoding utf8
            "Environment.Exit(17);" | Set-Content -LiteralPath (Join-Path $brokenProject "Program.cs") -Encoding utf8
            $brokenOutput = Join-Path $brokenProject "publish"
            & dotnet publish (Join-Path $brokenProject "SmartPrint.Agent.csproj") --configuration Release --output $brokenOutput
            if ($LASTEXITCODE -ne 0) { throw "Could not build the intentionally invalid service fixture." }
            $badExe = Join-Path $brokenOutput "SmartPrint.Agent.exe"
            Assert-True (Test-Path -LiteralPath $badExe) "The intentionally invalid service fixture executable is missing."

            $installScript = Join-Path (Split-Path -Parent $previousPath) "install-agent-service.ps1"
            $process = Start-Process -FilePath "powershell.exe" -ArgumentList @(
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-File", ('"' + $installScript + '"'),
                "-AgentExePath", ('"' + $badExe + '"'),
                "-AgentVersion", "rollback-fixture",
                "-CustomerPortalOrigin", "https://127.0.0.1",
                "-ApiBaseUrl", "https://127.0.0.1"
            ) -Wait -PassThru
            Assert-True ($process.ExitCode -ne 0) "The deliberately broken Agent unexpectedly installed successfully."
            Wait-Service "Running"
            $restoredPath = Get-ServiceBinaryPath
            Assert-True ($restoredPath -eq $previousPath) "Rollback did not restore the previous Agent executable path."
            $restoredHash = (Get-FileHash -LiteralPath $restoredPath -Algorithm SHA256).Hash
            Assert-True ($restoredHash -eq $previousHash) "Rollback changed the previous Agent executable."
            Assert-PreservedFixtures
            Assert-QueueUntouched
        } | Out-Null
    }
    else {
        Add-Result "Failed Agent upgrade rollback" "SKIP" "A successful N+1 upgrade was not available."
    }

    if ($firstInstallOk) {
        $uninstallOk = Invoke-Check "Normal uninstall removes service but preserves printers and recovery data" {
            Invoke-Uninstall
            Assert-True (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) "The Agent service still exists after uninstall."
            Assert-True (-not (Get-Service -Name $workerServiceName -ErrorAction SilentlyContinue)) "The optional document worker service still exists after uninstall."
            $workerRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like "$workerFirewallPrefix*" })
            Assert-True ($workerRules.Count -eq 0) "The isolated document worker outbound firewall rule remains after uninstall."
            $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like "$firewallPrefix*" })
            Assert-True ($rules.Count -eq 0) "SmartPrint firewall rules remain after uninstall."
            Assert-PreservedFixtures
            Assert-QueueUntouched
        }

        if ($uninstallOk) {
            Invoke-Check "Reinstall after uninstall recovers the same machine identity and queue" {
                Invoke-Setup $InstallerV2
                Assert-ServiceVersion "0.1.11"
                Assert-OptionalParserService
                Assert-PreservedFixtures
                Assert-QueueUntouched
            } | Out-Null
        }
    }
}
catch {
    Add-Result "Lifecycle harness" "FAIL" $_.Exception.Message
}
finally {
    Write-Host "[QA-CLEANUP] Cleaning only fixtures and installation resources created by this lifecycle run." -ForegroundColor DarkCyan
    if ($ownsInstall) {
        try { Invoke-Uninstall } catch { Write-Warning "Final uninstall cleanup: $($_.Exception.Message)" }
    }
    if ($ownsTask) {
        Unregister-ScheduledTask -TaskName "SmartPrintLifecycleSystemPrinterProbe" -Confirm:$false -ErrorAction SilentlyContinue
        $script:ownsTask = $false
    }
    if ($ownsPrinter) {
        Remove-Printer -Name $testPrinterName -ErrorAction SilentlyContinue
        $script:ownsPrinter = $false
    }
    if ($ownsInstall) {
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $installRoot)) {
            Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        Get-NetFirewallRule -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "$firewallPrefix*" } |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
    }
    Write-StepSummary
}

$failures = @($results | Where-Object { $_.Status -eq "FAIL" })
if ($failures.Count -gt 0) {
    throw "Windows installer lifecycle QA failed in $($failures.Count) scenario(s). See the scenario details above and the GitHub Actions step summary."
}
Write-Host "All Windows installer lifecycle scenarios passed." -ForegroundColor Green
