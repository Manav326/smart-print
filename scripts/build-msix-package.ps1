[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DesktopDirectory,
    [Parameter(Mandatory=$true)][string]$AgentDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9.-]{3,50}$')][string]$PackageName,
    [Parameter(Mandatory=$true)][ValidatePattern('^CN=.+$')][string]$Publisher,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$AppDisplayName,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][ValidatePattern('^https://')][string]$PublicApiOrigin,
    [Parameter(Mandatory=$true)][string]$IconPath,
    [Parameter(Mandatory=$true)][string]$SigningCertificatePath,
    [Parameter(Mandatory=$true)][string]$SigningCertificatePassword,
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$PublisherDisplayName
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-WindowsSdkTool([string]$ToolName) {
    $root = Join-Path $env:SystemDrive "Program Files (x86)\Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $root)) { throw "Windows SDK tools were not found at '$root'." }
    $item = Get-ChildItem -LiteralPath $root -Directory |
        Sort-Object { try { [version]$_.Name } catch { [version]"0.0" } } -Descending |
        ForEach-Object {
            $candidate = Join-Path $_.FullName "x64\$ToolName"
            if (Test-Path -LiteralPath $candidate) { Get-Item -LiteralPath $candidate }
        } | Select-Object -First 1
    if (-not $item) { throw "Could not locate $ToolName in the installed Windows SDK." }
    return $item.FullName
}

function Set-JsonProperty([object]$Object, [string]$Name, [object]$Value) {
    if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value }
    else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

function Invoke-NativeTool {
    param(
        [Parameter(Mandatory=$true)][string]$FilePath,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [Parameter(Mandatory=$true)][string]$Operation,
        [ValidateRange(1,1800)][int]$TimeoutSeconds = 600
    )

    Write-Host "BEGIN: $Operation (timeout: $TimeoutSeconds seconds)"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not start $Operation." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
            $process.WaitForExit()
            throw "$Operation exceeded its $TimeoutSeconds-second timeout and was terminated."
        }
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if (-not [string]::IsNullOrWhiteSpace($stdout)) { Write-Host $stdout.TrimEnd() }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Host $stderr.TrimEnd() }
        if ($process.ExitCode -ne 0) {
            throw "$Operation failed with exit code $($process.ExitCode)."
        }
        Write-Host "END: $Operation (exit code 0)"
    }
    finally {
        $process.Dispose()
    }
}

Write-Host "Resolving MSIX input directories and validating signing identity."
$desktopRoot = (Resolve-Path -LiteralPath $DesktopDirectory).Path
$agentRoot = (Resolve-Path -LiteralPath $AgentDirectory).Path
$iconFullPath = (Resolve-Path -LiteralPath $IconPath).Path
$certificateFullPath = (Resolve-Path -LiteralPath $SigningCertificatePath).Path
$apiUri = [Uri]$PublicApiOrigin
if ($apiUri.Scheme -ne "https" -or $apiUri.AbsolutePath -notin @("", "/") -or $apiUri.Query -or $apiUri.Fragment -or $apiUri.UserInfo) {
    throw "PublicApiOrigin must be a canonical HTTPS origin."
}
$portalOrigin = $apiUri.GetLeftPart([UriPartial]::Authority)
if (-not (Test-Path -LiteralPath (Join-Path $desktopRoot "SmartPrint.Desktop.exe"))) { throw "Native desktop executable is missing." }
if (-not (Test-Path -LiteralPath (Join-Path $desktopRoot "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"))) { throw "Microsoft-signed Evergreen WebView2 standalone runtime installer is missing." }
if (-not (Test-Path -LiteralPath (Join-Path $agentRoot "SmartPrint.Agent.exe"))) { throw "Self-contained Agent executable is missing." }

$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificateFullPath,
    $SigningCertificatePassword,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
)
if (-not $cert.HasPrivateKey -or $cert.Subject -ine $Publisher) {
    throw "MSIX publisher '$Publisher' must exactly match signing certificate subject '$($cert.Subject)'."
}
$hasEku = $false
foreach ($extension in $cert.Extensions) {
    if ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
        $hasEku = @($extension.EnhancedKeyUsages | ForEach-Object { $_.Value }) -contains "1.3.6.1.5.5.7.3.3"
    }
}
if (-not $hasEku -or $cert.NotAfter -le (Get-Date) -or $cert.NotBefore -gt (Get-Date)) {
    throw "Signing certificate must be currently valid and include the Code Signing EKU."
}

