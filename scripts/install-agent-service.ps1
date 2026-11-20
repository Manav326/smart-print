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

# The installer invokes this script hidden. Keep a small diagnostic trail in the
# current user's temp directory so failures can be investigated without changing
# service identity, production data ACLs, or printer configuration.
$installDiagnosticPath = Join-Path ([System.IO.Path]::GetTempPath()) "SmartPrint-Agent-Service-Install.log"
Remove-Item -LiteralPath $installDiagnosticPath -Force -ErrorAction SilentlyContinue
function Write-InstallDiagnostic {
    param([Parameter(Mandatory = $true)][string]$Message)
    $entry = "{0} | {1}" -f (Get-Date -Format "o"), $Message
    try {
        Add-Content -LiteralPath $installDiagnosticPath -Value $entry -Encoding utf8 -ErrorAction Stop
    }
    catch {
        # Diagnostics must never change installer success or rollback behaviour.
    }
}
Write-InstallDiagnostic "Started service installation script."
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from PowerShell as Administrator."
}

$resolvedExe = (Resolve-Path -LiteralPath $AgentExePath).Path
Write-InstallDiagnostic "Resolved Agent executable: $resolvedExe"
Write-InstallDiagnostic "Installer process identity: $($identity.Name)"
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
$agentDataRoot = Join-Path $env:ProgramData "QRtoPrint"
$localServiceAccount = "NT AUTHORITY\LocalService"

function Set-AgentServiceAccount {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("LocalSystem", "LocalService")]
        [string]$Account
    )

    $serviceAccountName = if ($Account -eq "LocalService") { $localServiceAccount } else { "LocalSystem" }

    # LocalSystem and LocalService are built-in accounts without passwords.
    # Omit password= entirely to avoid passing an empty native argument through
    # PowerShell's version-dependent command-line quoting rules.
    $commandOutput = & sc.exe config $serviceName obj= $serviceAccountName 2>&1
    $commandExitCode = $LASTEXITCODE
    $commandDetail = ($commandOutput | Out-String).Trim()
    Write-InstallDiagnostic "sc.exe config service account '$serviceAccountName' exit=$commandExitCode output='$commandDetail'"

    if ($commandExitCode -ne 0) {
        throw "Could not configure the SmartPrint Agent service identity as $Account. sc.exe exit=$commandExitCode. $commandDetail"
    }

    $configuredService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
    if (-not $configuredService -or
        ([string]$configuredService.StartName).Trim() -ine $serviceAccountName) {
        $observedStartName = if ($configuredService) { [string]$configuredService.StartName } else { "service not found" }
        throw "The Service Control Manager did not retain the requested $Account identity. Observed: '$observedStartName'."
    }
    Write-InstallDiagnostic "Verified service account configuration: $($configuredService.StartName)."
}

# Keep the old executable and firewall rule untouched until the new service has
# started and the replacement firewall rule has been created successfully.
$previousService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
$previousController = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$previousWasRunning = $null -ne $previousController -and $previousController.Status -eq "Running"
$previousServiceAccount = "LocalSystem"
if ($previousService -and -not $previousController) {
    throw "Windows returned an Agent service configuration but could not open its service controller. Refusing to modify the existing installation."
}
if ($previousService) {
    $previousStartName = ([string]$previousService.StartName).Trim()
    if ($previousStartName -match '^(LocalSystem|NT AUTHORITY\\LocalSystem)$') {
        $previousServiceAccount = "LocalSystem"
    }
    elseif ($previousStartName -match '^(LocalService|NT AUTHORITY\\LocalService)$') {
        $previousServiceAccount = "LocalService"
    }
    else {
        throw "The existing SmartPrint Agent uses unsupported service identity '$previousStartName'. Refusing to replace a service account that this installer cannot safely restore."
    }
}

# SmartPrint stores only its own machine identity, encrypted queue and network
# preference below this product-specific directory. Migrate the old SYSTEM-only
# tree while the Agent is stopped, then grant only SYSTEM, Administrators and
# LocalService. No printer, driver, port, default-printer or Spooler state is changed.
function Set-AgentDataPermissions {
    Write-InstallDiagnostic "Starting machine-data ACL migration at $agentDataRoot"
    New-Item -ItemType Directory -Force -Path $agentDataRoot | Out-Null
    $takeownPath = Join-Path $env:SystemRoot "System32\takeown.exe"
    $icaclsPath = Join-Path $env:SystemRoot "System32\icacls.exe"
    & $takeownPath /F $agentDataRoot /R /D Y /A | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not take administrative ownership of the existing SmartPrint machine-data tree for ACL migration."
    }

    # Reset any protected, old SYSTEM-only child ACLs to the parent baseline,
    # then replace inherited access with the exact SmartPrint principals.
    & $icaclsPath $agentDataRoot /reset /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not reset the SmartPrint machine-data ACLs for migration."
    }
    $agentDataAcl = @(
        "*S-1-5-18:(OI)(CI)F",      # LocalSystem
        "*S-1-5-32-544:(OI)(CI)F", # Built-in Administrators
        "*S-1-5-19:(OI)(CI)F"      # LocalService
    )
    # The recursive reset above re-enables parent inheritance on every existing
    # child. Apply the protected baseline only at the product root; NTFS then
    # propagates these inheritable ACEs without rewriting every file's DACL.
    & $icaclsPath $agentDataRoot /inheritance:r /grant:r @agentDataAcl /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not restrict SmartPrint machine data to SYSTEM, Administrators and LocalService."
    }

    # The encrypted document queue is more sensitive than pairing metadata.
    # Keep it private to SYSTEM and the LocalService Agent, excluding even the
    # Administrators group from its normal DACL.
    Write-InstallDiagnostic "Completed machine-data ACL migration."
    $privateQueueRoot = Join-Path $agentDataRoot "LocalQueue"
    if (Test-Path -LiteralPath $privateQueueRoot -PathType Container) {
        $privateQueueAcl = @(
            "*S-1-5-18:(OI)(CI)F", # LocalSystem
            "*S-1-5-19:(OI)(CI)F"  # LocalService
        )
        # The queue is a narrower security boundary. Protect only its root and
        # let all reset descendants inherit this SYSTEM/LocalService-only DACL.
        & $icaclsPath $privateQueueRoot /inheritance:r /grant:r @privateQueueAcl /C /Q | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restrict the encrypted SmartPrint queue to SYSTEM and LocalService."
        }

        & $icaclsPath $privateQueueRoot /setowner "*S-1-5-19" /T /C /Q | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not set the encrypted SmartPrint queue owner to LocalService."
        }
    }
}

