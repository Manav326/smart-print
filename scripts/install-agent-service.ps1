[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AgentExePath,

    [Parameter()]
    [string]$AgentVersion = "current",

    [Parameter()]
    [string]$CustomerPortalOrigin = "http://localhost:3000",

    [Parameter()]
    [string]$ApiBaseUrl = "",

    [Parameter()]
    [switch]$AllowInsecureLocalDevelopment
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from PowerShell as Administrator."
}

$resolvedExe = (Resolve-Path -LiteralPath $AgentExePath).Path
$workerExeCandidate = Join-Path (Split-Path -Parent $resolvedExe) "SmartPrint.DocumentWorker.Windows.exe"
$workerSetupScript = Join-Path $PSScriptRoot "install-document-worker-service.ps1"
$workerPackageAvailable = Test-Path -LiteralPath $workerExeCandidate -PathType Leaf

$portalUri = $null
if (-not [Uri]::TryCreate($CustomerPortalOrigin.TrimEnd('/'), [UriKind]::Absolute, [ref]$portalUri)) {
    throw "Provide the canonical HTTPS customer portal origin without a path, query string, fragment, or credentials."
}
$isLocalhostHttp = $portalUri.Scheme -eq "http" -and $portalUri.Host -in @("localhost", "127.0.0.1")
$isPrivateLanHttp = $false
if ($portalUri.Scheme -eq "http" -and $AllowInsecureLocalDevelopment -and $portalUri.Port -eq 3000) {
    $portalAddress = [System.Net.IPAddress]::None
    if ([System.Net.IPAddress]::TryParse($portalUri.Host, [ref]$portalAddress) -and
        $portalAddress.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) {
        $octets = $portalAddress.GetAddressBytes()
        $isPrivateLanHttp = $octets[0] -eq 10 -or
            ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or
            ($octets[0] -eq 192 -and $octets[1] -eq 168)
    }
}
$isAllowedScheme = $portalUri.Scheme -eq "https" -or $isLocalhostHttp -or $isPrivateLanHttp
if (-not $isAllowedScheme -or $portalUri.AbsolutePath -ne "/" -or $portalUri.Query -or $portalUri.Fragment -or $portalUri.UserInfo) {
    throw "Use HTTPS normally. HTTP is allowed for localhost, or a private IPv4 portal on port 3000 only with -AllowInsecureLocalDevelopment."
}

$allowedOrigin = $portalUri.GetLeftPart([UriPartial]::Authority)
$apiValue = if ([string]::IsNullOrWhiteSpace($ApiBaseUrl)) { $CustomerPortalOrigin } else { $ApiBaseUrl }
$apiUri = $null
if (-not [Uri]::TryCreate($apiValue.TrimEnd('/'), [UriKind]::Absolute, [ref]$apiUri) -or
    $apiUri.AbsolutePath -ne "/" -or $apiUri.Query -or $apiUri.Fragment -or $apiUri.UserInfo -or
    -not ($apiUri.Scheme -eq "https" -or ($apiUri.Scheme -eq "http" -and $apiUri.Host -in @("localhost", "127.0.0.1")))) {
    throw "The Agent API address must be a canonical HTTPS origin. HTTP is supported only for localhost development."
}
$apiOrigin = $apiUri.GetLeftPart([UriPartial]::Authority)

$settingsPath = Join-Path (Split-Path -Parent $resolvedExe) "appsettings.json"
$settings = if (Test-Path -LiteralPath $settingsPath) {
    Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
} else {
    [PSCustomObject]@{}
}
if (-not $settings.PSObject.Properties["Agent"] -or $null -eq $settings.Agent) {
    $settings | Add-Member -NotePropertyName Agent -NotePropertyValue ([PSCustomObject]@{}) -Force
}
if (-not $settings.Agent.PSObject.Properties["LocalApi"] -or $null -eq $settings.Agent.LocalApi) {
    $settings.Agent | Add-Member -NotePropertyName LocalApi -NotePropertyValue ([PSCustomObject]@{}) -Force
}
$settings.Agent | Add-Member -NotePropertyName ApiBaseUrl -NotePropertyValue $apiOrigin -Force
$settings.Agent.LocalApi | Add-Member -NotePropertyName Enabled -NotePropertyValue $true -Force
$settings.Agent.LocalApi | Add-Member -NotePropertyName AllowedOrigins -NotePropertyValue @($allowedOrigin, "http://tauri.localhost") -Force
$settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8

$serviceName = "QRtoPrintSmartPrintAgent"
$binaryPath = '"' + $resolvedExe + '"'
$firewallRulePrefix = "QRtoPrint Smart Print Agent Local API"
$firewallRuleName = "$firewallRulePrefix - $AgentVersion"

# Keep the old executable and firewall rule untouched until the new service has
# started and the replacement firewall rule has been created successfully.
$previousService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
$previousController = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$previousWasRunning = $null -ne $previousController -and $previousController.Status -eq "Running"
$createdService = $false
$newFirewallRule = $null

