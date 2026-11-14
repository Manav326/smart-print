[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from PowerShell as Administrator."
}

$serviceName = "QRtoPrintSmartPrintAgent"
$workerServiceName = "QRtoPrintSmartPrintDocumentWorker"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$workerService = Get-Service -Name $workerServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not remove the QRtoPrint Windows service." }
}
else {
    Write-Host "QRtoPrint Smart Print Agent is not installed."
}

# Stop the Agent before its parser service so no new parsing requests can arrive.
if ($workerService) {
    if ($workerService.Status -ne "Stopped") {
        Stop-Service -Name $workerServiceName -Force
        (Get-Service -Name $workerServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $workerServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not remove the isolated document worker service." }
}

# Only remove the local-network rule after the services have stopped/deleted.
# If stopping the service fails, leave its network configuration untouched.
$firewallRulePrefixes = @(
    "QRtoPrint Smart Print Agent Local API",
    "QRtoPrint Smart Print Document Worker outbound block"
)
Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object {
        $displayName = $_.DisplayName
        $firewallRulePrefixes | Where-Object { $displayName -like "$_*" }
    } |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue

Write-Host "QRtoPrint Smart Print Agent and isolated document worker services removed. The encrypted machine identity was left intact." -ForegroundColor Green
Write-Host "Revoke the agent in the shop workspace before disposing of this computer."