$createdService = $false
$newFirewallRule = $null

try {
    if ($previousService) {
        if ($previousController.Status -ne "Stopped") {
            Stop-Service -Name $serviceName -Force
            (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        }

        Set-AgentDataPermissions

        & sc.exe config $serviceName binPath= $binaryPath start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not update the existing QRtoPrint service executable path." }
        Set-Service -Name $serviceName -StartupType Automatic
    }
    else {
        Set-AgentDataPermissions
        New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName "QRtoPrint Smart Print Agent" -Description "Secure local printer inventory and cloud heartbeat service for QRtoPrint." -StartupType Automatic | Out-Null
        $createdService = $true
    }

    # Use the built-in least-privilege identity. It can access machine-wide
    # printer queues that grant standard Print permission but has no admin token.
    Set-AgentServiceAccount -Account "LocalService"

    # Recover automatically from an unexpected process/service failure.
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure automatic QRtoPrint service recovery." }
    & sc.exe failureflag $serviceName 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not enable QRtoPrint service failure recovery." }

    Write-InstallDiagnostic "Starting $serviceName as $localServiceAccount."
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    if ((Get-Service -Name $serviceName).Status -ne "Running") {
        throw "Windows did not confirm that the new QRtoPrint service is running."
    }
    $installedService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'"
    if ($installedService.StartName -notmatch 'LocalService$') {
        throw "The SmartPrint Agent did not start under the required LocalService identity."
    }

    # Preserve the existing customer LAN behaviour; the service identity change
    # must not silently narrow which Windows network profiles can reach the Agent.
    Write-InstallDiagnostic "Creating the LocalSubnet firewall rule for TCP 17842."
    $newFirewallRule = New-NetFirewallRule -DisplayName $firewallRuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 17842 -Program $resolvedExe -Profile Any -RemoteAddress LocalSubnet -Description "Allows same-local-subnet devices to reach the SmartPrint local Agent endpoints."

    Write-InstallDiagnostic "Created firewall rule $($newFirewallRule.Name)."
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
        Get-ChildItem -LiteralPath $agentRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -ne $currentAgentDirectory } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-InstallDiagnostic "Install completed successfully."
    Write-Host "Configured the local Agent API CORS origin: $allowedOrigin"
    Write-Host "QRtoPrint Smart Print Agent $AgentVersion is installed and running." -ForegroundColor Green
    Write-Host "The local Agent accepts connections only from devices on the connected local subnet."
    Write-Host "Use Get-Service $serviceName to inspect the service state."
}
catch {
    $installationFailure = $_.Exception.Message
    $rollbackFailure = $null
    Write-InstallDiagnostic "INSTALL FAILURE: $installationFailure"
    if ($_.ScriptStackTrace) {
        Write-InstallDiagnostic "Failure stack: $($_.ScriptStackTrace)"
    }
    try {
        $failedService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
        $failedController = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        $failedState = if ($failedController) { [string]$failedController.Status } else { "Absent" }
        $failedStartName = if ($failedService) { [string]$failedService.StartName } else { "Unavailable" }
        $failedPath = if ($failedService) { [string]$failedService.PathName } else { "Unavailable" }
        Write-InstallDiagnostic "Service state at failure: $failedState; account: $failedStartName; path: $failedPath"
        $scQuery = (& sc.exe queryex $serviceName 2>&1 | Out-String).Trim()
        if ($scQuery) { Write-InstallDiagnostic "SCM query at failure: $scQuery" }
        $recentEvents = @(
            Get-WinEvent -FilterHashtable @{
                LogName = "System"
                Id = @(7000, 7001, 7009, 7011, 7023, 7024, 7038, 7041, 7036)
                StartTime = (Get-Date).AddMinutes(-10)
            } -ErrorAction SilentlyContinue |
            Select-Object -First 12 |
            ForEach-Object { "EventId=$($_.Id): $(($_.Message -replace '\s+', ' ').Trim())" }
        )
        foreach ($event in $recentEvents) {
            Write-InstallDiagnostic "Recent SCM event: $event"
        }
    }
    catch {
        Write-InstallDiagnostic "Could not collect service failure details: $($_.Exception.Message)"
    }

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
            Set-AgentServiceAccount -Account $previousServiceAccount
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

    Write-InstallDiagnostic "Rollback finished. Rollback failure: $(if ($rollbackFailure) { $rollbackFailure } else { 'none' })"
    if ($rollbackFailure) {
        throw "SmartPrint update failed: $installationFailure. Automatic rollback also failed: $rollbackFailure. Keep the old version folder and run setup again."
    }

    throw "SmartPrint update failed: $installationFailure. The previous Agent version and its service configuration were restored."
}
