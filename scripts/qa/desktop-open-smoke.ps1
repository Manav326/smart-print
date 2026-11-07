[CmdletBinding()]
param(
    [string]$ExecutablePath = "apps/desktop/src-tauri/target/release/smartprint-desktop.exe",
    [ValidateRange(10, 120)][int]$StartupTimeoutSeconds = 45,
    [switch]$RequireDevTools
)

$ErrorActionPreference = "Stop"

$resolvedExecutable = Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop
$executable = $resolvedExecutable.Path
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "SmartPrint Desktop executable does not exist: $executable"
}

function Find-WebView2RuntimeExecutable {
    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\EdgeWebView\Application"),
        (Join-Path ${env:ProgramFiles} "Microsoft\EdgeWebView\Application"),
        (Join-Path ${env:LOCALAPPDATA} "Microsoft\EdgeWebView\Application")
    )
    $candidates = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            try { $version = [version]$directory.Name }
            catch { continue }
            $runtimePath = Join-Path $directory.FullName "msedgewebview2.exe"
            if (Test-Path -LiteralPath $runtimePath -PathType Leaf) {
                $candidates += [PSCustomObject]@{ Version = $version; Path = $runtimePath }
            }
        }
    }
    return ($candidates | Sort-Object Version -Descending | Select-Object -First 1)
}

function Get-DesktopAccessibilitySnapshot([IntPtr]$WindowHandle) {
    Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop

    $window = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
    if (-not $window) {
        throw "Windows UI Automation could not attach to the SmartPrint native window."
    }

    $elements = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition
    )
    $names = [System.Collections.Generic.List[string]]::new()
    $controlTypes = [System.Collections.Generic.List[string]]::new()
    $limit = [Math]::Min($elements.Count, 1200)

    for ($index = 0; $index -lt $limit; $index++) {
        try {
            $current = $elements.Item($index).Current
            if ($current.IsOffscreen) { continue }

            $name = ([string]$current.Name).Trim()
            if (-not [string]::IsNullOrWhiteSpace($name) -and -not $names.Contains($name)) {
                [void]$names.Add($name)
            }

            $controlType = $current.ControlType
            if ($controlType) {
                $typeName = [string]$controlType.ProgrammaticName
                if (-not [string]::IsNullOrWhiteSpace($typeName) -and -not $controlTypes.Contains($typeName)) {
                    [void]$controlTypes.Add($typeName)
                }
            }
        }
        catch {
            # Individual WebView2 accessibility nodes can disappear during layout.
            # Ignore that node and continue inspecting the stable window tree.
        }
    }

    return [PSCustomObject]@{
        visibleNamedElementCount = $names.Count
        visibleControlTypeCount = $controlTypes.Count
        names = @($names)
        controlTypes = @($controlTypes)
    }
}

$webViewRuntime = Find-WebView2RuntimeExecutable
if (-not $webViewRuntime) {
    throw "Microsoft Edge WebView2 Runtime is not installed for this Windows user. Install the Evergreen Runtime before running the desktop smoke test."
}
$webViewRuntimeVersion = (Get-Item -LiteralPath $webViewRuntime.Path).VersionInfo.ProductVersion
Write-Host "[QA-PREFLIGHT] WebView2 Runtime $webViewRuntimeVersion ($($webViewRuntime.Path))" -ForegroundColor Cyan

# Read the DevTools port from the shared QA configuration. It is used only
# by the dedicated CI diagnostic build. Published production apps are tested
# through Windows UI Automation and must not expose a remote-debugging port.
$qaConfigPath = Join-Path $PSScriptRoot "smartprint-qa.config.json"
$qaConfig = Get-Content -LiteralPath $qaConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop
$debugPort = [int]$qaConfig.apps.windows.webViewDevToolsPort
if ($RequireDevTools) {
    if ($debugPort -lt 1024 -or $debugPort -gt 65535) {
        throw "The configured WebView2 DevTools port is outside the valid non-privileged TCP range."
    }
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $debugPort)
    try {
        $listener.Start()
    } catch {
        throw "Configured WebView2 DevTools port $debugPort is already in use; the CI smoke test cannot safely probe this process."
    }
    $listener.Stop()
}