$makeAppx = Resolve-WindowsSdkTool "MakeAppx.exe"
$signTool = Resolve-WindowsSdkTool "SignTool.exe"
$out = [IO.Path]::GetFullPath($OutputPath)
$outDir = Split-Path -Parent $out
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Write-Host "Staging desktop and Agent payloads."
$stage = Join-Path $outDir "SmartPrint-MSIX-Staging"
$verify = Join-Path $outDir "SmartPrint-MSIX-Verify"
Remove-Item -LiteralPath $stage, $verify, $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage, (Join-Path $stage "Agent"), (Join-Path $stage "Assets") | Out-Null
Copy-Item -Path (Join-Path $desktopRoot "*") -Destination $stage -Recurse -Force
Write-Host "Desktop payload staged."
Copy-Item -Path (Join-Path $agentRoot "*") -Destination (Join-Path $stage "Agent") -Recurse -Force
Write-Host "Agent payload staged."
$stagedFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse)
$stagedBytes = ($stagedFiles | Measure-Object -Property Length -Sum).Sum
Write-Host "Staged $($stagedFiles.Count) files ($([math]::Round($stagedBytes / 1MB, 1)) MiB) for MSIX compression."

# MSIX files are immutable. Embed the API origin and the same Tauri CORS origin
# that the website installer currently writes into the Agent configuration.
$settingsPath = Join-Path $stage "Agent\appsettings.json"
$settings = if (Test-Path -LiteralPath $settingsPath) {
    Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
} else {
    [PSCustomObject]@{}
}
if (-not $settings.PSObject.Properties["Agent"] -or $null -eq $settings.Agent) {
    Set-JsonProperty $settings "Agent" ([PSCustomObject]@{})
}
if (-not $settings.Agent.PSObject.Properties["LocalApi"] -or $null -eq $settings.Agent.LocalApi) {
    Set-JsonProperty $settings.Agent "LocalApi" ([PSCustomObject]@{})
}
Set-JsonProperty $settings.Agent "ApiBaseUrl" $portalOrigin
Set-JsonProperty $settings.Agent.LocalApi "Enabled" $true
Set-JsonProperty $settings.Agent.LocalApi "AllowedOrigins" @($portalOrigin, "http://tauri.localhost")
$settings | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $settingsPath -Encoding utf8

Write-Host "Preparing Agent configuration and package assets."
# Reuse the checked-in Windows icon for the MSIX logos.
Write-Host "Generating MSIX logos from $iconFullPath."
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::new($iconFullPath)
if (-not $icon) { throw "Could not load SmartPrint Windows icon." }
try {
    foreach ($asset in @(
        @{ Name = "Square150x150Logo.png"; Size = 150 },
        @{ Name = "Square44x44Logo.png"; Size = 44 },
        @{ Name = "StoreLogo.png"; Size = 50 }
    )) {
        $bitmap = [System.Drawing.Bitmap]::new($asset.Size, $asset.Size)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawIcon($icon, [System.Drawing.Rectangle]::new(0, 0, $asset.Size, $asset.Size))
            } finally { $graphics.Dispose() }
            $bitmap.Save((Join-Path $stage "Assets\$($asset.Name)"), [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $bitmap.Dispose() }
    }
} finally { $icon.Dispose() }
Write-Host "MSIX logo generation completed."

$esc = { param([string]$value) [System.Security.SecurityElement]::Escape($value) }
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
 xmlns:desktop2="http://schemas.microsoft.com/appx/manifest/desktop/windows10/2"
 xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap desktop desktop2 desktop6 rescap">
 <Identity Name="$(& $esc $PackageName)" Publisher="$(& $esc $Publisher)" Version="$Version" ProcessorArchitecture="x64" />
 <Properties><DisplayName>$(& $esc $AppDisplayName)</DisplayName><PublisherDisplayName>$(& $esc $PublisherDisplayName)</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
 <Resources><Resource Language="en-us" /></Resources>
 <Applications><Application Id="SmartPrint" Executable="SmartPrint.Desktop.exe" EntryPoint="Windows.FullTrustApplication">
  <uap:VisualElements DisplayName="$(& $esc $AppDisplayName)" Description="SmartPrint desktop printing and shop connectivity" BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
  <Extensions><desktop6:Extension Category="windows.service" Executable="Agent\SmartPrint.Agent.exe"><desktop6:Service Name="QRtoPrint Smart Print Agent" StartupType="auto" StartAccount="localService" /></desktop6:Extension></Extensions>
 </Application></Applications>
 <Extensions><desktop2:Extension Category="windows.firewallRules"><desktop2:FirewallRules Executable="Agent\SmartPrint.Agent.exe"><desktop2:Rule Direction="in" IPProtocol="TCP" LocalPortMin="17842" LocalPortMax="17842" Profile="all" /></desktop2:FirewallRules></desktop2:Extension></Extensions>
 <Capabilities><uap:Capability Name="privateNetworkClientServer" /><rescap:Capability Name="runFullTrust" /><rescap:Capability Name="packagedServices" /></Capabilities>