try {
    if ($previousService) {
        if ($previousController.Status -ne "Stopped") {
            Stop-Service -Name $serviceName -Force
            (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        }

        & sc.exe config $serviceName binPath= $binaryPath start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not update the existing QRtoPrint service executable path." }
        Set-Service -Name $serviceName -StartupType Automatic
    }
    else {
        New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName "QRtoPrint Smart Print Agent" -Description "Secure local printer inventory and cloud heartbeat service for QRtoPrint." -StartupType Automatic | Out-Null
        $createdService = $true
    }

    # Recover automatically from an unexpected process/service failure.
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure automatic QRtoPrint service recovery." }
    & sc.exe failureflag $serviceName 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not enable QRtoPrint service failure recovery." }

    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    if ((Get-Service -Name $serviceName).Status -ne "Running") {
        throw "Windows did not confirm that the new QRtoPrint service is running."
    }

    $newFirewallRule = New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 17842 -Program $resolvedExe -Profile Any -RemoteAddress LocalSubnet -Description "Allows same-local-subnet devices to reach the QRtoPrint local agent status and upload endpoints."

    # The parser is optional to the printing service. Install/configure it only
    # after the main Agent is confirmed healthy. A parser setup failure blocks
    # document uploads, never the print queue, spooler monitoring or accepted jobs.
    $workerReady = $false
    if ($workerPackageAvailable -and (Test-Path -LiteralPath $workerSetupScript -PathType Leaf)) {
        try {
            & $workerSetupScript -WorkerExePath $workerExeCandidate
            $workerReady = $true
        }
        catch {
            Write-Warning "The isolated document parser could not be configured: $($_.Exception.Message)"
            Write-Warning "SmartPrint printing remains installed and running. New document uploads/photo revisions will return a temporary-unavailable response until the parser is repaired."
        }
    }
    else {
        Write-Warning "The isolated document parser package/helper is missing. SmartPrint printing remains installed and running; new uploads and photo revisions are blocked until the parser is repaired."
    }

    # The new version is running and reachable. Old version-specific rules are
    # now safe to remove; any cleanup failure must not roll back a healthy Agent.
    Get-NetFirewallRule -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "$firewallRulePrefix*" -and $_.Name -ne $newFirewallRule.Name } |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue

    # The replacement service is now confirmed healthy. Remove older versioned
    # payloads to avoid accumulating unused binaries in Program Files. This is
    # deliberately after the successful start/firewall checks so rollback always
    # has the previous executable available until the switch is complete.
    $currentAgentDirectory = Split-Path -Parent $resolvedExe
    $agentRoot = Split-Path -Parent $currentAgentDirectory
    if ((Split-Path -Leaf $agentRoot) -ieq "agent") {
        $keepDirectories = [System.Collections.Generic.List[string]]::new()
        $keepDirectories.Add($currentAgentDirectory)

        # If parser setup failed during an upgrade, its previous executable must
        # remain on disk. Removing that version would orphan the optional worker.
        $workerRegistration = Get-CimInstance -ClassName Win32_Service -Filter "Name='QRtoPrintSmartPrintDocumentWorker'" -ErrorAction SilentlyContinue
        if ($workerRegistration -and -not [string]::IsNullOrWhiteSpace([string]$workerRegistration.PathName)) {
            $workerImage = [string]$workerRegistration.PathName
            if ($workerImage -match '^\s*"([^"]+)"') { $workerImage = $Matches[1] }
            else { $workerImage = ($workerImage -split '\s+', 2)[0] }
            $workerDirectory = Split-Path -Parent $workerImage
            $workerDirectoryAlreadyKept = $false
            foreach ($keepDirectory in $keepDirectories) {
                if ([string]::Equals($keepDirectory, $workerDirectory, [StringComparison]::OrdinalIgnoreCase)) {
                    $workerDirectoryAlreadyKept = $true
                    break
                }
            }
            if ((Test-Path -LiteralPath $workerDirectory -PathType Container) -and -not $workerDirectoryAlreadyKept) {
                $keepDirectories.Add($workerDirectory)
            }
        }

        Get-ChildItem -LiteralPath $agentRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object {
                $candidate = $_.FullName
                -not ($keepDirectories | Where-Object { [string]::Equals($_, $candidate, [StringComparison]::OrdinalIgnoreCase) })
            } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Configured the local Agent API CORS origin: $allowedOrigin"
    Write-Host "QRtoPrint Smart Print Agent $AgentVersion is installed and running." -ForegroundColor Green
    Write-Host "The local Agent accepts connections only from devices on the connected local subnet."
    if ($workerReady) {
        Write-Host "Document parsing is optional, LocalService-isolated and demand-started; it stops after two minutes idle."
    } else {
        Write-Warning "Printing is healthy. Document uploads/photo revisions are temporarily disabled because the isolated parser is unavailable."
    }
    Write-Host "Use Get-Service $serviceName to inspect the service state."
}
catch {
    $installationFailure = $_.Exception.Message
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
            if ($LASTEXITCODE -ne 0) { throw "The failed new service could not be removed." }
        }
        elseif ($previousService) {
            $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($current -and $current.Status -ne "Stopped") {
                Stop-Service -Name $serviceName -Force
                (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
            }

            $previousStart = switch ($previousService.StartMode) {
                "Manual" { "demand" }
                "Disabled" { "disabled" }
                default { "auto" }
            }
            $previousPowerShellStart = switch ($previousService.StartMode) {
                "Manual" { "Manual" }
                "Disabled" { "Disabled" }
                default { "Automatic" }
            }

            & sc.exe config $serviceName binPath= $previousService.PathName start= $previousStart | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "The previous QRtoPrint service executable path could not be restored." }
            Set-Service -Name $serviceName -StartupType $previousPowerShellStart

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
        throw "SmartPrint update failed: $installationFailure. Automatic rollback also failed: $rollbackFailure. Keep the old version folder and run setup again."
    }

    throw "SmartPrint update failed: $installationFailure. The previous Agent version and its service configuration were restored."
}