# CI uses a private, disposable WebView2 profile so its diagnostic endpoint
# cannot attach to or alter a user's shared browser profile. Production QA
# deliberately keeps the normal profile to verify real persisted user state.
$dataDirectoryEnvironmentName = "WEBVIEW2_USER_DATA_FOLDER"
$previousWebViewDataDirectory = [Environment]::GetEnvironmentVariable($dataDirectoryEnvironmentName, "Process")
$testWebViewDataDirectory = $null
if ($RequireDevTools) {
    $testWebViewDataDirectory = Join-Path ([IO.Path]::GetTempPath()) ("smartprint-desktop-smoke-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $testWebViewDataDirectory | Out-Null
}

$process = $null
$webSocket = $null
$desktopSmokeEvidence = $null

try {
    if ($RequireDevTools) {
        Write-Host "[QA-MODE] CI diagnostic build: private DevTools port $debugPort and isolated temporary profile." -ForegroundColor DarkCyan
        [Environment]::SetEnvironmentVariable($dataDirectoryEnvironmentName, $testWebViewDataDirectory, "Process")
    }
    else {
        Write-Host "[QA-MODE] Production acceptance: Windows UI Automation; normal WebView2 profile and signed-in session are preserved." -ForegroundColor DarkCyan
    }

    Write-Host "[QA-ACTION] Launch SmartPrint Desktop: $executable" -ForegroundColor Cyan
    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $pageTarget = $null
    $mainWindowReady = $false
    $observedTargets = @()

    $windowPoll = 0
    do {
        $windowPoll++
        $process.Refresh()
        if ($process.HasExited) {
            throw "SmartPrint Desktop exited during startup with code $($process.ExitCode)."
        }

        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and
            $process.MainWindowTitle -eq "SmartPrint by QuantaEdge") {
            $mainWindowReady = $true
        }

        if ($RequireDevTools) {
            try {
                $targets = @(Invoke-RestMethod -Uri "http://127.0.0.1:$debugPort/json/list" -TimeoutSec 2 -ErrorAction Stop)
                $observedTargets = @($targets | ForEach-Object {
                    [PSCustomObject]@{
                        type = $_.type
                        title = $_.title
                        url = $_.url
                        hasDevToolsSocket = -not [string]::IsNullOrWhiteSpace($_.webSocketDebuggerUrl)
                    }
                })
                # WebView2 can publish the page target before the frontend title
                # and React root are ready. Runtime.evaluate remains the authority
                # for completed frontend rendering in this dedicated CI build.
                $pageTarget = @($targets |
                    Where-Object {
                        $_.type -eq "page" -and
                        -not [string]::IsNullOrWhiteSpace($_.webSocketDebuggerUrl)
                    } |
                    Sort-Object {
                        if ($_.title -eq "SmartPrint by QuantaEdge") { 0 }
                        elseif ($_.url -match "^https?://tauri\.localhost(?:/|$)") { 1 }
                        elseif ($_.url -eq "about:blank") { 2 }
                        else { 3 }
                    } |
                    Select-Object -First 1)
            }
            catch {
                # WebView2 may take several seconds to create its first page target.
            }
        }

        if ($mainWindowReady -and (-not $RequireDevTools -or $pageTarget)) { break }
        if ($windowPoll -eq 1 -or $windowPoll % 10 -eq 0) {
            Write-Host "[QA-PROGRESS] Waiting for native window/WebView2 startup; poll=$windowPoll elapsed=$([Math]::Round(($StartupTimeoutSeconds - [Math]::Max(0, ($deadline - [DateTime]::UtcNow).TotalSeconds)), 1))s." -ForegroundColor DarkCyan
        }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)

    if (-not $mainWindowReady) {
        $process.Refresh()
        throw "SmartPrint Desktop did not create its native window with the expected title."
    }

    Write-Host "[QA-CHECK-PASS] Native desktop window opened." -ForegroundColor Green

    if (-not $RequireDevTools) {
        Write-Host "[QA-ACTION] Inspecting the production WebView2 accessibility tree for workspace/sign-in landmarks." -ForegroundColor Cyan
        # The installed production binary intentionally has no remote-debugging
        # endpoint. Verify visible, app-owned content through the Windows
        # accessibility tree instead of weakening production WebView2 flags.
        $workspaceLabels = @(
            "Order Journey", "Overview", "Print Queue", "Printers", "QR Stations",
            "Print Agent", "Pricing", "Subscription", "Payments", "Customers",
            "Reports", "Settings"
        )
        $authLabels = @("Welcome back", "Sign in to workspace")
        $accessibility = $null
        $matchedWorkspaceLabels = [System.Collections.Generic.List[string]]::new()
        $matchedAuthLabels = [System.Collections.Generic.List[string]]::new()
        $renderedMode = $null
        $uiAutomationError = $null
        $uiDeadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        $uiAttempt = 0

        # The native HWND can appear before WebView2 finishes its document,
        # Tauri bridge startup, credential restoration and React rendering.
        # Poll for app-owned content rather than taking a single early snapshot.
        do {
            $uiAttempt++
            if ($uiAttempt -eq 1 -or $uiAttempt % 10 -eq 0) {
                Write-Host "[QA-PROGRESS] Waiting for React workspace/sign-in content via Windows UI Automation; attempt=$uiAttempt." -ForegroundColor DarkCyan
            }
            try {
                $accessibility = Get-DesktopAccessibilitySnapshot $process.MainWindowHandle
                $matchedWorkspaceLabels.Clear()
                $matchedAuthLabels.Clear()

                foreach ($expected in $workspaceLabels) {
                    foreach ($observed in $accessibility.names) {
                        if ($observed.IndexOf($expected, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                            [void]$matchedWorkspaceLabels.Add($expected)
                            break
                        }
                    }
                }

                foreach ($expected in $authLabels) {
                    foreach ($observed in $accessibility.names) {
                        if ($observed.IndexOf($expected, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                            [void]$matchedAuthLabels.Add($expected)
                            break
                        }
                    }
                }

                if ($matchedWorkspaceLabels.Count -ge 2) {
                    $renderedMode = "authenticated-workspace"
                }
                elseif ($matchedAuthLabels.Count -eq $authLabels.Count) {
                    $renderedMode = "sign-in"
                }
                $uiAutomationError = $null
            }
            catch {
                $uiAutomationError = $_.Exception.Message
            }

            if ($renderedMode) { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $uiDeadline)

        if (-not $renderedMode) {
            $visibleNames = if ($accessibility) {
                (@($accessibility.names | Select-Object -First 40) -join " | ")
            } else {
                "<no accessibility snapshot>"
            }
            $visibleCount = if ($accessibility) { $accessibility.visibleNamedElementCount } else { 0 }
            $controlCount = if ($accessibility) { $accessibility.visibleControlTypeCount } else { 0 }
            $uiError = if ($uiAutomationError) { " Last UI Automation error: $uiAutomationError" } else { "" }
            throw ("Windows UI Automation did not find the SmartPrint workspace or sign-in interface within " +
                "$StartupTimeoutSeconds seconds after the native window opened. " +
                "Attempts: $uiAttempt; visible named elements: $visibleCount; " +
                "visible control types: $controlCount; workspace labels matched: " +
                "$($matchedWorkspaceLabels -join ', '); sign-in labels matched: " +
                "$($matchedAuthLabels -join ', '); visible names: $visibleNames.$uiError")
        }

        $process.Refresh()
        if ($process.HasExited) {
            throw "SmartPrint Desktop exited immediately after its visible interface was detected."
        }

        Write-Host "[QA-CHECK-PASS] Windows UI Automation exposed visible SmartPrint application content." -ForegroundColor Green
        Write-Host "[QA-CHECK-PASS] Production WebView2 rendered an app-owned workspace or sign-in screen." -ForegroundColor Green
        Write-Host "Desktop accessibility evidence: mode='$renderedMode', attempts=$uiAttempt, visibleNamedElements=$($accessibility.visibleNamedElementCount), matchedWorkspaceLabels=$($matchedWorkspaceLabels.Count), matchedSignInLabels=$($matchedAuthLabels.Count)"
        $desktopSmokeEvidence = [PSCustomObject]@{
            verification = "windows-ui-automation"
            mode = $renderedMode
            nativeWindowOpened = $true
            attempts = $uiAttempt
            waitedSeconds = [Math]::Round(($StartupTimeoutSeconds - [Math]::Max(0, ($uiDeadline - [DateTime]::UtcNow).TotalSeconds)), 1)
            visibleNamedElementCount = $accessibility.visibleNamedElementCount
            visibleControlTypeCount = $accessibility.visibleControlTypeCount
            visibleNames = @($accessibility.names)
            matchedWorkspaceLabels = @($matchedWorkspaceLabels)
            matchedSignInLabels = @($matchedAuthLabels)
        }
    }
    else {
        if (-not $pageTarget) {
            $targetEvidence = $observedTargets | ConvertTo-Json -Depth 5 -Compress
            throw "The CI diagnostic build opened its native window, but WebView2 exposed no page target on its private DevTools port. Observed targets: $targetEvidence"
        }

        $webSocket = [System.Net.WebSockets.ClientWebSocket]::new()
        $webSocket.Options.SetRequestHeader("Origin", "http://localhost")
        $connectTimeoutSeconds = 10
        $connectCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($connectTimeoutSeconds))
        try {
            $webSocket.ConnectAsync([Uri]$pageTarget.webSocketDebuggerUrl, $connectCancellation.Token).GetAwaiter().GetResult()
        }
        catch [System.OperationCanceledException] {
            throw "WebView2 DevTools connection did not open within $connectTimeoutSeconds seconds."
        }
        finally {
            $connectCancellation.Dispose()
        }

        $probeExpression = @"
new Promise(resolve => {
  const started = Date.now();
  const inspect = () => {
    const root = document.getElementById("smartprint-desktop-root");
    const result = {
      title: document.title,
      appShell: document.body ? document.body.dataset.appShell : null,
      rootId: root ? root.id : null,
      rootChildCount: root ? root.childElementCount : 0,
      rootTextLength: root ? (root.innerText || "").trim().length : 0
    };
    if (
      result.title === "SmartPrint by QuantaEdge" &&
      result.appShell === "desktop" &&
      result.rootId === "smartprint-desktop-root" &&
      result.rootChildCount > 0 &&
      result.rootTextLength > 0
    ) {
      resolve(JSON.stringify(result));
    } else if (Date.now() - started >= 15000) {
      resolve(JSON.stringify(result));
    } else {
      window.setTimeout(inspect, 100);
    }
  };
  inspect();
})
"@.Trim()

        $command = @{
            id = 1
            method = "Runtime.evaluate"
            params = @{
                expression = $probeExpression
                returnByValue = $true
                awaitPromise = $true
            }
        } | ConvertTo-Json -Depth 8 -Compress

        $payload = [Text.Encoding]::UTF8.GetBytes($command)
        $sendTimeoutSeconds = 10
        $sendCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($sendTimeoutSeconds))
        try {
            $webSocket.SendAsync(
                [System.ArraySegment[byte]]::new($payload),
                [System.Net.WebSockets.WebSocketMessageType]::Text,
                $true,
                $sendCancellation.Token
            ).GetAwaiter().GetResult()
        }
        catch [System.OperationCanceledException] {
            throw "WebView2 DevTools did not accept the startup probe within $sendTimeoutSeconds seconds."
        }
        finally {
            $sendCancellation.Dispose()
        }

        $responseText = [Text.StringBuilder]::new()
        $buffer = New-Object byte[] 8192
        $responseTimeoutSeconds = 20
        $responseDeadline = [DateTime]::UtcNow.AddSeconds($responseTimeoutSeconds)
        $receiveCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($responseTimeoutSeconds))
        $probeResponse = $null

        try {
            do {
                $received = $webSocket.ReceiveAsync(
                    [System.ArraySegment[byte]]::new($buffer),
                    $receiveCancellation.Token
                ).GetAwaiter().GetResult()

                if ($received.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                    throw "WebView2 closed its DevTools connection before returning the startup probe."
                }

                [void]$responseText.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $received.Count))

                if ($received.EndOfMessage) {
                    $message = $responseText.ToString() | ConvertFrom-Json -ErrorAction Stop
                    $responseText.Clear() | Out-Null

                    if ($message.id -eq 1) {
                        $probeResponse = $message
                        break
                    }
                }
            } while ([DateTime]::UtcNow -lt $responseDeadline)
        }
        catch [System.OperationCanceledException] {
            throw "WebView2 did not return the desktop frontend startup probe within $responseTimeoutSeconds seconds."
        }
        finally {
            $receiveCancellation.Dispose()
        }

        if (-not $probeResponse) {
            throw "WebView2 did not return the desktop frontend startup probe within $responseTimeoutSeconds seconds."
        }
        if ($probeResponse.error) {
            throw "WebView2 startup probe failed: $($probeResponse.error.message)"
        }

        $probeJson = [string]$probeResponse.result.result.value
        if ([string]::IsNullOrWhiteSpace($probeJson)) {
            throw "The desktop frontend returned an empty startup probe."
        }

        $probe = $probeJson | ConvertFrom-Json -ErrorAction Stop

        if ($probe.title -ne "SmartPrint by QuantaEdge" -or
            $probe.appShell -ne "desktop" -or
            $probe.rootId -ne "smartprint-desktop-root" -or
            [int]$probe.rootChildCount -lt 1 -or
            [int]$probe.rootTextLength -lt 1) {
            throw "Desktop frontend did not render correctly. Probe: $probeJson"
        }

        $process.Refresh()
        if ($process.HasExited) {
            throw "SmartPrint Desktop exited immediately after the frontend rendered."
        }

        Write-Host "PASS: WebView2 loaded the SmartPrint document." -ForegroundColor Green
        Write-Host "PASS: Desktop shell marker is present." -ForegroundColor Green
        Write-Host "PASS: React rendered visible application content." -ForegroundColor Green
        Write-Host "Desktop startup evidence: title='$($probe.title)', shell='$($probe.appShell)', rootChildren=$($probe.rootChildCount), textLength=$($probe.rootTextLength)"
        $desktopSmokeEvidence = [PSCustomObject]@{
            verification = "webview2-devtools"
            mode = "ci-diagnostic"
            nativeWindowOpened = $true
            documentTitle = [string]$probe.title
            appShell = [string]$probe.appShell
            rootChildCount = [int]$probe.rootChildCount
            rootTextLength = [int]$probe.rootTextLength
        }
    }
}
finally {
    if ($webSocket) {
        try {
            if ($webSocket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
                $closeCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(2))
                try {
                    $webSocket.CloseAsync(
                        [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                        "Smoke test complete",
                        $closeCancellation.Token
                    ).GetAwaiter().GetResult()
                }
                finally {
                    $closeCancellation.Dispose()
                }
            }
        }
        catch { }
        $webSocket.Dispose()
    }

    if ($process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                [void]$process.CloseMainWindow()
                if (-not $process.WaitForExit(8000)) {
                    # Only terminate the exact process started by this smoke test.
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    [void]$process.WaitForExit(5000)
                }
            }
        }
        catch { }
        $process.Dispose()
    }

    if ($RequireDevTools) {
        [Environment]::SetEnvironmentVariable($dataDirectoryEnvironmentName, $previousWebViewDataDirectory, "Process")

        # Clean only the CI diagnostic browser process/profile. Never terminate
        # a shared Edge/WebView2 process or touch the production user profile.
        try {
            $profileProcessDeadline = [DateTime]::UtcNow.AddSeconds(10)
            $profileProcesses = @()
            do {
                $profileProcesses = @(
                    Get-CimInstance -ClassName Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction Stop |
                        Where-Object {
                            $_.CommandLine -and
                            $_.CommandLine.IndexOf($testWebViewDataDirectory, [StringComparison]::OrdinalIgnoreCase) -ge 0
                        }
                )
                if ($profileProcesses.Count -eq 0) { break }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $profileProcessDeadline)

            if ($profileProcesses.Count -eq 0) {
                Remove-Item -LiteralPath $testWebViewDataDirectory -Recurse -Force -ErrorAction SilentlyContinue
            } else {
                Write-Warning "The isolated WebView2 browser is still exiting. Its temporary profile was left untouched at: $testWebViewDataDirectory"
            }
        }
        catch {
            Write-Warning "Could not verify that the isolated WebView2 profile is unused; leaving it untouched at: $testWebViewDataDirectory"
        }
    }
}

if ($desktopSmokeEvidence) {
    Write-Output $desktopSmokeEvidence
}
