[CmdletBinding()]
param(
    [switch]$SkipUnitTests,
    [switch]$SkipPlaywrightInstall,
    [switch]$E2EOnly,
    [switch]$PullLatest,
    [switch]$ReuseRunningStack,
    [switch]$TestPublishedApps,
    [switch]$AndroidAppOnly,
    [string]$AndroidDeviceSerial = "",
    [switch]$InstallPublishedAndroidApk,
    [string]$LocalAndroidDeviceSerial = ""
)

$ErrorActionPreference = "Stop"
if ($InstallPublishedAndroidApk -and -not $AndroidAppOnly) {
    throw "-InstallPublishedAndroidApk can only be used with -AndroidAppOnly."
}
if (-not [string]::IsNullOrWhiteSpace($AndroidDeviceSerial) -and -not $AndroidAppOnly) {
    throw "-AndroidDeviceSerial can only be used with -AndroidAppOnly."
}
if (-not [string]::IsNullOrWhiteSpace($AndroidDeviceSerial) -and $AndroidDeviceSerial -notmatch '^[A-Za-z0-9._:-]{1,128}$') {
    throw "-AndroidDeviceSerial contains unsupported characters. Copy the exact serial shown by 'adb devices'."
}
if (-not [string]::IsNullOrWhiteSpace($LocalAndroidDeviceSerial) -and ($AndroidAppOnly -or $E2EOnly)) {
    throw "-LocalAndroidDeviceSerial is for the full local QA run; it cannot be combined with -AndroidAppOnly or -E2EOnly."
}
if (-not [string]::IsNullOrWhiteSpace($LocalAndroidDeviceSerial) -and $LocalAndroidDeviceSerial -notmatch '^[A-Za-z0-9._:-]{1,128}$') {
    throw "-LocalAndroidDeviceSerial contains unsupported characters. Copy the exact serial shown by 'adb devices'."
}
if ($AndroidAppOnly -and ($E2EOnly -or $PullLatest -or $ReuseRunningStack -or $TestPublishedApps)) {
    throw "-AndroidAppOnly is a standalone mode. Do not combine it with -E2EOnly, -PullLatest, -ReuseRunningStack or -TestPublishedApps."
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $repoRoot

$pullChangedPaths = @()
$pulledLatest = $false
$requiresAppRefresh = $true
$sourceRevision = $null
if ($PullLatest) {
    if (-not $E2EOnly) { throw "-PullLatest is supported only with -E2EOnly." }
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if (-not $git) { $git = Get-Command git -ErrorAction Stop }
    $activeBranch = (& $git.Source "branch" "--show-current").Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($activeBranch)) {
        throw "-PullLatest requires an attached local branch; detached HEAD is not supported."
    }
    $expectedBranch = $activeBranch
    $beforePull = (& $git.Source "rev-parse" "HEAD").Trim()
    if ($LASTEXITCODE -ne 0 -or -not $beforePull) { throw "Could not resolve the current source revision." }
    & $git.Source "fetch" "--no-tags" "origin" $expectedBranch
    if ($LASTEXITCODE -ne 0) { throw "Git fetch failed. Existing files and containers were left unchanged." }
    $upstreamRef = "refs/remotes/origin/$expectedBranch"
    $upstreamRevision = (& $git.Source "rev-parse" $upstreamRef).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $upstreamRevision) { throw "Could not resolve origin/$expectedBranch after fetch." }
    if ($beforePull -ne $upstreamRevision) {
        # A dirty tree is harmless when GitHub has no newer commit. If an update
        # is actually needed, refuse before merge so tracked and untracked user
        # files remain exactly where they are.
        $workingTree = @(& $git.Source "status" "--porcelain" "--untracked-files=all")
        if ($LASTEXITCODE -ne 0) { throw "Could not verify the Git working tree; no merge was attempted." }
        if ($workingTree.Count -gt 0) {
            throw "GitHub has newer commits, but the working tree contains local changes. No merge was attempted; all local files were preserved."
        }
        $pullChangedPaths = @(& $git.Source "diff" "--name-only" "--diff-filter=ACDMRT" "$beforePull..$upstreamRevision")
        if ($LASTEXITCODE -ne 0) { throw "Could not classify the pulled changes; no merge was attempted." }
        & $git.Source "merge" "--ff-only" $upstreamRef
        if ($LASTEXITCODE -ne 0) { throw "The update is not a fast-forward. No reset, rebase, or force update was attempted." }
    } else {
        # A no-op fetch may preserve local QA/doc edits, but never test stale containers against dirty app source.
        $localTrackedChanges = @(& $git.Source "diff" "--name-only" "HEAD" "--")
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect local tracked changes; no container operation was attempted." }
        $localRuntimeChanges = @($localTrackedChanges | Where-Object {
            $normalisedPath = ([string]$_).Replace([char]92, [char]47)
            $parts = $normalisedPath.Split("/")
            $isAppTest = $parts.Length -ge 3 -and $parts[0] -eq "apps" -and $parts[2] -eq "tests"
            $qaOrDocsOnly = $normalisedPath.StartsWith("scripts/qa/") -or
                $normalisedPath.StartsWith("docs/") -or
                $normalisedPath.StartsWith(".github/") -or
                $normalisedPath.StartsWith("tests/") -or
                $isAppTest -or
                $normalisedPath.EndsWith(".md") -or
                $normalisedPath.EndsWith(".example")
            -not $qaOrDocsOnly
        })
        if ($localRuntimeChanges.Count -gt 0) {
            throw ("GitHub is current, but tracked application/runtime files are modified: " +
                ($localRuntimeChanges -join ", ") +
                ". No merge or container operation was attempted; preserve these edits and reconcile them before QA.")
        }
        Write-Host "Local source is already at the GitHub branch head. QA/docs-only tracked edits and unrelated untracked files are preserved; no merge is needed." -ForegroundColor Green
    }
    $sourceRevision = (& $git.Source "rev-parse" "HEAD").Trim()
    if ($LASTEXITCODE -ne 0 -or -not $sourceRevision) { throw "Could not verify the resulting source revision." }
    $pulledLatest = $true
    $runtimeChanges = @($pullChangedPaths | Where-Object {
        $normalisedPath = ([string]$_).Replace([char]92, [char]47)
        $qaOrDocsOnly = $normalisedPath -match '^(scripts/qa/|docs/|\.github/|tests/|apps/[^/]+/tests/)' -or
            $normalisedPath -match '\.md$'
        -not $qaOrDocsOnly
    })
    $requiresAppRefresh = $runtimeChanges.Count -gt 0
    $decision = if ($requiresAppRefresh) { "application/runtime change: refresh API and Portal" } else { "QA scripts/tests/docs only: reuse running containers" }
    Write-Host ("Pulled source revision: " + $sourceRevision.Substring(0, [Math]::Min(12, $sourceRevision.Length))) -ForegroundColor Green
    Write-Host ("Changed files: " + $pullChangedPaths.Count + " | " + $decision) -ForegroundColor Cyan
    $pullChangedPaths | ForEach-Object { Write-Host ("  " + $_) }
}
if ($ReuseRunningStack -and -not $E2EOnly) { throw "-ReuseRunningStack requires -E2EOnly." }
if ($PullLatest -and $ReuseRunningStack -and $requiresAppRefresh) {
    throw "Application/runtime files changed in the pull. Refusing to test stale containers in reuse-only mode."
}

$qaConfigPath = Join-Path $PSScriptRoot "smartprint-qa.config.json"
if (-not (Test-Path -LiteralPath $qaConfigPath -PathType Leaf)) {
    throw "The central SmartPrint QA configuration is missing: $qaConfigPath"
}
try {
    $qaConfig = Get-Content -LiteralPath $qaConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop
} catch {
    throw "The central SmartPrint QA configuration is invalid: $($_.Exception.Message)"
}
if ($qaConfig.schemaVersion -ne 1) { throw "Unsupported SmartPrint QA configuration schema version." }

try {
    $publicUri = [Uri]$qaConfig.origins.public
    $localUri = [Uri]$qaConfig.origins.local
    $androidEmulatorUri = [Uri]$qaConfig.origins.androidEmulator
    $minimumNodeVersion = [Version]$qaConfig.runtime.minimumNodeVersion
} catch {
    throw "The central SmartPrint QA configuration contains an invalid origin or runtime version: $($_.Exception.Message)"
}

