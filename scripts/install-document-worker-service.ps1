[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$WorkerExePath
)

$ErrorActionPreference = "Stop"
$serviceName = "QRtoPrintSmartPrintDocumentWorker"
$firewallRuleName = "QRtoPrint Smart Print Document Worker outbound block"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this worker setup helper from PowerShell as Administrator."
}

$resolvedWorkerExe = (Resolve-Path -LiteralPath $WorkerExePath -ErrorAction Stop).Path
if ([IO.Path]::GetFileName($resolvedWorkerExe) -ne "SmartPrint.DocumentWorker.Windows.exe") {
    throw "The parser worker executable name is not the expected SmartPrint.DocumentWorker.Windows.exe."
}

$previousService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
$previousController = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$previousWasRunning = $null -ne $previousController -and $previousController.Status -eq "Running"
$previousImagePath = if ($previousService) { [string]$previousService.PathName } else { $null }
$createdService = $false
$newFirewallRule = $null

try {
    if ($previousService) {
        if ($previousController.Status -ne "Stopped") {
            Stop-Service -Name $serviceName -Force
            (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        }

        & sc.exe config $serviceName binPath= ('"' + $resolvedWorkerExe + '"') start= demand obj= "NT AUTHORITY\LocalService" password= "" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not update the isolated parser service to the new version."
        }
    }
    else {
        & sc.exe create $serviceName binPath= ('"' + $resolvedWorkerExe + '"') start= demand obj= "NT AUTHORITY\LocalService" password= "" DisplayName= "SmartPrint Isolated Document Worker" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not create the optional isolated document worker service."
        }
        $createdService = $true
    }

    # Restricted service SID and minimum privilege keep parsing separate from the print Agent.
    & sc.exe sidtype $serviceName restricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not enable the restricted document-worker service SID." }

    & sc.exe privs $serviceName SeChangeNotifyPrivilege | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not reduce the document-worker service privileges." }

    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure document-worker recovery after a genuine crash." }
    & sc.exe failureflag $serviceName 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not enable document-worker failure recovery." }

    $workerServiceSid = "NT SERVICE\$serviceName"
    & icacls.exe (Split-Path -Parent $resolvedWorkerExe) /grant "$($workerServiceSid):(OI)(CI)(RX)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not grant the isolated worker read/execute access to its versioned application folder." }

    $newFirewallRule = New-NetFirewallRule -DisplayName $firewallRuleName -Direction Outbound -Action Block -Program $resolvedWorkerExe -Profile Any -RemoteAddress Any -Description "The isolated local document parser has no need for outbound network access."
    Set-Service -Name $serviceName -StartupType Manual

    # Deliberately do not start the service here. The Agent starts it only when
    # an upload/photo revision needs parsing. It exits after two minutes idle.
    Write-Host "Configured the optional document parser under NT AUTHORITY\LocalService." -ForegroundColor Green
    Write-Host "It is Manual-start and starts only for document analysis; it does not gate or run the print queue."
}
catch {
    $setupFailure = $_.Exception.Message
    $rollbackFailure = $null

    try {
        if ($newFirewallRule) {
            Remove-NetFirewallRule -InputObject $newFirewallRule -ErrorAction SilentlyContinue
        }

        if ($createdService) {
            $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($current -and $current.Status -ne "Stopped") {
                Stop-Service -Name $serviceName -Force
                (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
            }
            & sc.exe delete $serviceName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not remove the failed parser-service registration." }
        }
        elseif ($previousService) {
            $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($current -and $current.Status -ne "Stopped") {
                Stop-Service -Name $serviceName -Force
                (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
            }

            & sc.exe config $serviceName binPath= $previousImagePath start= demand obj= "NT AUTHORITY\LocalService" password= "" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not restore the previous parser-service executable path." }

            if ($previousWasRunning) {
                Start-Service -Name $serviceName
                (Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
            }
        }
    }
    catch {
        $rollbackFailure = $_.Exception.Message
    }

    if ($rollbackFailure) {
        throw "The isolated parser setup failed: $setupFailure. Parser rollback also failed: $rollbackFailure. The primary SmartPrint printing Agent was not changed."
    }

    throw "The isolated parser setup failed: $setupFailure. The primary SmartPrint printing Agent was not changed."
}
