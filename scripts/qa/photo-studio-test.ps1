<#
.SYNOPSIS
Runs SmartPrint Photo Studio software-only tests across Web, Windows and Android.

.DESCRIPTION
The default run executes the portal unit suite and real Chromium acceptance flow,
Windows API/Print Agent tests without printer hardware, and Android JVM tests plus a
debug APK build. The Android emulator WebView suite is enabled with -RunAndroidEmulator.

No test in this script submits a real payment or sends a job to a physical printer.
The optional -UseVirtualPdfPrinter switch opts into the Windows Microsoft Print to PDF
virtual spooler integration only.

.PARAMETER Platform
All, Web, Windows, or Android. Defaults to All.

.PARAMETER SkipInstall
Skips pnpm dependency installation. Use only when the lockfile dependencies are already installed.

.PARAMETER SkipBuild
Skips portal and native desktop builds. Unit tests and the Android APK build still run.

.PARAMETER Headed
Runs Chromium with a visible browser window for local debugging.

.PARAMETER RunAndroidEmulator
Starts the local Photo Studio fixture server on port 3000 and runs connected Android
instrumentation tests against the emulator's real Android WebView. Requires an online emulator.

.PARAMETER UseVirtualPdfPrinter
Opts into the Microsoft Print to PDF Windows virtual-printer tests. It does not use a physical printer.

.PARAMETER ServiceUrl
Canonical HTTPS origin embedded in the Android debug build. A harmless example.invalid
default is used for offline test builds; no live service is contacted by this test suite.

.PARAMETER ArtifactDirectory
Directory where browser evidence and test outputs are written.

.EXAMPLE
.\scripts\qa\photo-studio-test.ps1 -Help

.EXAMPLE
.\scripts\qa\photo-studio-test.ps1

.EXAMPLE
.\scripts\qa\photo-studio-test.ps1 -Platform Web -Headed

.EXAMPLE
.\scripts\qa\photo-studio-test.ps1 -Platform Android -RunAndroidEmulator