if (-not $publicUri.IsAbsoluteUri -or $publicUri.Scheme -ne "https" -or
    -not [string]::IsNullOrEmpty($publicUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($publicUri.Query) -or
    -not [string]::IsNullOrEmpty($publicUri.Fragment) -or $publicUri.AbsolutePath -ne "/") {
    throw "QA public origin must be a plain HTTPS origin without credentials, path, query or fragment."
}
if (-not $localUri.IsAbsoluteUri -or $localUri.Scheme -ne "http" -or -not $localUri.IsLoopback -or
    -not [string]::IsNullOrEmpty($localUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($localUri.Query) -or
    -not [string]::IsNullOrEmpty($localUri.Fragment) -or $localUri.AbsolutePath -ne "/") {
    throw "QA local origin must be a loopback HTTP origin without credentials, path, query or fragment."
}
if (-not $androidEmulatorUri.IsAbsoluteUri -or $androidEmulatorUri.Scheme -ne "http" -or
    $androidEmulatorUri.Host -ne "10.0.2.2" -or $androidEmulatorUri.AbsolutePath -ne "/" -or
    -not [string]::IsNullOrEmpty($androidEmulatorUri.Query) -or -not [string]::IsNullOrEmpty($androidEmulatorUri.Fragment)) {
    throw "QA Android emulator origin must use the Android emulator host 10.0.2.2."
}

$target = $localUri.GetLeftPart([UriPartial]::Authority)
$canonicalPublicUrl = $publicUri.GetLeftPart([UriPartial]::Authority)
$androidEmulatorOrigin = $androidEmulatorUri.GetLeftPart([UriPartial]::Authority)
$composeProjectName = [string]$qaConfig.composeProjectName
$playwrightVersion = [string]$qaConfig.runtime.playwrightVersion
if ($composeProjectName -notmatch '^[a-z0-9][a-z0-9_-]{1,62}$') { throw "Invalid Compose project name in the QA configuration." }
if ($playwrightVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid Playwright version in the QA configuration." }
if ($minimumNodeVersion -lt [Version]"22.6.0") { throw "The configured Node.js minimum cannot be lower than 22.6.0." }

foreach ($downloadPath in @($qaConfig.downloads.statusPath, $qaConfig.downloads.windowsPath, $qaConfig.downloads.androidPath)) {
    if ([string]$downloadPath -notmatch '^/downloads/[a-z]+$') { throw "Invalid public download path in the QA configuration." }
}
foreach ($assetName in @($qaConfig.downloads.windowsAssetName, $qaConfig.downloads.androidAssetName)) {
    if ([string]$assetName -notmatch '^[A-Za-z0-9._-]+$') { throw "Invalid public download asset name in the QA configuration." }
}
if ([long]$qaConfig.downloads.maxTransferBytes -lt 1 -or [long]$qaConfig.downloads.maxTransferBytes -gt 536870912) {
    throw "Public download QA transfer limit must be between 1 byte and 512 MiB."
}

$windowsAppConfig = $qaConfig.apps.windows
$androidAppConfig = $qaConfig.apps.android
foreach ($value in @($windowsAppConfig.installDirectoryName,$windowsAppConfig.executableName,$windowsAppConfig.agentServiceName,$windowsAppConfig.programDataDirectoryName,$windowsAppConfig.agentIdentityFileName)) {
    if ([string]$value -notmatch '^[A-Za-z0-9][A-Za-z0-9._ -]{0,63}$') { throw "Invalid Windows app identity in central QA configuration." }
}
if ([IO.Path]::GetFileName([string]$windowsAppConfig.executableName) -ne [string]$windowsAppConfig.executableName -or
    -not ([string]$windowsAppConfig.executableName).EndsWith(".exe",[StringComparison]::OrdinalIgnoreCase)) {
    throw "Windows executable name must be a simple .exe filename."
}
if ([int]$windowsAppConfig.smokeTimeoutSeconds -lt 10 -or [int]$windowsAppConfig.smokeTimeoutSeconds -gt 120) {
    throw "Windows smoke timeout must be 10 to 120 seconds."
}
foreach ($idName in @("nativeRoot","homeNavigation","filesNavigation","homeHeading","filesHeading")) {
    $resourceId = [string]$androidAppConfig.uiAutomationIds.$idName
    if ($resourceId -notmatch '^[a-z][a-z0-9_]{2,63}$') {
        throw "Invalid Android UI automation resource ID '$idName' in central QA configuration."
    }
}
if ([string]$androidAppConfig.applicationId -notmatch '^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$') { throw "Invalid Android application ID." }
try { [void][regex]::new([string]$androidAppConfig.emulatorSerialPattern) } catch { throw "Invalid emulator serial pattern." }
foreach ($timeoutName in @("launchTimeoutSeconds","uiNavigationTimeoutSeconds")) {
    $timeoutValue = [int]$androidAppConfig.$timeoutName
    if ($timeoutValue -lt 5 -or $timeoutValue -gt 120) { throw "Android $timeoutName must be between 5 and 120 seconds." }
}
if ([int]$androidAppConfig.crashLogLineCount -lt 50 -or [int]$androidAppConfig.crashLogLineCount -gt 2000) {
    throw "Android crashLogLineCount must be between 50 and 2000."
}
$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runSuffix = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$runId = "local-" + $runStamp + "-" + $runSuffix
$artifactRelative = Join-Path "artifacts" (Join-Path "smartprint-qa" $runId)
$artifactRoot = Join-Path $repoRoot $artifactRelative
$logDirectory = Join-Path $artifactRoot "logs"
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
$script:qaStepNumber = 0
$fatalError = $null
$exitCode = 0

function Add-QAResult {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet("PASS", "FAIL", "SKIPPED", "WARN")][string]$Status,
        [string]$Detail = "",
        [string]$Log = ""
    )
    $results.Add([PSCustomObject]@{
        name = $Name
        status = $Status
        detail = $Detail
        log = $Log
    })
    $colour = switch ($Status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "WARN" { "Yellow" }
        default { "DarkYellow" }
    }
    Write-Host ("{0} - {1}" -f $Status, $Name) -ForegroundColor $colour
    if ($Detail) { Write-Host ("  " + $Detail) }
}

function Invoke-QAStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$LogName,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    $script:qaStepNumber += 1
    $stepTimer = [System.Diagnostics.Stopwatch]::StartNew()
    $stepLabel = "[QA-STEP-START] {0:00} | {1} | {2}" -f $script:qaStepNumber, (Get-Date -Format "HH:mm:ss"), $Name
    Write-Host ([Environment]::NewLine + $stepLabel) -ForegroundColor Cyan
    Write-Host ("[QA-STEP-ACTION] Log: " + $LogName) -ForegroundColor DarkCyan
    $logPath = Join-Path $logDirectory $LogName
    $stepExit = 0
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # PowerShell can surface native stderr (including normal Docker Compose
        # build progress) as NativeCommandError records. With the script-wide
        # Stop preference, that can abort a successful process before its exit
        # code is checked. Keep native stderr non-terminating here; the native
        # process exit code below remains the source of truth for PASS or FAIL.
        $ErrorActionPreference = "Continue"
        $global:LASTEXITCODE = 0
        & $Action 2>&1 | Tee-Object -FilePath $logPath -ErrorAction Stop | ForEach-Object { Write-Host $_ }
        if ($null -ne $LASTEXITCODE) { $stepExit = [int]$LASTEXITCODE }
    } catch {
        $stepExit = 1
        ($_ | Out-String) | Add-Content -LiteralPath $logPath
        Write-Host ($_ | Out-String) -ForegroundColor Red
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $stepTimer.Stop()
    $stepStatus = if ($stepExit -eq 0) { "PASS" } else { "FAIL" }
    Write-Host ("[QA-STEP-END] {0} | {1} | exit={2} | elapsed={3:n1}s" -f (Get-Date -Format "HH:mm:ss"), $stepStatus, $stepExit, $stepTimer.Elapsed.TotalSeconds) -ForegroundColor $(if ($stepStatus -eq "PASS") { "Green" } else { "Red" })
    Add-QAResult -Name $Name -Status $stepStatus -Detail ("Exit code " + $stepExit + "; elapsed " + [Math]::Round($stepTimer.Elapsed.TotalSeconds, 1) + "s") -Log (Join-Path "logs" $LogName)
    return ($stepExit -eq 0)
}

function Invoke-PhotoStudioBrowserAcceptance {
    param([Parameter(Mandatory)]$Node)

    $suite = Join-Path $repoRoot "apps/portal/tests/theme-responsive.browser.mjs"
    $env:SMARTPRINT_E2E_BASE_URL = "http://127.0.0.1:4173"
    $env:SMARTPRINT_DESKTOP_E2E_URL = "http://127.0.0.1:1420"
    $env:RESPONSIVE_ARTIFACT_DIR = Join-Path $artifactRelative "photo-studio-browser"

    $packageManagerOk = Invoke-QAStep -Name "Resolve pinned pnpm and prepare portal/Desktop Vite workspace dependencies" -LogName "photo-studio-workspace-dependencies.log" -Action {
        $manifestPath = Join-Path $repoRoot "package.json"
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "The workspace package.json is missing." }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ([string]$manifest.packageManager -notmatch '^pnpm@(?<version>\d+\.\d+\.\d+)$') {
            throw "The root package.json must pin pnpm to an exact version."
        }
        $requiredPnpmVersion = $Matches.version

        $corepack = Get-Command "corepack" -CommandType Application -ErrorAction SilentlyContinue
        if ($corepack) {
            $script:photoStudioPackageManager = $corepack
            $versionArgs = @("pnpm", "--version")
            $installPrefix = @("pnpm")
            $env:SMARTPRINT_PNPM_COMMAND = "corepack"
            $env:SMARTPRINT_PNPM_PREFIX = "pnpm"
        } else {
            $pnpm = Get-Command "pnpm" -CommandType Application -ErrorAction SilentlyContinue
            if (-not $pnpm) { throw "Neither Corepack nor pnpm is available. Install the workspace-pinned package manager or use the documented local setup." }
            $script:photoStudioPackageManager = $pnpm
            $versionArgs = @("--version")
            $installPrefix = @()
            $env:SMARTPRINT_PNPM_COMMAND = "pnpm"
            $env:SMARTPRINT_PNPM_PREFIX = ""
        }

        $actualPnpmVersion = (& $script:photoStudioPackageManager.Source @versionArgs | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualPnpmVersion -ne $requiredPnpmVersion) {
            throw "The browser visual suite requires pnpm $requiredPnpmVersion; resolved version was '$actualPnpmVersion'."
        }
        Write-Host "Pinned workspace package manager: pnpm $actualPnpmVersion"

        $workspaceManifest = Join-Path $repoRoot "pnpm-workspace.yaml"
        $lockFile = Join-Path $repoRoot "pnpm-lock.yaml"
        if (-not (Test-Path -LiteralPath $workspaceManifest -PathType Leaf) -or -not (Test-Path -LiteralPath $lockFile -PathType Leaf)) {
            throw "The pinned pnpm workspace manifest or lock file is missing."
        }
        $installArgs = @($installPrefix) + @(
            "install", "--frozen-lockfile", "--prefer-offline",
            "--filter", "@smart-print/portal...",
            "--filter", "@smart-print/desktop..."
        )
        & $script:photoStudioPackageManager.Source @installArgs
        if ($LASTEXITCODE -ne 0) { throw "Pinned portal/Desktop workspace dependency preparation failed." }
        Write-Host "Portal and Desktop Vite dependencies are ready from the frozen workspace lock file."
    }
    if (-not $packageManagerOk) { return $false }

    $syntaxOk = Invoke-QAStep -Name "Validate isolated browser visual and Photo Studio acceptance suite syntax" -LogName "photo-studio-browser-syntax.log" -Action {
        if (-not (Test-Path -LiteralPath $suite -PathType Leaf)) {
            throw "The isolated browser visual suite is missing: $suite"
        }
        & $Node.Source "--check" $suite
    }
    if (-not $syntaxOk) { return $false }

    $browserOk = Invoke-QAStep -Name "Local Chromium visual and exhaustive Photo Studio browser acceptance (synthetic fixture; no live API, payments or print dispatch)" -LogName "photo-studio-browser.log" -Action {
        foreach ($port in @(4173, 1420)) {
            $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, [int]$port)
            try {
                $probe.Start()
            } catch {
                throw "Required isolated browser QA port $port is already in use. The existing process was left untouched; close its owner or choose another approved setup before retrying."
            } finally {
                $probe.Stop()
            }
        }
        Write-Host "[QA-SUITE-START] Isolated Chromium visual matrix: responsive routes, light/dark, customer/shop Photo Studio tools, A4 PDF raster geometry, pagination, failure recovery and Windows Desktop WebView viewport." -ForegroundColor Cyan
        Write-Host "[QA-SUITE-SAFETY] Synthetic photo fixture only; API requests are intercepted with 503 responses. No live customer data, payment, refund, print job or physical dispatch." -ForegroundColor Yellow
        & $Node.Source $suite
    }
    return $browserOk
}

