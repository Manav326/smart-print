[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "PRINT PRIORITY CONTRACT FAILED: $Message" }
}

$workerPath = Join-Path $repoRoot "src/SmartPrint.DocumentWorker.Windows/Program.cs"
$defenderPath = Join-Path $repoRoot "src/SmartPrint.Agent/WindowsDefenderDocumentThreatScanner.cs"
$agentWorkerPath = Join-Path $repoRoot "src/SmartPrint.Agent/Worker.cs"
$printExecutorPath = Join-Path $repoRoot "src/SmartPrint.Agent/WindowsPrintExecutor.cs"
$serviceStarterPath = Join-Path $repoRoot "src/SmartPrint.Agent/WindowsLocalDocumentWorkerServiceStarter.cs"
$documentAnalyzerPath = Join-Path $repoRoot "src/SmartPrint.Agent/WindowsLocalDocumentAnalyzer.cs"
$serviceSetupPath = Join-Path $repoRoot "scripts/install-document-worker-service.ps1"
$agentInstallPath = Join-Path $repoRoot "scripts/install-agent-service.ps1"
$lifecyclePath = Join-Path $repoRoot "scripts/qa/windows-installer-lifecycle.ps1"

foreach ($path in @(
    $workerPath, $defenderPath, $agentWorkerPath, $printExecutorPath,
    $serviceStarterPath, $documentAnalyzerPath, $serviceSetupPath,
    $agentInstallPath, $lifecyclePath
)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required printing/security source is missing: $path"
}

$worker = Get-Content -LiteralPath $workerPath -Raw
$defender = Get-Content -LiteralPath $defenderPath -Raw
$agent = Get-Content -LiteralPath $agentWorkerPath -Raw
$printExecutor = Get-Content -LiteralPath $printExecutorPath -Raw
$starter = Get-Content -LiteralPath $serviceStarterPath -Raw
$setup = Get-Content -LiteralPath $serviceSetupPath -Raw
$install = Get-Content -LiteralPath $agentInstallPath -Raw
$lifecycle = Get-Content -LiteralPath $lifecyclePath -Raw

# No fixed CPU quota or percentage throttle. BelowNormal is a scheduler priority,
# not a CPU ceiling: it yields to normal-priority spooler/print work and can use
# otherwise idle CPU when the printer is not busy.
Assert-True ($worker.Contains("ProcessPriorityClass.BelowNormal")) "Isolated parser service must yield to normal-priority printing."
Assert-True ($defender.Contains("ProcessPriorityClass.BelowNormal")) "Defender custom-file scans must yield to normal-priority printing."
foreach ($forbidden in @("CpuRateHundredthsPercent", "CpuRateControlHardCap", "JobObjectCpuRateControlInformation", "-CpuThrottling")) {
    Assert-True (-not $worker.Contains($forbidden) -and -not $defender.Contains($forbidden)) "A fixed low CPU throttle was reintroduced: $forbidden"
}

# The parser is demand-started and isolated; it is not part of the Agent's
# heartbeat, printer-discovery, queue dispatch or GDI execution dependency graph.
Assert-True ($setup.Contains('start= demand')) "The isolated parser service must be demand-started."
Assert-True ($setup.Contains('NT AUTHORITY\LocalService')) "The isolated parser must not run under the LocalSystem printing identity."
Assert-True ($setup.Contains('Set-Service -Name $serviceName -StartupType Manual')) "The parser service must remain manual-start."
Assert-True ($starter.Contains("service.Start()")) "The upload path must start the parser on demand."
Assert-True ($agent.Contains("printExecutor.Print(")) "The Agent print dispatch loop must retain its direct spooler execution path."
Assert-True ($agent.Contains("printExecutor.ExportPdfUsingWindowsPrinterOrFallbackAsync(")) "The Agent PDF output path must remain in the print dispatch loop."
Assert-True (-not $agent.Contains("WindowsLocalDocumentAnalyzer")) "The print dispatch loop must not depend on the isolated upload parser."
Assert-True (-not $printExecutor.Contains("WindowsLocalDocumentWorkerServiceStarter")) "The GDI print executor must not start/wait for the upload parser."
Assert-True (-not $printExecutor.Contains("WindowsLocalDocumentAnalyzer")) "The GDI print executor must not depend on upload analysis."
Assert-True ($install.Contains("printing remains installed and running") -or $install.Contains("SmartPrint printing remains installed and running")) "Missing-parser installation must not uninstall or roll back the print Agent."
Assert-True ($lifecycle.Contains("Assert-OptionalParserService")) "Windows lifecycle acceptance must verify the parser is stopped while the print Agent is running."

Write-Host "PASS: no fixed CPU quota, scan/parser work is below normal priority, parser service is demand-started, and the print dispatch/GDI path is independent."
