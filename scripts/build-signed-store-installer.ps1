$hasCertificate = -not [string]::IsNullOrWhiteSpace($env:WINDOWS_CODESIGN_PFX_BASE64) -and -not [string]::IsNullOrWhiteSpace($env:WINDOWS_CODESIGN_PFX_PASSWORD)
$hasRuntimeUrl = -not [string]::IsNullOrWhiteSpace($env:WEBVIEW2_STANDALONE_X64_URL)
$hasPublisher = -not [string]::IsNullOrWhiteSpace($env:SMARTPRINT_WINDOWS_PUBLISHER)
$missing = @()
if (-not $hasCertificate) { $missing += "Windows code-signing PFX secrets" }
if (-not $hasRuntimeUrl) { $missing += "WebView2 Evergreen Standalone x64 URL" }
if (-not $hasPublisher) { $missing += "Partner Center publisher name" }
if ($missing.Count -gt 0) {
  "available=false" >> $env:GITHUB_OUTPUT
  Write-Host "Signed Windows packages skipped; missing configuration: $($missing -join ', ')."
  exit 0
}
"available=true" >> $env:GITHUB_OUTPUT
$storeAgent = "artifacts/store-agent"
$storeDesktop = "artifacts/store-desktop"
New-Item -ItemType Directory -Force -Path $storeAgent, $storeDesktop | Out-Null
Copy-Item "artifacts/agent/*" $storeAgent -Recurse -Force
Copy-Item "artifacts/desktop/*" $storeDesktop -Recurse -Force
$runtimeUri = [Uri]$env:WEBVIEW2_STANDALONE_X64_URL
if (-not $runtimeUri.IsAbsoluteUri -or $runtimeUri.Scheme -ne "https" -or -not [string]::IsNullOrWhiteSpace($runtimeUri.UserInfo)) {
  throw "WEBVIEW2_STANDALONE_X64_URL must be an absolute HTTPS URL without embedded credentials."
}
$runtime = Join-Path $storeDesktop "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
Invoke-WebRequest -Uri $runtimeUri -OutFile $runtime -MaximumRedirection 10 -TimeoutSec 300
# The Evergreen bootstrapper is only a small network downloader. A
# standalone x64 runtime is substantially larger; reject bootstrapper
# URLs before packaging so the Store installer is genuinely offline.
if ((Get-Item -LiteralPath $runtime).Length -lt 50MB) {
  throw "The WebView2 download is too small to be the standalone x64 runtime. Do not use the Evergreen Bootstrapper URL."
}
$runtimeSignature = Get-AuthenticodeSignature -LiteralPath $runtime
if ($runtimeSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or $runtimeSignature.SignerCertificate.Subject -notmatch "Microsoft") {
  throw "WebView2 Evergreen Standalone Installer did not pass Microsoft Authenticode verification."
}
$pfxPath = Join-Path $env:RUNNER_TEMP "smartprint-code-signing.pfx"
$thumbprint = $null
try {
  [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:WINDOWS_CODESIGN_PFX_BASE64))
  $pfxPassword = ConvertTo-SecureString $env:WINDOWS_CODESIGN_PFX_PASSWORD -AsPlainText -Force
  $signingCertificate = @(Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation Cert:\CurrentUser\My -Password $pfxPassword | Where-Object { $_.HasPrivateKey } | Select-Object -First 1)
  if ($signingCertificate.Count -ne 1) { throw "The PFX did not import exactly one code-signing certificate with a private key." }
  $certificate = $signingCertificate[0]
  $now = [DateTime]::UtcNow
  if ($now -lt $certificate.NotBefore.ToUniversalTime() -or $now -gt $certificate.NotAfter.ToUniversalTime()) {
    throw "The Windows code-signing certificate is not currently valid."
  }
  $codeSigningEku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.37" } | ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
  if ($codeSigningEku.Count -eq 0 -or $codeSigningEku -notcontains "1.3.6.1.5.5.7.3.3") {
    throw "The PFX certificate is not explicitly valid for Code Signing."
  }
  $certificatePublisher = $certificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
  if ([string]::IsNullOrWhiteSpace($certificatePublisher)) {
    throw "The code-signing certificate has no readable publisher identity."
  }
  if ($certificate.Subject -notmatch [regex]::Escape($env:SMARTPRINT_WINDOWS_PUBLISHER)) {
    throw "The code-signing certificate subject does not contain the configured Partner Center publisher identity."
  }
  Write-Host "Using code-signing certificate publisher identity: $certificatePublisher"
  $thumbprint = $certificate.Thumbprint
  $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -File |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
  if (-not $signtool) { throw "Windows SDK signtool.exe was not found." }
  # Detect PE files by the DOS MZ signature, not filename extension. This
  # includes native modules such as .node and avoids silently omitting a
  # bundled executable because it uses an unexpected extension.
  $peFiles = @()
  foreach ($candidate in (Get-ChildItem $storeAgent, $storeDesktop -Recurse -File)) {
    $stream = [System.IO.File]::OpenRead($candidate.FullName)
    try {
      $first = $stream.ReadByte()
      $second = $stream.ReadByte()
    }
    finally {
      $stream.Dispose()
    }
    if (
      $first -eq 0x4D -and $second -eq 0x5A -and
      -not [string]::Equals($candidate.FullName, $runtime, [StringComparison]::OrdinalIgnoreCase)
    ) {
      $peFiles += $candidate
    }
  }
  if ($peFiles.Count -eq 0) { throw "No SmartPrint PE binaries were found for signing." }
  foreach ($file in $peFiles) {
    & $signtool sign /fd SHA256 /td SHA256 /tr "http://timestamp.digicert.com" /sha1 $thumbprint $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed for $($file.FullName)." }
    & $signtool verify /pa /all $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $($file.FullName)." }
  }
  $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
  # Keep the already-built unsigned website installer untouched.
  Remove-Item -LiteralPath "artifacts/agent-installer/SmartPrint-Windows-Store-Setup.exe" -Force -ErrorAction SilentlyContinue
  $commonArgs = @(
    "/DSmartPrintPublicUrl=$env:SMARTPRINT_PUBLIC_BASE_URL",
    "/DSmartPrintVersion=$($env:RELEASE_TAG -replace '^v', '')",
    "/DSmartPrintBuildId=$env:GITHUB_RUN_NUMBER",
    "/DSmartPrintAgentSourceDir=artifacts\store-agent",
    "/DSmartPrintDesktopSourceDir=artifacts\store-desktop",
    "/DSmartPrintPublisher=$env:SMARTPRINT_WINDOWS_PUBLISHER",
    "/DSmartPrintSignToolName=SmartPrintSignTool",
    "scripts/build-windows-installer.iss"
  )
  # Inno Setup requires $q placeholders for quotes embedded in a
  # command-line SignTool definition; raw quotes are parsed as grouping.
  $signToolCommand = 'SmartPrintSignTool=$q' + $signtool + '$q sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /sha1 ' + $thumbprint + ' $f'
  $signToolArg = "--signtool=$signToolCommand"
  
  $storeArgs = @($signToolArg) + $commonArgs + @("/DSmartPrintOutputBaseFilename=SmartPrint-Windows-Store-Setup")
  & $iscc @storeArgs
  if ($LASTEXITCODE -ne 0) { throw "Signed offline Microsoft Store installer compilation failed." }
  $storeInstaller = "artifacts/agent-installer/SmartPrint-Windows-Store-Setup.exe"
  & $signtool verify /pa /all $storeInstaller
  if ($LASTEXITCODE -ne 0) { throw "Microsoft Store installer signature verification failed." }
  
  
  Write-Host "Verified the separate signed offline Microsoft Store installer, including its signed uninstaller. The website installer remains the intentionally unsigned direct-download build."
}
finally {
  if (Test-Path -LiteralPath $pfxPath) {
    Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
  }
  if (-not [string]::IsNullOrWhiteSpace($thumbprint)) {
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -Force -ErrorAction SilentlyContinue
  }
}