function Test-LocalSmartPrint {
    try {
        # Verify that the public portal origin serves the application.
        $portal = Invoke-WebRequest -Uri "$target/" -Method GET -UseBasicParsing -TimeoutSec 8 -ErrorAction Stop
        if ($portal.StatusCode -ne 200) {
            return $false
        }

        # The API readiness endpoint includes database connectivity details.
        # Probe it from inside the API container instead of exposing it through
        # the unauthenticated portal origin or weakening API authorization.
        $apiHealthArgs = $composeBaseArgs + @(
            "exec",
            "-T",
            "api",
            "curl",
            "--fail",
            "--silent",
            "--show-error",
            "http://127.0.0.1:8080/health/ready"
        )
        $apiHealthOutput = & $docker.Source @apiHealthArgs 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $apiHealthOutput) {
            return $false
        }

        $ready = ($apiHealthOutput -join [Environment]::NewLine) |
            ConvertFrom-Json -ErrorAction Stop

        return ($ready.status -eq "ready" -and $ready.database -eq "connected")
    } catch {
        return $false
    }
}

function New-QAPassword {
    $bytes = New-Object byte[] 36
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $suffix = [Convert]::ToBase64String($bytes).Replace("+", "A").Replace("/", "B").TrimEnd("=")
    return "Qe!7" + $suffix.Substring(0, 32)
}

function Protect-QAPassword {
    param([Parameter(Mandatory)][string]$Password)
    $secure = ConvertTo-SecureString -String $Password -AsPlainText -Force
    try { return ConvertFrom-SecureString -SecureString $secure }
    finally { $secure.Dispose() }
}

function Unprotect-QAPassword {
    param([Parameter(Mandatory)][string]$ProtectedPassword)
    $secure = ConvertTo-SecureString -String $ProtectedPassword
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        $secure.Dispose()
    }
}

function New-QAAccountSet {
    $accountId = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    $shop1Password = New-QAPassword
    $shop2Password = New-QAPassword
    $adminPassword = New-QAPassword
    $customerPassword = New-QAPassword
    return [PSCustomObject]@{
        schemaVersion = 1
        accountId = $accountId
        createdUtc = [DateTime]::UtcNow.ToString("o")
        shop1 = [PSCustomObject]@{
            email = "smartprint.qa.shop1.$accountId@example.invalid"
            businessName = "SmartPrint QA Shop One $accountId"
            ownerName = "SmartPrint QA Owner One"
            passwordProtected = (Protect-QAPassword $shop1Password)
        }
        shop2 = [PSCustomObject]@{
            email = "smartprint.qa.shop2.$accountId@example.invalid"
            businessName = "SmartPrint QA Shop Two $accountId"
            ownerName = "SmartPrint QA Owner Two"
            passwordProtected = (Protect-QAPassword $shop2Password)
        }
        admin = [PSCustomObject]@{
            email = "smartprint.qa.admin.$accountId@example.invalid"
            displayName = "SmartPrint Local QA Platform Admin"
            passwordProtected = (Protect-QAPassword $adminPassword)
        }
        customer = [PSCustomObject]@{
            email = "smartprint.qa.customer.$accountId@example.invalid"
            displayName = "SmartPrint Local QA Customer"
            passwordProtected = (Protect-QAPassword $customerPassword)
        }
    }
}

function Get-OrCreateQAAccounts {
    $secretDirectory = Join-Path $repoRoot ".secrets\smartprint-qa"
    $accountFile = Join-Path $secretDirectory "qa-accounts.dpapi.json"
    New-Item -ItemType Directory -Force -Path $secretDirectory | Out-Null

    if (Test-Path -LiteralPath $accountFile -PathType Leaf) {
        try {
            $accounts = Get-Content -LiteralPath $accountFile -Raw | ConvertFrom-Json
            if ($accounts.schemaVersion -ne 1 -or
                -not $accounts.shop1.email -or -not $accounts.shop1.passwordProtected -or
                -not $accounts.shop2.email -or -not $accounts.shop2.passwordProtected -or
                -not $accounts.admin.email -or -not $accounts.admin.passwordProtected -or
                -not $accounts.customer.email -or -not $accounts.customer.passwordProtected) {
                throw "Required QA account fields are missing."
            }
            foreach ($record in @($accounts.shop1, $accounts.shop2, $accounts.admin, $accounts.customer)) {
                $null = Unprotect-QAPassword $record.passwordProtected
            }
            Write-Host "Reusing this Windows user's DPAPI-protected QA identities." -ForegroundColor Yellow
            return $accounts
        } catch {
            throw "The existing QA account file cannot be read by this Windows user. It was not overwritten. File: $accountFile. Details: $($_.Exception.Message)"
        }
    }

    $accounts = New-QAAccountSet
    $json = $accounts | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($accountFile, $json, [Text.UTF8Encoding]::new($false))
    Write-Host "Created four local QA identities. Passwords are DPAPI-protected for the current Windows user and are not printed or committed."
    Write-Host "QA identity file: $accountFile"
    return $accounts
}

function Set-QAProcessEnvironment {
    param([Parameter(Mandatory)]$Accounts)
    $env:SMARTPRINT_QA_BASE_URL = $target
    $env:SMARTPRINT_QA_RUN_ID = $runId
    $env:SMARTPRINT_QA_ARTIFACT_DIR = Join-Path $artifactRelative "local-e2e"
    $env:SMARTPRINT_QA_SHOP1_EMAIL = $Accounts.shop1.email
    $env:SMARTPRINT_QA_SHOP1_PASSWORD = Unprotect-QAPassword $Accounts.shop1.passwordProtected
    $env:SMARTPRINT_QA_SHOP1_NAME = $Accounts.shop1.businessName
    $env:SMARTPRINT_QA_SHOP2_EMAIL = $Accounts.shop2.email
    $env:SMARTPRINT_QA_SHOP2_PASSWORD = Unprotect-QAPassword $Accounts.shop2.passwordProtected
    $env:SMARTPRINT_QA_SHOP2_NAME = $Accounts.shop2.businessName
    $env:SMARTPRINT_QA_ADMIN_EMAIL = $Accounts.admin.email
    $env:SMARTPRINT_QA_ADMIN_PASSWORD = Unprotect-QAPassword $Accounts.admin.passwordProtected
    $env:SMARTPRINT_QA_ADMIN_NAME = $Accounts.admin.displayName
    $env:SMARTPRINT_QA_CUSTOMER_EMAIL = $Accounts.customer.email
    $env:SMARTPRINT_QA_CUSTOMER_PASSWORD = Unprotect-QAPassword $Accounts.customer.passwordProtected
    $env:SMARTPRINT_QA_CUSTOMER_NAME = $Accounts.customer.displayName
}

function Install-OrLocatePlaywright {
    param([switch]$ForceSkipInstall)
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npm) { $npm = Get-Command npm -ErrorAction Stop }

    if (-not $SkipPlaywrightInstall -and -not $ForceSkipInstall) {
        # Capture npm's browser-download output so PowerShell does not return
        # those progress lines as part of this function's module-path result.
        $installOutput = @(& $npm.Source "exec" "--yes" "--package=playwright@$playwrightVersion" "--" "playwright" "install" "chromium" 2>&1)
        $installExitCode = $LASTEXITCODE
        if ($installExitCode -ne 0) {
            $installOutput | ForEach-Object { Write-Host $_ }
            throw "Playwright $playwrightVersion Chromium installation failed with exit code $installExitCode."
        }
    }

    $npmCache = (& $npm.Source "config" "get" "cache").Trim()
    if ($LASTEXITCODE -ne 0 -or -not $npmCache) { throw "Could not determine the npm cache directory." }
    $npxRoot = Join-Path $npmCache "_npx"
    if (-not (Test-Path -LiteralPath $npxRoot -PathType Container)) {
        throw "The npm temporary package cache was not found: $npxRoot"
    }

    $module = $null
    foreach ($candidate in (Get-ChildItem -LiteralPath $npxRoot -Directory | Sort-Object LastWriteTime -Descending)) {
        $possible = Join-Path $candidate.FullName "node_modules\playwright"
        $packageJson = Join-Path $possible "package.json"
        if (-not (Test-Path -LiteralPath $packageJson -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $possible "index.mjs") -PathType Leaf)) {
            continue
        }
        $packageInfo = Get-Content -LiteralPath $packageJson -Raw | ConvertFrom-Json
        if ($packageInfo.version -eq $playwrightVersion) {
            $module = $possible
            break
        }
    }
    if (-not $module) { throw "Pinned Playwright $playwrightVersion was not found under $npxRoot." }
    return $module
}