</Package>
"@

Write-Host "Writing and validating AppxManifest.xml."
$manifestPath = Join-Path $stage "AppxManifest.xml"
Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding utf8
$null = [xml](Get-Content -LiteralPath $manifestPath -Raw)

Invoke-NativeTool -FilePath $makeAppx -Arguments @("pack", "/d", $stage, "/p", $out, "/o") -Operation "MakeAppx package compression" -TimeoutSeconds 900
if (-not (Test-Path -LiteralPath $out -PathType Leaf)) { throw "MakeAppx did not create the MSIX package." }
$packageBytes = (Get-Item -LiteralPath $out).Length
Write-Host "MSIX compressed: $([math]::Round($packageBytes / 1MB, 1)) MiB."
Write-Host "Signing MSIX with the temporary publisher-matched certificate (no network timestamp; Store re-signs the package)."
Invoke-NativeTool -FilePath $signTool -Arguments @("sign", "/fd", "SHA256", "/f", $certificateFullPath, "/p", $SigningCertificatePassword, $out) -Operation "SignTool package signing" -TimeoutSeconds 180
Write-Host "Verifying MSIX signature."
Invoke-NativeTool -FilePath $signTool -Arguments @("verify", "/pa", "/v", $out) -Operation "SignTool signature verification" -TimeoutSeconds 180
Write-Host "Unpacking signed MSIX and validating package contents."
Invoke-NativeTool -FilePath $makeAppx -Arguments @("unpack", "/p", $out, "/d", $verify, "/o") -Operation "MakeAppx package verification/unpack" -TimeoutSeconds 900

foreach ($file in @(
    "AppxManifest.xml",
    "SmartPrint.Desktop.exe",
    "Agent\SmartPrint.Agent.exe",
    "Agent\appsettings.json",
    "Assets\Square150x150Logo.png",
    "Assets\Square44x44Logo.png",
    "Assets\StoreLogo.png"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $verify $file) -PathType Leaf)) {
        throw "MSIX is missing required file '$file'."
    }
}

$packagedSettingsPath = Join-Path $verify "Agent\appsettings.json"
$packagedSettings = Get-Content -LiteralPath $packagedSettingsPath -Raw | ConvertFrom-Json
$allowedOrigins = @($packagedSettings.Agent.LocalApi.AllowedOrigins)
if ($allowedOrigins -notcontains $portalOrigin) { throw "Packaged Agent CORS settings are missing the customer portal origin: $PublicApiOrigin" }
if ($allowedOrigins -notcontains "http://tauri.localhost") { throw "Packaged Agent CORS settings are missing the Tauri desktop origin." }
if ($allowedOrigins -contains "http://localhost:3000") { throw "Development localhost origin must not be included in the Store package." }

$m = [xml](Get-Content -LiteralPath (Join-Path $verify "AppxManifest.xml") -Raw)
$n = [System.Xml.XmlNamespaceManager]::new($m.NameTable)
$n.AddNamespace("d6", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6")
$n.AddNamespace("d2", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/2")
$n.AddNamespace("rc", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities")
$n.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
if (-not $m.SelectSingleNode("//d6:Service[@Name='QRtoPrint Smart Print Agent' and @StartAccount='localService' and @StartupType='auto']", $n)) {
    throw "Packaged LocalService Agent declaration is missing."
}
if (-not $m.SelectSingleNode("//d2:FirewallRules[@Executable='Agent\SmartPrint.Agent.exe']/d2:Rule[@Direction='in' and @IPProtocol='TCP' and @LocalPortMin='17842' and @LocalPortMax='17842' and @Profile='all']", $n)) {
    throw "All-profile LAN firewall rule for Agent TCP 17842 is missing."
}
if (-not $m.SelectSingleNode("//rc:Capability[@Name='packagedServices']", $n)) { throw "packagedServices restricted capability is missing." }
if (-not $m.SelectSingleNode("//rc:Capability[@Name='runFullTrust']", $n)) { throw "runFullTrust restricted capability is missing." }
if (-not $m.SelectSingleNode("//uap:Capability[@Name='privateNetworkClientServer']", $n)) { throw "Private network capability is missing." }
if ($m.SelectSingleNode("//rc:Capability[@Name='localSystemServices']", $n)) {
    throw "MSIX must not request localSystemServices; the Agent must run as LocalService."
}
if ($m.SelectSingleNode("//uap:Capability[@Name='internetClient']", $n)) {
    throw "Unnecessary internetClient capability must not be declared for this full-trust desktop package."
}

Write-Host "SmartPrint MSIX created and signature/manifest verified: $out"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant())"
Write-Host "Microsoft Store approval for runFullTrust and packagedServices remains a separate release gate."