.EXAMPLE
.\scripts\qa\photo-studio-test.ps1 -Platform Windows -UseVirtualPdfPrinter
#>
[CmdletBinding()]
param(
    [switch]$Help,
    [ValidateSet("All", "Web", "Windows", "Android")]
    [string]$Platform = "All",
    [switch]$SkipInstall,
    [switch]$SkipBuild,
    [switch]$Headed,
    [switch]$RunAndroidEmulator,
    [switch]$UseVirtualPdfPrinter,
    [string]$ServiceUrl = "https://example.invalid",
    [string]$ArtifactDirectory = "artifacts/photo-studio-local"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Show-Usage {
    @"
SmartPrint Photo Studio software-only test runner

Usage:
  .\scripts\qa\photo-studio-test.ps1 [options]

Options:
  -Help                    Show this help.
  -Platform All|Web|Windows|Android
                           Select test platforms. Default: All.
  -SkipInstall             Skip pnpm install --frozen-lockfile.
  -SkipBuild               Skip portal and Windows desktop builds.
  -Headed                  Show Chromium while running browser acceptance.
  -RunAndroidEmulator      Start local fixture server and execute Android WebView instrumentation.
  -UseVirtualPdfPrinter    Opt in to Microsoft Print to PDF virtual-queue tests only.
  -ServiceUrl <https-url>  HTTPS origin embedded in the Android build. Default: https://example.invalid.
  -ArtifactDirectory <dir> Store reports, screenshots and logs. Default: artifacts/photo-studio-local.

Examples:
  .\scripts\qa\photo-studio-test.ps1
  .\scripts\qa\photo-studio-test.ps1 -Platform Web -Headed
  .\scripts\qa\photo-studio-test.ps1 -Platform Android -RunAndroidEmulator
  .\scripts\qa\photo-studio-test.ps1 -Platform Windows -UseVirtualPdfPrinter

The default suites do not connect to a physical printer or submit real payments.
The optional Microsoft Print to PDF integration uses a Windows virtual printer only.
"@
}

if ($Help) {
    Show-Usage
    exit 0
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Push-Location $repoRoot
$serverProcess = $null
try {
    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null

    function Invoke-Checked {
        param(
            [Parameter(Mandatory = $true)][string]$Label,
            [Parameter(Mandatory = $true)][scriptblock]$Action
        )
        Write-Host ""
        Write-Host "=== $Label ===" -ForegroundColor Cyan
        & $Action
        if ($LASTEXITCODE -ne 0) {
            throw "$Label failed with exit code $LASTEXITCODE."
        }
    }

    function Start-PhotoStudioFixtureServer {
        param([string]$LogDirectory)
        $stdout = Join-Path $LogDirectory "photo-studio-vite.stdout.log"
        $stderr = Join-Path $LogDirectory "photo-studio-vite.stderr.log"
        $process = Start-Process -FilePath "pnpm.cmd" -ArgumentList "--filter @smart-print/portal dev --host 0.0.0.0 --port 3000 --strictPort" -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            if ($process.HasExited) {
                Get-Content $stdout, $stderr -ErrorAction SilentlyContinue
                throw "The Photo Studio Vite fixture server exited with code $($process.ExitCode)."
            }
            try {
                $response = Invoke-WebRequest -Uri "http://127.0.0.1:3000/tests/photo-studio.browser.html" -TimeoutSec 2
                if ($response.StatusCode -eq 200) { return $process }
            } catch {
                Start-Sleep -Milliseconds 500
            }
        }
        Get-Content $stdout, $stderr -ErrorAction SilentlyContinue
        throw "The Photo Studio fixture server did not become ready on port 3000."
    }

    function Invoke-PortalSuite {
        if (-not $SkipInstall) {
            Invoke-Checked "Install locked portal dependencies" { pnpm install --frozen-lockfile }
        }

        $testFiles = @(
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
        foreach ($testFile in $testFiles) {
            if (-not (Test-Path -LiteralPath $testFile)) { throw "Required portal regression test is missing: $testFile" }
        }

        Invoke-Checked "Run all portal unit and regression tests" {
            node --experimental-strip-types --test @testFiles
        }

        Invoke-Checked "Install Playwright Chromium" {
            npx --yes --package=playwright@1.56.1 playwright install chromium
        }

        $npmCache = (npm config get cache).Trim()
        $module = Get-ChildItem -Path (Join-Path $npmCache "_npx") -Filter "index.mjs" -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\node_modules\\playwright\\index\.mjs$" } |
            Select-Object -First 1
        if (-not $module) { throw "Playwright was installed but its index.mjs module could not be located." }

        $env:PLAYWRIGHT_MODULE_DIR = $module.DirectoryName
        $env:SMARTPRINT_PHOTO_STUDIO_ARTIFACT_DIR = Join-Path $ArtifactDirectory "browser"
        $env:VITE_API_BASE_URL = ""
        Remove-Item Env:SMARTPRINT_PHOTO_STUDIO_BASE_URL -ErrorAction SilentlyContinue
        $env:SMARTPRINT_PHOTO_STUDIO_HEADED = if ($Headed) { "1" } else { "0" }
        New-Item -ItemType Directory -Force -Path $env:SMARTPRINT_PHOTO_STUDIO_ARTIFACT_DIR | Out-Null
        Invoke-Checked "Run Photo Studio browser acceptance (customer + shop owner + responsive)" {
            node apps/portal/tests/photo-studio.browser.mjs
        }

        if (-not $SkipBuild) {
            Invoke-Checked "Portal TypeScript validation" { pnpm typecheck }
            Invoke-Checked "Portal production build" { pnpm build }
        }
    }

    function Invoke-WindowsSuite {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw ".NET SDK is required for the Windows API and Agent suites."
        }
        if ($Platform -eq "Windows" -and -not $SkipInstall) {
            Invoke-Checked "Install locked desktop dependencies" { pnpm install --frozen-lockfile }
        }
        if (-not $SkipBuild) {
            $env:VITE_API_BASE_URL = $ServiceUrl
            Invoke-Checked "Windows desktop frontend typecheck" { pnpm --filter @smart-print/desktop typecheck }
            Invoke-Checked "Windows desktop frontend build" { pnpm --filter @smart-print/desktop build }
            $nativeManifest = Join-Path $repoRoot "apps/desktop/src-tauri/Cargo.toml"
            if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
                throw "Rust/Cargo is required for the Windows native desktop validation."
            }
            Invoke-Checked "Windows native desktop Rust checks" {
                cargo check --locked --manifest-path $nativeManifest
            }
            Invoke-Checked "Windows native desktop Rust tests" {
                cargo test --locked --manifest-path $nativeManifest
            }
            Invoke-Checked "Windows native desktop executable build" {
                pnpm --filter @smart-print/desktop exec tauri build --no-bundle --ci
            }
        }
        Invoke-Checked "Windows cloud API unit and model tests" {
            dotnet test tests/SmartPrint.Api.Tests/SmartPrint.Api.Tests.csproj --configuration Release
        }

        if (-not $UseVirtualPdfPrinter) {
            Remove-Item Env:SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION -ErrorAction SilentlyContinue
            Remove-Item Env:SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION -ErrorAction SilentlyContinue
        } else {
            $virtualPrinter = Get-Printer -Name "Microsoft Print to PDF" -ErrorAction SilentlyContinue
            if (-not $virtualPrinter) {
                throw "Microsoft Print to PDF is not installed. Enable the Windows optional feature before selecting -UseVirtualPdfPrinter."
            }
            $env:SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION = "1"
            $env:SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION = "1"
        }

        Invoke-Checked "Windows Print Agent unit and policy tests" {
            dotnet test tests/SmartPrint.Agent.Tests/SmartPrint.Agent.Tests.csproj --configuration Release
        }
    }

    function Invoke-AndroidSuite {
        if ($RunAndroidEmulator -and -not $SkipInstall -and $Platform -eq "Android") {
            Invoke-Checked "Install portal dependencies for Android WebView fixture" { pnpm install --frozen-lockfile }
        }
        $gradle = Join-Path $repoRoot "apps/android/gradlew.bat"
        if (-not (Test-Path $gradle)) { throw "Android Gradle wrapper was not found at $gradle." }

        Invoke-Checked "Android JVM unit tests" {
            & $gradle --no-daemon -p apps/android "-PsmartPrintServiceUrl=$ServiceUrl" :app:testDebugUnitTest
        }
        Invoke-Checked "Build Android debug APK" {
            & $gradle --no-daemon -p apps/android "-PsmartPrintServiceUrl=$ServiceUrl" :app:assembleDebug
        }

        if ($RunAndroidEmulator) {
            if (-not (Get-Command adb -ErrorAction SilentlyContinue)) {
                throw "Android SDK platform-tools (adb) are required for -RunAndroidEmulator."
            }
            $deviceOutput = & adb devices
            if ($LASTEXITCODE -ne 0 -or -not ($deviceOutput | Select-String "emulator-\d+\s+device")) {
                throw "No online Android emulator was detected. Start an emulator before using -RunAndroidEmulator."
            }
            $script:serverProcess = Start-PhotoStudioFixtureServer -LogDirectory $ArtifactDirectory
            Invoke-Checked "Android emulator Photo Studio WebView instrumentation" {
                & $gradle --no-daemon -p apps/android "-PsmartPrintServiceUrl=$ServiceUrl" :app:connectedDebugAndroidTest
            }
        }
    }

    if ($RunAndroidEmulator -and $Platform -notin @("All", "Android")) {
        throw "-RunAndroidEmulator can only be combined with -Platform All or -Platform Android."
    }
    if ($UseVirtualPdfPrinter -and $Platform -notin @("All", "Windows")) {
        throw "-UseVirtualPdfPrinter can only be combined with -Platform All or -Platform Windows."
    }

    Write-Host "SmartPrint Photo Studio software-only QA" -ForegroundColor Green
    Write-Host "Repository: $repoRoot"
    Write-Host "Platform: $Platform"
    Write-Host "Physical printer access: DISABLED"
    Write-Host "Live payment submission: DISABLED"
    Write-Host "Evidence directory: $ArtifactDirectory"

    if ($Platform -in @("All", "Web", "Windows")) { Invoke-PortalSuite }
    if ($Platform -in @("All", "Windows")) { Invoke-WindowsSuite }
    if ($Platform -in @("All", "Android")) { Invoke-AndroidSuite }

    Write-Host ""
    Write-Host "All selected Photo Studio software-only test commands completed successfully." -ForegroundColor Green
}
catch {
    Write-Error $_
    exit 1
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        $serverProcess.Kill($true)
        $serverProcess.WaitForExit(5000) | Out-Null
    }
    Pop-Location
}