$environmentNames = @(
    "PLAYWRIGHT_MODULE_DIR", "SMARTPRINT_LOCAL_ORIGIN", "SMARTPRINT_QA_BASE_URL", "SMARTPRINT_QA_RUN_ID", "SMARTPRINT_QA_ARTIFACT_DIR",
    "SMARTPRINT_QA_SHOP1_EMAIL", "SMARTPRINT_QA_SHOP1_PASSWORD", "SMARTPRINT_QA_SHOP1_NAME",
    "SMARTPRINT_QA_SHOP2_EMAIL", "SMARTPRINT_QA_SHOP2_PASSWORD", "SMARTPRINT_QA_SHOP2_NAME",
    "SMARTPRINT_QA_ADMIN_EMAIL", "SMARTPRINT_QA_ADMIN_PASSWORD", "SMARTPRINT_QA_ADMIN_NAME",
    "SMARTPRINT_QA_CUSTOMER_EMAIL", "SMARTPRINT_QA_CUSTOMER_PASSWORD", "SMARTPRINT_QA_CUSTOMER_NAME",
    "QRTP_PLATFORM_ADMIN_EMAIL", "QRTP_PLATFORM_ADMIN_NAME", "QRTP_PLATFORM_ADMIN_PASSWORD",
    "SMARTPRINT_TEST_POSTGRES", "SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION", "SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION",
    "SMARTPRINT_E2E_BASE_URL", "SMARTPRINT_DESKTOP_E2E_URL", "RESPONSIVE_ARTIFACT_DIR", "SMARTPRINT_PNPM_COMMAND", "SMARTPRINT_PNPM_PREFIX"
)
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}
$env:SMARTPRINT_LOCAL_ORIGIN = $target

if ($AndroidAppOnly) {
    Write-Host "SMARTPRINT ANDROID-ONLY RELEASE ACCEPTANCE TEST" -ForegroundColor Green
} else {
    Write-Host "SMARTPRINT LOCAL REAL-DATA ACCEPTANCE TEST" -ForegroundColor Green
}
$gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
if (-not $gitCommand) { $gitCommand = Get-Command git -ErrorAction SilentlyContinue }
if ($gitCommand) {
    $sourceRevision = (& $gitCommand.Source rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $sourceRevision) {
        Write-Host ("Local source revision: " + $sourceRevision.Trim())
    } else {
        Write-Host "Local source revision: unavailable (not a Git checkout)" -ForegroundColor Yellow
    }
}
if ($AndroidAppOnly) {
    Write-Host "APK source: $canonicalPublicUrl (release metadata and APK download only)"
    Write-Host "Local website UI/API E2E, Windows QA, Docker and PostgreSQL suites: NOT RUN"
} else {
    Write-Host "Only browser/API target: $target"
    Write-Host "QR and public links use: $canonicalPublicUrl"
    Write-Host "Playwright uses the local Docker API and local PostgreSQL. No production database is used."
    Write-Host "No mocked API routes, synthetic JWTs, real customer data or real payment verification."
}
Write-Host "Evidence directory: $artifactRoot"

