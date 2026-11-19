# Removes only the isolated queue and path-backed port created by install-epson-l360-driver.ps1.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$QueueName,
    [Parameter(Mandatory = $true)][string]$PortName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows -and $env:OS -ne "Windows_NT") {
    throw "Epson L360 QA cleanup requires Windows."
}
$printer = Get-Printer -Name $QueueName -ErrorAction SilentlyContinue
if ($printer) {
    if ($printer.PortName -ne $PortName) {
        throw "Refusing to remove queue '$QueueName' because it no longer references the QA-owned port."
    }
    Remove-Printer -Name $QueueName -ErrorAction Stop
}
$port = Get-PrinterPort -Name $PortName -ErrorAction SilentlyContinue
if ($port) {
    $remainingUsers = Get-Printer -ErrorAction SilentlyContinue |
        Where-Object { $_.PortName -eq $PortName }
    if ($remainingUsers) {
        throw "Refusing to remove Epson QA port '$PortName' while another queue still uses it."
    }
    Remove-PrinterPort -Name $PortName -ErrorAction Stop
}
Write-Host "[EPSON-L360] Removed isolated queue and port only. The signed driver package may remain staged in this disposable runner's driver store."