try {
    if ($AndroidAppOnly) {
        if ($env:OS -ne "Windows_NT") { throw "Android-only published-app QA is designed for Windows PowerShell." }
        $publishedAppScript = Join-Path $PSScriptRoot "test-published-apps.ps1"
        if (-not (Test-Path -LiteralPath $publishedAppScript -PathType Leaf)) {
            Add-QAResult -Name "Android-only published app QA" -Status "FAIL" -Detail "Missing helper: $publishedAppScript"
            $exitCode = 1
        } else {
            $androidOnlyOk = Invoke-QAStep -Name "Download and acceptance-test the website-published Android APK only" -LogName "android-published-app.log" -Action {
                & $publishedAppScript -AndroidOnly -PublicOrigin $canonicalPublicUrl -OutputDirectory (Join-Path $artifactRoot "android-published-app") -ConfigPath $qaConfigPath -AndroidDeviceSerial $AndroidDeviceSerial -InstallPublishedAndroidApk:$InstallPublishedAndroidApk
            }
            if (-not $androidOnlyOk) { $exitCode = 1 }
        }
    } else {
        if ($env:OS -ne "Windows_NT") { throw "This runner is designed for Windows PowerShell on the local shop/deployment machine." }
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) { $node = Get-Command node -ErrorAction Stop }
    $nodeVersion = (& $node.Source --version).Trim()
    $nodeVersionMatch = [regex]::Match($nodeVersion, '^v(?<version>\d+\.\d+\.\d+)')
    $nodeActualVersion = if ($nodeVersionMatch.Success) { [Version]$nodeVersionMatch.Groups["version"].Value } else { [Version]"0.0.0" }
    if (-not $nodeVersionMatch.Success -or $nodeActualVersion -lt $minimumNodeVersion) {
        throw "Node.js $minimumNodeVersion or newer is required for the built-in TypeScript strip-types test runner. Found $nodeVersion."
    }
    Add-QAResult -Name "Node.js runtime" -Status "PASS" -Detail $nodeVersion

    if ($E2EOnly) {
        $reuseExistingStack = $ReuseRunningStack -or ($PullLatest -and -not $requiresAppRefresh)
        if ($reuseExistingStack) {
            Write-Host "E2E-only reuse mode: no Docker build, container restart, or dependency recreation. Running PostgreSQL, API, Portal and Agent are reused." -ForegroundColor Green
        } else {
            Write-Host "E2E-only refresh mode: only API and Portal images/containers may be rebuilt/refreshed. PostgreSQL, its data volume, QA identities and Agent are preserved." -ForegroundColor Yellow
        }

        $docker = Get-Command docker.exe -ErrorAction SilentlyContinue
        if (-not $docker) { $docker = Get-Command docker -ErrorAction Stop }
        & $docker.Source compose version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Docker Compose v2 is required for the targeted API/Portal refresh." }
        & $docker.Source info | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Docker Desktop engine is not running." }

        $envPath = Join-Path $repoRoot ".env.local"
        $composeFile = Join-Path $repoRoot "compose.local.yml"
        if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) { throw "E2E-only mode requires the existing .env.local file." }
        if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) { throw "E2E-only mode requires compose.local.yml." }

            $composeBaseArgs = @("compose", "--project-name", $composeProjectName, "--env-file", $envPath, "-f", $composeFile)
        $composeConfigArgs = $composeBaseArgs + @("config", "--quiet")
        $configOk = Invoke-QAStep -Name "Validate existing local Compose configuration" -LogName "compose-config.log" -Action {
            & $docker.Source @composeConfigArgs
        }
        if (-not $configOk) { throw "Existing local Compose configuration is invalid." }

        & $docker.Source network inspect "cloudflare-edge" *> $null
        if ($LASTEXITCODE -ne 0) { throw "The existing shared cloudflare-edge Docker network is unavailable. E2E-only mode will not create or replace it." }

        $dbListArgs = $composeBaseArgs + @("ps", "-q", "db")
        $dbContainerIdBefore = (& $docker.Source @dbListArgs | Out-String).Trim()
        if (-not $dbContainerIdBefore) { throw "The existing smart-print PostgreSQL container was not found. E2E-only mode will not create a database." }
        $dbStateBefore = (& $docker.Source inspect --format "{{.State.Status}}" $dbContainerIdBefore).Trim()
        if ($LASTEXITCODE -ne 0 -or $dbStateBefore -ne "running") {
            throw "The existing PostgreSQL container is not running; no attempt will be made to recreate it."
        }

        foreach ($service in @("api", "portal")) {
            $serviceListArgs = $composeBaseArgs + @("ps", "-q", $service)
            $containerId = (& $docker.Source @serviceListArgs | Out-String).Trim()
            if (-not $containerId) { throw "The existing $service container was not found. E2E-only mode will not create missing stack services." }
            $serviceState = (& $docker.Source inspect --format "{{.State.Status}}" $containerId).Trim()
            if ($LASTEXITCODE -ne 0 -or $serviceState -ne "running") {
                throw "The existing $service container is not running. Reuse mode will not start or replace it."
            }
        }

        if (-not $reuseExistingStack) {
        $buildArgs = $composeBaseArgs + @("build", "api", "portal")
        $buildOk = Invoke-QAStep -Name "Build only SmartPrint API and Portal images" -LogName "targeted-app-image-build.log" -Action {
            & $docker.Source @buildArgs
        }
        if (-not $buildOk) { throw "The targeted API/Portal image build failed. Existing containers and PostgreSQL were not replaced." }

        $refreshArgs = $composeBaseArgs + @("up", "-d", "--no-deps", "api", "portal")
        $refreshOk = Invoke-QAStep -Name "Refresh only API and Portal containers (no dependencies, no orphan removal)" -LogName "targeted-app-refresh.log" -Action {
            & $docker.Source @refreshArgs
        }
        if (-not $refreshOk) { throw "API/Portal refresh failed. PostgreSQL was excluded from the Compose up operation." }

        $dbContainerIdAfter = (& $docker.Source @dbListArgs | Out-String).Trim()
        if ($dbContainerIdAfter -ne $dbContainerIdBefore) {
            throw "PostgreSQL container identity changed during the targeted refresh. Stop QA and inspect the existing database before proceeding."
        }
        Add-QAResult -Name "PostgreSQL container preserved during API/Portal refresh" -Status "PASS" -Detail "The existing database container ID is unchanged."
        } else {
            $dbContainerIdAfter = (& $docker.Source @dbListArgs | Out-String).Trim()
            if ($dbContainerIdAfter -ne $dbContainerIdBefore) {
                throw "PostgreSQL container identity changed during reuse preflight. Stop QA and inspect the existing database before proceeding."
            }
            $changedSummary = if ($pullChangedPaths.Count -eq 0) { "No new files were pulled." } else { "Only QA scripts, tests, or documentation changed." }
            Add-QAResult -Name "Reused existing API/Portal containers without building or restarting" -Status "PASS" -Detail $changedSummary
        }

        $runtimeReady = $false
        if ($reuseExistingStack) {
            $runtimeReady = Test-LocalSmartPrint
            if (-not $runtimeReady) {
                throw "The existing Portal/API stack is not healthy or cannot reach PostgreSQL. Reuse mode will not restart or repair containers automatically."
            }
            Add-QAResult -Name "Existing local Portal/API and PostgreSQL readiness" -Status "PASS" -Detail "Reused running services; HTTP 200 and database connected."
        } else {
            for ($attempt = 1; $attempt -le 45; $attempt++) {
                try {
                    $portalResponse = Invoke-WebRequest -Uri "$target/" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
                    $liveResponse = Invoke-WebRequest -Uri "$target/health/live" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
                    $readyResponse = Invoke-WebRequest -Uri "$target/health/ready" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
                    $ready = $readyResponse.Content | ConvertFrom-Json -ErrorAction Stop
                    if ($portalResponse.StatusCode -eq 200 -and $liveResponse.StatusCode -eq 200 -and
                        $readyResponse.StatusCode -eq 200 -and $ready.database -eq "connected") {
                        $runtimeReady = $true
                        break
                    }
                } catch {
                    Start-Sleep -Seconds 2
                }
            }
            if (-not $runtimeReady) { throw "The refreshed API/Portal did not become healthy with PostgreSQL connected. Review targeted-app-refresh.log and runtime logs." }
            Add-QAResult -Name "Refreshed local Portal/API and existing PostgreSQL readiness" -Status "PASS" -Detail "HTTP 200; database connected."
        }

        $accountsFile = Join-Path $repoRoot ".secrets\smartprint-qa\qa-accounts.dpapi.json"
        if (-not (Test-Path -LiteralPath $accountsFile -PathType Leaf)) {
            throw "E2E-only mode requires the existing DPAPI QA identity file: $accountsFile. It will not create or bootstrap accounts."
        }
        $accounts = Get-OrCreateQAAccounts
        Set-QAProcessEnvironment $accounts

        $script:playwrightModule = $null
        $playwrightOk = Invoke-QAStep -Name "Locate pinned Playwright $playwrightVersion for E2E-only run" -LogName "playwright-install.log" -Action {
            $script:playwrightModule = Install-OrLocatePlaywright -ForceSkipInstall:$reuseExistingStack
            if (-not $script:playwrightModule) { throw "Playwright module resolution failed." }
            Write-Host ("Pinned Playwright module: " + $script:playwrightModule)
        }
        if (-not $playwrightOk -or -not $script:playwrightModule) { throw "Playwright is not ready." }
        $env:PLAYWRIGHT_MODULE_DIR = $script:playwrightModule

        $liveSuite = Join-Path $PSScriptRoot "smartprint-local-e2e.mjs"
        if (-not (Test-Path -LiteralPath $liveSuite -PathType Leaf)) { throw "Local E2E suite is missing: $liveSuite" }
        $syntaxOk = Invoke-QAStep -Name "Validate local Playwright suite JavaScript syntax" -LogName "playwright-syntax-check.log" -Action {
            & $node.Source "--check" $liveSuite
        }
        if (-not $syntaxOk) { throw "The local Playwright suite has a syntax error. Review logs/playwright-syntax-check.log." }

        $qaHarnessPreflightOk = Invoke-QAStep -Name "Validate QA harness modules and run existing prerequisite/isolation/HTTP audit tests" -LogName "qa-harness-preflight-tests.log" -Action {
            $qaModuleFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.mjs" -File)
            if ($qaModuleFiles.Count -eq 0) { throw "No QA JavaScript modules were found for syntax validation." }
            foreach ($qaModule in $qaModuleFiles) {
                & $node.Source "--check" $qaModule.FullName
                if ($LASTEXITCODE -ne 0) { throw "QA module syntax validation failed: $($qaModule.Name)" }
            }

            $qaUnitTests = @(
                (Join-Path $PSScriptRoot "live-node-test-reporter.test.mjs"),
                (Join-Path $PSScriptRoot "production-qa-plan.test.mjs"),
                (Join-Path $PSScriptRoot "smartprint-qa-config.test.mjs"),
                (Join-Path $PSScriptRoot "local-e2e-http-audit.test.mjs"),
                (Join-Path $PSScriptRoot "local-e2e-prerequisites.test.mjs")
            )
            $missingQaUnitTests = @($qaUnitTests | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
            if ($missingQaUnitTests.Count -gt 0) {
                throw "QA harness regression tests are missing: $($missingQaUnitTests -join ', ')"
            }
            $qaTestArgs = @("--test", "--test-concurrency=1", "--test-reporter=./scripts/qa/live-node-test-reporter.mjs") + $qaUnitTests
            & $node.Source @qaTestArgs
            if ($LASTEXITCODE -ne 0) { throw "QA harness regression tests failed." }
        }
        if (-not $qaHarnessPreflightOk) {
            throw "QA harness preflight failed; the live browser suite was not started."
        }

        $e2eOk = Invoke-QAStep -Name "Real local Playwright E2E using the existing stack and QA identities" -LogName "local-real-data-e2e.log" -Action {
            & $node.Source $liveSuite
        }
        if (-not $e2eOk) { $exitCode = 1 }

        $photoStudioBrowserOk = Invoke-PhotoStudioBrowserAcceptance -Node $node
        if (-not $photoStudioBrowserOk) { $exitCode = 1 }

        Add-QAResult -Name "Portal/API/Agent/Android build and unit suites" -Status "SKIPPED" -Detail "E2E-only mode deliberately skips builds, platform unit tests, and device tests."
    } else {
    $docker = Get-Command docker.exe -ErrorAction SilentlyContinue
    if (-not $docker) { $docker = Get-Command docker -ErrorAction Stop }
    & $docker.Source compose version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose v2 is required." }
    & $docker.Source info | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Docker Desktop engine is not running." }
    Add-QAResult -Name "Docker Desktop and Compose" -Status "PASS"

    $stackScript = Join-Path $repoRoot "scripts\docker-up-local.ps1"
    $composeFile = Join-Path $repoRoot "compose.local.yml"
    $envPath = Join-Path $repoRoot ".env.local"
    if (-not (Test-Path -LiteralPath $composeFile -PathType Leaf)) {
        throw "Local Docker Compose file is missing: $composeFile"
    }
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
        if (-not (Test-Path -LiteralPath $stackScript -PathType Leaf)) {
            throw "First-time local stack bootstrap is missing: $stackScript"
        }
        $stackOk = Invoke-QAStep -Name "First-time local SmartPrint setup and secret creation" -LogName "docker-up-local-bootstrap.log" -Action {
            & $stackScript -PublicBaseUrl $canonicalPublicUrl
        }
        if (-not $stackOk) {
            throw "First-time local setup failed. Review logs/docker-up-local-bootstrap.log. Existing secrets and volumes were not deleted."
        }
    }
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
        throw "The local .env.local file was not created. Check scripts/docker-up-local.ps1 output."
    }

    $localEnvironmentLines = @(Get-Content -LiteralPath $envPath)
    $publicBaseLine = $localEnvironmentLines | Where-Object { $_ -match '^\s*PUBLIC_BASE_URL\s*=' } | Select-Object -First 1
    if ($null -eq $publicBaseLine -or $publicBaseLine -notmatch '^\s*PUBLIC_BASE_URL\s*=\s*https://[^\s]+\s*$') {
        Write-Host "The local environment is missing its canonical SmartPrint QR URL. Updating only PUBLIC_BASE_URL; all existing secrets and volumes are preserved." -ForegroundColor Yellow
        $baseUrlFixOk = Invoke-QAStep -Name "Set canonical SmartPrint QR origin in local .env.local" -LogName "local-public-base-url.log" -Action {
            & $stackScript -PublicBaseUrl $canonicalPublicUrl
        }
        if (-not $baseUrlFixOk) {
            throw "Could not set PUBLIC_BASE_URL in .env.local. Existing signing/database/document secrets were not regenerated."
        }
        $localEnvironmentLines = @(Get-Content -LiteralPath $envPath)
        $publicBaseLine = $localEnvironmentLines | Where-Object { $_ -match '^\s*PUBLIC_BASE_URL\s*=\s*https://[^\s]+\s*$' } | Select-Object -First 1
        if ($null -eq $publicBaseLine) { throw "PUBLIC_BASE_URL is still missing after local environment repair." }
    }

    & $docker.Source "network" "inspect" "cloudflare-edge" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "The shared Docker network 'cloudflare-edge' is missing. Start it from QuantaEdge-Production with .\scripts\local-ingress.ps1 up. The QA runner will not create or replace shared ingress infrastructure."
    }

    # Pin this stack to its own namespace even when the calling shell or
    # .env.local defines COMPOSE_PROJECT_NAME for QuantaEdge infrastructure.
    $composeBaseArgs = @("compose", "--project-name", $composeProjectName, "--env-file", $envPath, "-f", $composeFile)
    $configArgs = $composeBaseArgs + @("config", "--quiet")
    $configOk = Invoke-QAStep -Name "Validate local SmartPrint Compose configuration (project: $composeProjectName)" -LogName "compose-config.log" -Action {
        & $docker.Source @configArgs
    }
    if (-not $configOk) { throw "Local SmartPrint Compose configuration is invalid." }

    # Refuse to take a localhost port from another Compose project. In
    # particular, never stop or replace a QuantaEdge site container to make
    # room for SmartPrint.
    $portPreflightOk = Invoke-QAStep -Name "Check localhost port 3000 is not owned by another Docker project" -LogName "docker-port-preflight.log" -Action {
        # Avoid nested quotes inside Docker's Go template: Windows PowerShell can
        # strip them before the Docker CLI parses the template. Read IDs, names,
        # and ports first, then retrieve Compose labels as JSON only for port
        # 3000 candidates.
        $runningContainers = @(& $docker.Source "ps" "--format" '{{.ID}}|{{.Names}}|{{.Ports}}')
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect running Docker containers." }
        $conflicts = @()
        foreach ($containerLine in $runningContainers) {
            $parts = $containerLine -split '\|', 3
            if ($parts.Count -lt 3) { continue }
            $containerId = $parts[0].Trim()
            $containerName = $parts[1].Trim()
            $publishedPorts = $parts[2]
            if ($publishedPorts -notmatch '(^|,)\s*(?:\[[^\]]+\]:|(?:\d{1,3}\.){3}\d{1,3}:|:::)?3000->') { continue }

            $labelsJson = & $docker.Source "inspect" "--format" '{{json .Config.Labels}}' $containerId
            if ($LASTEXITCODE -ne 0) {
                throw "Could not inspect Docker Compose ownership for container '$containerName'."
            }
            try {
                $containerLabels = $labelsJson | ConvertFrom-Json -ErrorAction Stop
            }
            catch {
                throw "Could not parse Docker labels for container '$containerName': $($_.Exception.Message)"
            }
            $projectProperty = if ($null -ne $containerLabels) { $containerLabels.PSObject.Properties["com.docker.compose.project"] } else { $null }
            $containerProject = if ($null -ne $projectProperty) { [string]$projectProperty.Value } else { "" }
            if ($containerProject -eq $composeProjectName) { continue }

            $conflicts += "$containerName (project: $containerProject) : $publishedPorts"
        }
        if ($conflicts.Count -gt 0) {
            Write-Host "Conflicting containers (left running; no automatic stop was attempted):" -ForegroundColor Yellow
            $conflicts | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Yellow }
            throw "SmartPrint needs localhost port 3000 for the portal. The API remains private behind Nginx on the Compose network. Resolve the listed port ownership safely before running QA again."
        }
        Write-Host "Host port 3000 has no conflicting Docker container owner; API port 8080 is private to the Compose network."
    }
    if (-not $portPreflightOk) { throw "Docker port preflight failed; no existing containers were stopped." }

    $expectedImages = @{
        api = "localhost/smart-print/smart-print-api:local"
        portal = "localhost/smart-print/smart-print-portal:local"
    }
    $imageBuildOk = Invoke-QAStep -Name "Build and verify the exact SmartPrint local Docker images" -LogName "docker-image-build.log" -Action {
        $rootManifestPath = Join-Path $repoRoot "package.json"
        $portalManifestPath = Join-Path $repoRoot "apps/portal/package.json"
        $desktopManifestPath = Join-Path $repoRoot "apps/desktop/package.json"
        $portalDockerfilePath = Join-Path $repoRoot "apps/portal/Dockerfile"
        $responsiveBrowserTestPath = Join-Path $repoRoot "apps/portal/tests/theme-responsive.browser.mjs"
        $apiProgramPath = Join-Path $repoRoot "src/SmartPrint.Api/Program.cs"
        $localComposePath = Join-Path $repoRoot "compose.local.yml"
        $publicLandingCssPath = Join-Path $repoRoot "apps/portal/src/public-landing.css"
        foreach ($requiredPath in @($rootManifestPath, $portalManifestPath, $desktopManifestPath, $portalDockerfilePath, $responsiveBrowserTestPath, $apiProgramPath, $localComposePath, $publicLandingCssPath)) {
            if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
                throw "Required SmartPrint workspace identity file is missing: $requiredPath"
            }
        }
        $rootManifest = Get-Content -LiteralPath $rootManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $portalManifest = Get-Content -LiteralPath $portalManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $desktopManifest = Get-Content -LiteralPath $desktopManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $portalDockerfile = Get-Content -LiteralPath $portalDockerfilePath -Raw
        $responsiveBrowserTest = Get-Content -LiteralPath $responsiveBrowserTestPath -Raw
        $apiProgram = Get-Content -LiteralPath $apiProgramPath -Raw
        $localCompose = Get-Content -LiteralPath $localComposePath -Raw
        $publicLandingCss = Get-Content -LiteralPath $publicLandingCssPath -Raw
        $healthRouteStart = $apiProgram.IndexOf('app.MapGet("/health/live"')
        $healthRouteAnonymousMarker = $apiProgram.IndexOf('})).AllowAnonymous();', $healthRouteStart)
        $healthRouteIsAnonymous = $healthRouteStart -ge 0 -and $healthRouteAnonymousMarker -ge $healthRouteStart
        $expectedCorsOriginLine = 'Cors__AllowedOrigins__2: "${SMARTPRINT_LOCAL_ORIGIN:-' + $target + '}"'
        $loopbackCorsIsAllowed = $localCompose.Contains($expectedCorsOriginLine)
        $portalUsesOfflineFonts = -not $publicLandingCss.Contains("fonts.googleapis.com") -and
            -not $publicLandingCss.Contains("fonts.gstatic.com")
        if (-not $healthRouteIsAnonymous -or -not $loopbackCorsIsAllowed -or -not $portalUsesOfflineFonts) {
            throw "This checkout is missing the local health/CORS/offline-font fixes or its CORS fallback does not match the central QA origin. No E2E browser test was started."
        }
        Write-Host ("Verified source preflight: anonymous API liveness, " + $target + " CORS and no external Google Fonts.")
        if ($rootManifest.name -ne "smart-print" -or
            $portalManifest.name -ne "@smart-print/portal" -or
            $desktopManifest.name -ne "@smart-print/desktop" -or
            $portalDockerfile -notmatch 'pnpm --filter @smart-print/portal build' -or
            $responsiveBrowserTest -notmatch '"--filter",\s*"@smart-print/portal"' -or
            $responsiveBrowserTest -notmatch '"--filter",\s*"@smart-print/desktop"') {
            throw "The checkout still contains an old workspace/package identity. Expected smart-print, @smart-print/portal, @smart-print/desktop, and matching Docker/browser QA package filters. Pull the current main branch before building."
        }
        Write-Host "Verified workspace identity: smart-print / @smart-print/portal / @smart-print/desktop."
        $resolvedConfigArgs = $composeBaseArgs + @("config", "--format", "json")
        $resolvedConfigOutput = & $docker.Source @resolvedConfigArgs
        if ($LASTEXITCODE -ne 0) { throw "Could not resolve the SmartPrint Compose image configuration." }
        $resolvedConfig = ($resolvedConfigOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop

        foreach ($serviceName in @("api", "portal")) {
            $configuredImage = [string]$resolvedConfig.services.$serviceName.image
            if ($configuredImage -ne $expectedImages[$serviceName]) {
                throw "Compose image mismatch for '$serviceName'. Expected '$($expectedImages[$serviceName])', found '$configuredImage'."
            }
            Write-Host ("Confirmed {0} image target: {1}" -f $serviceName, $configuredImage)
        }

        $buildArgs = $composeBaseArgs + @("build", "--progress", "plain", "api", "portal")
        & $docker.Source @buildArgs
        if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed to build the API and Portal images." }

        foreach ($serviceName in @("api", "portal")) {
            $expectedImage = $expectedImages[$serviceName]
            $tagOutput = & $docker.Source "image" "inspect" "--format" '{{json .RepoTags}}' $expectedImage
            if ($LASTEXITCODE -ne 0) { throw "The expected image was not created: $expectedImage" }
            $repoTags = ($tagOutput -join "") | ConvertFrom-Json -ErrorAction Stop
            if (@($repoTags) -notcontains $expectedImage) {
                throw "Docker image inspection did not report the required tag '$expectedImage'."
            }
            Write-Host ("Verified built image: {0}" -f $expectedImage) -ForegroundColor Green
        }
    }
    if (-not $imageBuildOk) { throw "SmartPrint local image build or image-tag verification failed. Review logs/docker-image-build.log." }

    $stackOk = Invoke-QAStep -Name "Start verified SmartPrint images and PostgreSQL without disturbing its data volume (project: $composeProjectName)" -LogName "docker-compose-up.log" -Action {
        # Bring PostgreSQL up normally first. Do not force-recreate this stateful
        # service: its existing container and named data volume must be retained.
        $dbUpArgs = $composeBaseArgs + @("up", "-d", "--no-build", "db")
        & $docker.Source @dbUpArgs
        if ($LASTEXITCODE -ne 0) { throw "Local PostgreSQL failed to start." }

        # Compose does not notice that a locally tagged image was rebuilt when
        # the service configuration hash is unchanged. Force replacement of only
        # the stateless app containers, with --no-deps so PostgreSQL is untouched.
        $appUpArgs = $composeBaseArgs + @("up", "-d", "--no-build", "--force-recreate", "--no-deps", "api", "portal")
        & $docker.Source @appUpArgs
        if ($LASTEXITCODE -ne 0) { throw "Local SmartPrint API/portal containers failed to start from the rebuilt images." }
    }
    if (-not $stackOk) { throw "Local SmartPrint Docker stack failed to start. Review logs/docker-compose-up.log." }

    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    $ready = $false
    do {
        if (Test-LocalSmartPrint) { $ready = $true; break }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $ready) {
        $logsArgs = $composeBaseArgs + @("logs", "--tail", "100", "portal", "api", "db")
        & $docker.Source @logsArgs 2>&1 | Tee-Object -FilePath (Join-Path $logDirectory "docker-readiness-failure.log") | ForEach-Object { Write-Host $_ }
        throw "Local portal or API/PostgreSQL readiness did not become healthy at $target after rebuild. Review logs/docker-readiness-failure.log."
    }
    Add-QAResult -Name "Local SmartPrint stack readiness" -Status "PASS" -Detail "Current source rebuilt; portal returns 200 and the private API readiness check confirms database connected."

    $runtimePreflightOk = Invoke-QAStep -Name "Verify rebuilt API liveness, portal proxy, local CORS and offline font assets" -LogName "runtime-preflight.log" -Action {
        foreach ($serviceName in @("api", "portal")) {
            $containerIdArgs = $composeBaseArgs + @("ps", "-q", $serviceName)
            $containerId = (& $docker.Source @containerIdArgs).Trim()
            if ($LASTEXITCODE -ne 0 -or -not $containerId) { throw "The rebuilt '$serviceName' container is not running." }
            $runningImageId = (& $docker.Source "inspect" "--format" "{{.Image}}" $containerId).Trim()
            if ($LASTEXITCODE -ne 0 -or -not $runningImageId) { throw "Could not inspect the running '$serviceName' container image." }
            $expectedImage = $expectedImages[$serviceName]
            $builtImageId = (& $docker.Source "image" "inspect" "--format" "{{.Id}}" $expectedImage).Trim()
            if ($LASTEXITCODE -ne 0 -or $runningImageId -ne $builtImageId) {
                throw "The running '$serviceName' container is not using the image just built from this checkout. Refusing to start E2E."
            }
            Write-Host ("Verified running image: {0} ({1})" -f $serviceName, $runningImageId)
        }

        $apiLiveArgs = $composeBaseArgs + @("exec", "-T", "api", "curl", "--fail", "--silent", "--show-error", "http://127.0.0.1:8080/health/live")
        $apiLiveOutput = & $docker.Source @apiLiveArgs
        if ($LASTEXITCODE -ne 0 -or -not $apiLiveOutput) { throw "Direct API /health/live is not anonymously reachable inside the API container." }
        $apiLive = ($apiLiveOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
        if ($apiLive.status -ne "live") { throw "Direct API liveness returned an unexpected payload." }

        $proxyLiveArgs = $composeBaseArgs + @(
            "exec", "-T", "api", "curl", "--noproxy", "*", "--silent", "--show-error", "--include",
            "http://portal/health/live"
        )
        $proxyLiveOutput = @(& $docker.Source @proxyLiveArgs)
        $proxyLiveExit = $LASTEXITCODE
        $proxyLiveText = $proxyLiveOutput -join [Environment]::NewLine
        if ($proxyLiveExit -ne 0 -or -not $proxyLiveText) {
            throw "The portal /health/live reverse proxy request failed. curl exit: $proxyLiveExit. Response: $proxyLiveText"
        }
        $proxyLiveStatusMatch = [regex]::Match($proxyLiveText, '(?m)^HTTP/\S+\s+(?<status>\d{3})')
        if (-not $proxyLiveStatusMatch.Success) {
            throw "The portal /health/live response did not contain an HTTP status line. Response: $proxyLiveText"
        }
        $proxyLiveStatus = [int]$proxyLiveStatusMatch.Groups["status"].Value
        $proxyLiveSeparator = [regex]::Match($proxyLiveText, '\r?\n\r?\n')
        if (-not $proxyLiveSeparator.Success) {
            throw "The portal /health/live response did not contain an HTTP header/body separator. Response: $proxyLiveText"
        }
        $proxyLiveBody = $proxyLiveText.Substring($proxyLiveSeparator.Index + $proxyLiveSeparator.Length).Trim()
        if ($proxyLiveStatus -ne 200) {
            throw "The portal /health/live reverse proxy returned HTTP $proxyLiveStatus. Response body: $proxyLiveBody"
        }
        $proxyLive = $proxyLiveBody | ConvertFrom-Json -ErrorAction Stop
        if ($proxyLive.status -ne "live") { throw "Portal-proxied API liveness returned an unexpected payload: $proxyLiveBody" }

        $corsArgs = $composeBaseArgs + @(
            "exec", "-T", "api", "curl", "--noproxy", "*", "--silent", "--show-error",
            "--dump-header", "-", "--output", "/dev/null", "--request", "OPTIONS",
            "--header", "Origin: $target",
            "--header", "Access-Control-Request-Method: POST",
            "--header", "Access-Control-Request-Headers: content-type,authorization,idempotency-key,x-print-batch-id",
            "http://portal/api/v1/auth/register-customer"
        )
        $corsOutput = & $docker.Source @corsArgs
        if ($LASTEXITCODE -ne 0) { throw "Could not execute local CORS preflight through the portal proxy." }
        $corsHeaders = $corsOutput -join [Environment]::NewLine
        $preflightStatusIs204 = $corsHeaders -match 'HTTP/.+ 204'
        $allowedOriginHeaderFound = $corsHeaders.IndexOf(
            "Access-Control-Allow-Origin: $target",
            [StringComparison]::OrdinalIgnoreCase
        ) -ge 0
        if (-not $preflightStatusIs204 -or -not $allowedOriginHeaderFound) {
            throw "Local origin CORS preflight failed. Response headers: $corsHeaders"
        }

        $fontArgs = $composeBaseArgs + @(
            "exec", "-T", "portal", "sh", "-lc",
            "if grep -R -n -E 'fonts.googleapis.com|fonts.gstatic.com' /usr/share/nginx/html; then exit 1; else exit 0; fi"
        )
        $fontOutput = & $docker.Source @fontArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Compiled portal image still references external Google Fonts. No browser E2E was started."
        }
        Write-Host "Verified direct and proxied API liveness, local CORS preflight, running image IDs and offline fonts."
    }
    if (-not $runtimePreflightOk) {
        throw "Local runtime preflight failed. The long Playwright E2E suite was intentionally not started. Review logs/runtime-preflight.log."
    }

    $accounts = Get-OrCreateQAAccounts
    Set-QAProcessEnvironment $accounts
    $env:QRTP_PLATFORM_ADMIN_EMAIL = $accounts.admin.email
    $env:QRTP_PLATFORM_ADMIN_NAME = $accounts.admin.displayName
    $env:QRTP_PLATFORM_ADMIN_PASSWORD = Unprotect-QAPassword $accounts.admin.passwordProtected

    $dockerArgs = @(
        "compose", "--project-name", $composeProjectName, "--env-file", $envPath, "-f", $composeFile, "exec", "-T",
        "-e", "QRTP_PLATFORM_ADMIN_EMAIL",
        "-e", "QRTP_PLATFORM_ADMIN_NAME",
        "-e", "QRTP_PLATFORM_ADMIN_PASSWORD",
        "api", "dotnet", "SmartPrint.Api.dll", "--bootstrap-platform-admin"
    )
    $bootstrapOk = Invoke-QAStep -Name "Bootstrap local platform QA administrator without elevating shop accounts" -LogName "bootstrap-local-admin.log" -Action {
        & $docker.Source @dockerArgs
    }
    if (-not $bootstrapOk) { throw "Local platform administrator bootstrap failed." }

    $script:playwrightModule = $null
    $playwrightOk = Invoke-QAStep -Name "Install and locate pinned Playwright $playwrightVersion with Chromium" -LogName "playwright-install.log" -Action {
        $script:playwrightModule = Install-OrLocatePlaywright
        if (-not $script:playwrightModule) { throw "Playwright module resolution failed." }
        Write-Host ("Pinned Playwright module: " + $script:playwrightModule)
    }
    if (-not $playwrightOk -or -not $script:playwrightModule) { throw "Playwright is not ready." }
    $env:PLAYWRIGHT_MODULE_DIR = $script:playwrightModule

    $liveSuite = Join-Path $PSScriptRoot "smartprint-local-e2e.mjs"
    if (-not (Test-Path -LiteralPath $liveSuite -PathType Leaf)) { throw "Local E2E suite is missing: $liveSuite" }
    $syntaxOk = Invoke-QAStep -Name "Validate local Playwright suite JavaScript syntax" -LogName "playwright-syntax-check.log" -Action {
        & $node.Source "--check" $liveSuite
    }
    if (-not $syntaxOk) { throw "The local Playwright suite has a syntax error. Review logs/playwright-syntax-check.log." }

    $e2eOk = Invoke-QAStep -Name "Real local Playwright E2E: all QA roles, UI navigation, database, documents, batch review, UPI rejection and tenant isolation" -LogName "local-real-data-e2e.log" -Action {
        & $node.Source $liveSuite
    }
    if (-not $e2eOk) { $exitCode = 1 }

    $photoStudioBrowserOk = Invoke-PhotoStudioBrowserAcceptance -Node $node
    if (-not $photoStudioBrowserOk) { $exitCode = 1 }

    if ($SkipUnitTests) {
        Add-QAResult -Name "Existing portal/API/Agent logic suites" -Status "SKIPPED" -Detail "Skipped by -SkipUnitTests."
    } else {
        $portalTests = @(
            "apps/portal/tests/portal-routing.test.ts",
            "apps/portal/tests/order-journey.test.ts",
            "apps/portal/tests/customer-session-state.test.ts",
            "apps/portal/tests/table-layout.test.ts",
            "apps/portal/tests/theme-responsive.test.ts",
            "apps/portal/tests/batch-checkout-state.test.ts",
            "apps/portal/tests/local-upload-routing.test.ts",
            "apps/portal/tests/print-fulfilment-flow.test.ts",
            "apps/portal/tests/photo-studio.test.ts",
            "apps/portal/tests/upi-payment-intent.test.ts"
        )
        $missingPortalTests = @($portalTests | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repoRoot $_) -PathType Leaf) })
        if ($missingPortalTests.Count -eq 0) {
            $portalOk = Invoke-QAStep -Name "Existing portal logic regression tests (separate from live API E2E)" -LogName "portal-logic-tests.log" -Action {
                & $node.Source "--experimental-strip-types" "--test" "--test-concurrency=1" "--test-reporter=./scripts/qa/live-node-test-reporter.mjs" @portalTests
            }
            if (-not $portalOk) { $exitCode = 1 }
        } else {
            Add-QAResult -Name "Existing portal logic regression tests" -Status "FAIL" -Detail ("Missing files: " + ($missingPortalTests -join ", "))
            $exitCode = 1
        }

        $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if (-not $dotnet) { $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue }
        if ($dotnet) {
            $env:SMARTPRINT_TEST_POSTGRES = $null
            $apiTestOk = Invoke-QAStep -Name "Existing SmartPrint API unit/regression tests (destructive migration DB test disabled)" -LogName "api-dotnet-tests.log" -Action {
                Write-Host "[QA-SUITE-START] API .NET tests | xUnit verbose reporter announces each case start/end; StopOnFail=false." -ForegroundColor Cyan
                & $dotnet.Source "test" "tests/SmartPrint.Api.Tests/SmartPrint.Api.Tests.csproj" "--configuration" "Release" "--logger" "console;verbosity=normal" "--" "xUnit.ReporterSwitch=verbose" "xUnit.ShowLiveOutput=true" "xUnit.StopOnFail=false" "xUnit.ParallelizeTestCollections=false" "xUnit.MaxParallelThreads=1"
            }
            if (-not $apiTestOk) { $exitCode = 1 }

            $pdfPrinterAvailable = $false
            if (Get-Command Get-Printer -ErrorAction SilentlyContinue) {
                $pdfPrinter = Get-Printer -Name "Microsoft Print to PDF" -ErrorAction SilentlyContinue
                $pdfPrinterAvailable = $null -ne $pdfPrinter
            }
            if ($pdfPrinterAvailable) {
                $env:SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION = "1"
                Write-Host "Microsoft Print to PDF is installed. Enabling its real virtual-spooler test; no physical printer is used." -ForegroundColor Cyan
            } else {
                $env:SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION = $null
                Add-QAResult -Name "Microsoft Print to PDF Windows spooler integration" -Status "SKIPPED" -Detail "Microsoft Print to PDF queue or PrintManagement cmdlet is unavailable."
            }
            $env:SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION = $null
            $agentTestOk = Invoke-QAStep -Name "Existing SmartPrint Windows Agent unit/regression tests and optional virtual spooler integration" -LogName "agent-dotnet-tests.log" -Action {
                Write-Host "[QA-SUITE-START] Windows Agent .NET tests | xUnit verbose reporter announces each case start/end; StopOnFail=false." -ForegroundColor Cyan
                & $dotnet.Source "test" "tests/SmartPrint.Agent.Tests/SmartPrint.Agent.Tests.csproj" "--configuration" "Release" "--logger" "console;verbosity=normal" "--" "xUnit.ReporterSwitch=verbose" "xUnit.ShowLiveOutput=true" "xUnit.StopOnFail=false" "xUnit.ParallelizeTestCollections=false" "xUnit.MaxParallelThreads=1"
            }
            if (-not $agentTestOk) { $exitCode = 1 }
            if ($pdfPrinterAvailable -and $agentTestOk) {
                Add-QAResult -Name "Microsoft Print to PDF Windows spooler integration" -Status "PASS" -Detail "Real GDI job reached Windows virtual printer and generated a verified PDF in a temporary directory."
            }
            $env:SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION = $null
        } else {
            Add-QAResult -Name "Existing API and Windows Agent .NET unit suites" -Status "SKIPPED" -Detail ".NET 10 SDK was not found on this Windows machine."
        }

        $gradle = Get-Command gradle.bat -ErrorAction SilentlyContinue
        if (-not $gradle) { $gradle = Get-Command gradle -ErrorAction SilentlyContinue }
        $sdkRoot = $env:ANDROID_SDK_ROOT
        if ([string]::IsNullOrWhiteSpace($sdkRoot)) { $sdkRoot = $env:ANDROID_HOME }
        if ([string]::IsNullOrWhiteSpace($sdkRoot) -and $env:LOCALAPPDATA) {
            $defaultSdk = Join-Path $env:LOCALAPPDATA "Android\Sdk"
            if (Test-Path -LiteralPath $defaultSdk -PathType Container) { $sdkRoot = $defaultSdk }
        }

        if (-not $gradle -or [string]::IsNullOrWhiteSpace($sdkRoot) -or -not (Test-Path -LiteralPath $sdkRoot -PathType Container)) {
            Add-QAResult -Name "Android local JVM and emulator suites" -Status "SKIPPED" -Detail "System Gradle and Android SDK are required; the script will not install to physical phones."
        } else {
            $androidUnitTestArgs = @(
                "--no-daemon", "--console=plain", "-p", "apps/android",
                "-PsmartPrintServiceUrl=$androidEmulatorOrigin",
                ":app:testDebugUnitTest"
            )
            $androidUnitTestsOk = Invoke-QAStep -Name "Android native JVM unit tests against the local portal configuration" -LogName "android-unit-tests.log" -Action {
                & $gradle.Source @androidUnitTestArgs
            }
            if (-not $androidUnitTestsOk) { $exitCode = 1 }

            $androidBuildArgs = @(
                "--no-daemon", "--console=plain", "-p", "apps/android",
                "-PsmartPrintServiceUrl=$androidEmulatorOrigin",
                ":app:assembleDebug"
            )
            $androidApkBuildOk = Invoke-QAStep -Name "Build Android debug APK for emulator acceptance" -LogName "android-apk-build.log" -Action {
                & $gradle.Source @androidBuildArgs
            }
            if (-not $androidApkBuildOk) { $exitCode = 1 }

            $adb = Get-Command adb.exe -ErrorAction SilentlyContinue
            if (-not $adb) { $adb = Get-Command adb -ErrorAction SilentlyContinue }
            if (-not $adb) {
                $platformToolsAdb = Join-Path $sdkRoot "platform-tools\adb.exe"
                if (Test-Path -LiteralPath $platformToolsAdb -PathType Leaf) {
                    $adb = [PSCustomObject]@{ Source = $platformToolsAdb }
                }
            }

            if ($adb) {
                $adbOutput = @(& $adb.Source "devices")
                $connectedDevices = @($adbOutput | Where-Object { $_ -match '^\S+\s+device(\s|$)' })
                $emulatorDevices = @($connectedDevices | Where-Object { $_ -match '^emulator-\d+\s+device(\s|$)' })
            } else {
                $connectedDevices = @()
                $emulatorDevices = @()
            }

            if ($androidApkBuildOk -and $emulatorDevices.Count -eq 1 -and $connectedDevices.Count -eq 1) {
                $instrumentationArgs = @(
                    "--no-daemon", "--console=plain", "-p", "apps/android",
                    "-PsmartPrintServiceUrl=$androidEmulatorOrigin",
                    "--info", ":app:connectedDebugAndroidTest"
                )
                $androidUiOk = Invoke-QAStep -Name "Android emulator instrumentation tests against local SmartPrint portal" -LogName "android-emulator-tests.log" -Action {
                    & $gradle.Source @instrumentationArgs
                }
                if (-not $androidUiOk) { $exitCode = 1 }
            } elseif ($androidApkBuildOk) {
                $deviceReason = if ($connectedDevices.Count -eq 0) {
                    "No Android emulator is online."
                } elseif ($connectedDevices.Count -gt 1) {
                    "More than one ADB device is online. Disconnect phones/extra emulators so tests cannot install to an unintended device."
                } else {
                    "The only connected Android device is not an emulator. Physical phones are deliberately not targeted."
                }
                Add-QAResult -Name "Android emulator instrumentation suite" -Status "SKIPPED" -Detail $deviceReason
            }
        }
    }
    }

    if (-not $E2EOnly) {
        $nativeAppsScript = Join-Path $PSScriptRoot "run-smartprint-native-local.ps1"
        if (-not (Test-Path -LiteralPath $nativeAppsScript -PathType Leaf)) {
            Add-QAResult -Name "Local native Windows Desktop and Android QA" -Status "FAIL" -Detail "Missing local native QA runner: $nativeAppsScript"
            $exitCode = 1
        } else {
            $nativeOutputDirectory = Join-Path $artifactRoot "native-local"
            $nativeArgs = @(
                "-PublicOrigin", $canonicalPublicUrl,
                "-LocalPortalOrigin", $target,
                "-OutputDirectory", $nativeOutputDirectory
            )
            if (-not [string]::IsNullOrWhiteSpace($LocalAndroidDeviceSerial)) {
                $nativeArgs += @("-AndroidDeviceSerial", $LocalAndroidDeviceSerial)
            }
            $nativeAppsOk = Invoke-QAStep -Name "Local Windows Desktop native tests and Android side-by-side device acceptance (no public release downloads)" -LogName "native-local-apps.log" -Action {
                & $nativeAppsScript @nativeArgs
            }
            if (-not $nativeAppsOk) { $exitCode = 1 }
        }
    } else {
        Add-QAResult -Name "Local native Windows Desktop and Android QA" -Status "SKIPPED" -Detail "E2E-only mode deliberately skips native application builds and device acceptance."
    }

    if ($TestPublishedApps) {
        $publishedAppScript = Join-Path $PSScriptRoot "test-published-apps.ps1"
        if (-not (Test-Path -LiteralPath $publishedAppScript -PathType Leaf)) {
            Add-QAResult -Name "Published Windows/Android optional QA" -Status "FAIL" -Detail "Missing helper: $publishedAppScript"
            $exitCode = 1
        } else {
            $publishedAppsOk = Invoke-QAStep -Name "Official website Windows installer and Android APK acceptance" -LogName "published-apps.log" -Action {
                & $publishedAppScript -PublicOrigin $canonicalPublicUrl -OutputDirectory (Join-Path $artifactRoot "published-apps") -ConfigPath $qaConfigPath
            }
            if (-not $publishedAppsOk) { $exitCode = 1 }
        }
    } else {
        Write-Host "Optional published Windows/Android app QA was not requested. Pass -TestPublishedApps to enable it." -ForegroundColor DarkCyan
    }
    }
}
catch {
    $fatalError = $_.Exception.Message
    $exitCode = 1
    Add-QAResult -Name "QA orchestrator" -Status "FAIL" -Detail $fatalError
    ($_ | Out-String) | Set-Content -LiteralPath (Join-Path $logDirectory "fatal-error.log")
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], "Process")
    }

    $summary = [PSCustomObject]@{
        product = "SmartPrint"
        sourceRevision = if ($sourceRevision) { $sourceRevision } else { $null }
        pulledLatestFromGitHub = $pulledLatest
        changedFilesFromPull = @($pullChangedPaths)
        applicationRefreshRequiredByPull = if ($pulledLatest) { $requiresAppRefresh } else { $null }
        reusedExistingAppContainers = if ($E2EOnly) { $reuseExistingStack } else { $null }
        publishedAppsTestRequested = [bool]($TestPublishedApps -or $AndroidAppOnly)
        androidAppOnly = [bool]$AndroidAppOnly
        androidDeviceSerial = if (-not [string]::IsNullOrWhiteSpace($AndroidDeviceSerial)) { $AndroidDeviceSerial } else { $null }
        localAndroidDeviceSerial = if (-not [string]::IsNullOrWhiteSpace($LocalAndroidDeviceSerial)) { $LocalAndroidDeviceSerial } else { $null }
        localNativeAppsTestIncluded = (-not [bool]$AndroidAppOnly -and -not [bool]$E2EOnly)
        webE2eExecuted = -not [bool]$AndroidAppOnly
        environment = if ($AndroidAppOnly) { "PUBLISHED_ANDROID_APP_ONLY" } else { "LOCAL_DOCKER_POSTGRES_REAL_API" }
        target = if ($AndroidAppOnly) { $canonicalPublicUrl } else { $target }
        runId = $runId
        startedLocal = $runStamp
        completedUtc = [DateTime]::UtcNow.ToString("o")
        result = if ($exitCode -eq 0) { "PASS" } else { "FAIL" }
        passed = @($results | Where-Object status -eq "PASS").Count
        failed = @($results | Where-Object status -eq "FAIL").Count
        skipped = @($results | Where-Object status -eq "SKIPPED").Count
        warnings = @($results | Where-Object status -eq "WARN").Count
        steps = @($results)
        fatalError = $fatalError
        evidenceDirectory = $artifactRelative
        qaCredentialsFile = ".secrets\smartprint-qa\qa-accounts.dpapi.json"
        mockedApiRoutesUsed = $false
        productionDatabaseTouched = $false
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactRoot "summary.json") -Encoding UTF8
    $summary | Format-List | Out-String | Set-Content -LiteralPath (Join-Path $artifactRoot "summary.txt") -Encoding UTF8
    Write-Host ([Environment]::NewLine + "=== SMARTPRINT LOCAL QA SUMMARY ===") -ForegroundColor Cyan
    $summary | Format-Table result, passed, failed, skipped, warnings, target, evidenceDirectory -AutoSize
    Write-Host "QA identities are stored encrypted for this Windows user. No password was written to the report or Git."
    Write-Host "Evidence: $artifactRoot" -ForegroundColor Cyan
}

if ($exitCode -ne 0) { exit 1 }
