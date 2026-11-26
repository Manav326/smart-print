[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$ReleaseTag,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProductId,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SellerAccountId,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TenantId,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ClientId,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ClientSecret,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://')]
    [string]$PublicDownloadBaseUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$baseUri = [Uri]$PublicDownloadBaseUrl
if ($baseUri.AbsolutePath -notin @("", "/") -or $baseUri.Query -or $baseUri.Fragment -or $baseUri.UserInfo) {
    throw "PublicDownloadBaseUrl must be a canonical HTTPS origin without path, query, fragment or credentials."
}

$packageUrl = "$($baseUri.GetLeftPart([UriPartial]::Authority))/$ReleaseTag/SmartPrint-Windows-Store-Setup.exe"
$documentationUrl = "https://raw.githubusercontent.com/Manav326/smart-print/$ReleaseTag/docs/windows-installer-exit-codes.md"
$apiBase = "https://api.store.microsoft.com"
$tokenUri = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"

Write-Host "Requesting Microsoft Store Submission API token."
$token = Invoke-RestMethod -Method Post -Uri $tokenUri -ContentType "application/x-www-form-urlencoded" -Body @{
    grant_type = "client_credentials"
    client_id = $ClientId
    client_secret = $ClientSecret
    scope = "https://api.store.microsoft.com/.default"
} -TimeoutSec 60
if ([string]::IsNullOrWhiteSpace($token.access_token)) {
    throw "Microsoft identity endpoint did not return an access token."
}

$headers = @{
    Authorization = "Bearer $($token.access_token)"
    "X-Seller-Account-Id" = $SellerAccountId
    Accept = "application/json"
}

function Invoke-StoreApi {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("GET", "POST", "PUT", "PATCH")][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [object]$Body
    )
    $parameters = @{
        Method = $Method
        Uri = "$apiBase$Path"
        Headers = $headers
        TimeoutSec = 60
        ErrorAction = "Stop"
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = ConvertTo-Json -InputObject $Body -Depth 20 -Compress
    }
    # GET and PATCH are safe to retry for transient throttling/service errors.
    # Do not automatically retry POST: a timed-out submission could already have
    # been accepted by Partner Center and must be reconciled before another POST.
    $maxAttempts = if ($Method -in @("GET", "PATCH")) { 5 } else { 1 }
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            $response = Invoke-RestMethod @parameters
            if ($response -and $response.PSObject.Properties.Name -contains "isSuccess" -and -not $response.isSuccess) {
                $details = ($response.errors | ForEach-Object { "$($_.code): $($_.message) [$($_.target)]" }) -join "; "
                throw "Microsoft Store API rejected $Method $Path. $details"
            }
            return $response
        }
        catch {
            $httpResponse = $_.Exception.Response
            $statusCode = 0
            if ($httpResponse -and $null -ne $httpResponse.StatusCode) {
                $statusCode = [int]$httpResponse.StatusCode
            }
            $retryable = $statusCode -in @(408, 429, 500, 502, 503, 504)
            if (-not $retryable -or $attempt -ge $maxAttempts) {
                throw
            }

            $retryAfterSeconds = 0
            try {
                $retryAfterHeader = $httpResponse.Headers["Retry-After"]
                if ($retryAfterHeader) { $retryAfterSeconds = [int]$retryAfterHeader }
            }
            catch {
                $retryAfterSeconds = 0
            }
            if ($retryAfterSeconds -le 0) {
                $retryAfterSeconds = [Math]::Min(30, [Math]::Pow(2, $attempt))
            }
            $retryAfterSeconds = [Math]::Min(300, [Math]::Max(1, $retryAfterSeconds))
            Write-Warning "Microsoft Store API returned HTTP $statusCode for $Method $Path. Retrying in $retryAfterSeconds seconds (attempt $attempt of $maxAttempts)."
            Start-Sleep -Seconds $retryAfterSeconds
        }
    }
    throw "Microsoft Store API request exhausted its retry policy for $Method $Path."
}

$draftStatusPath = "/submission/v1/product/$ProductId/status"
$initialStatus = Invoke-StoreApi -Method GET -Path $draftStatusPath
if (-not [string]::IsNullOrWhiteSpace([string]$initialStatus.responseData.ongoingSubmissionId)) {
    throw "Microsoft already has an active submission ($($initialStatus.responseData.ongoingSubmissionId)). Wait for it to finish before updating the package."
}

$packagesPath = "/submission/v1/product/$ProductId/packages"
$packagesResponse = Invoke-StoreApi -Method GET -Path $packagesPath
$packages = @($packagesResponse.responseData.packages)
if ($packages.Count -eq 0) {
    throw "No existing package is registered for this product. Complete the first Partner Center submission manually, including an x64 EXE package and age rating, then rerun this workflow."
}

$x64Packages = @($packages | Where-Object { @($_.architectures) -contains "X64" })
if ($x64Packages.Count -ne 1) {
    throw "Expected exactly one existing X64 package in the Partner Center draft; found $($x64Packages.Count). Refusing to replace or remove other architectures."
}
$existingArchitectures = @($x64Packages[0].architectures)
if ($existingArchitectures.Count -ne 1 -or $existingArchitectures[0] -ne "X64") {
    throw "The existing Store package must target X64 only; refusing to patch a mixed-architecture package."
}
if ($x64Packages[0].packageType -ne "exe") {
    throw "The existing Partner Center X64 package is not an EXE package; refusing to patch a different package type."
}

$package = @{
    packageUrl = $packageUrl
    languages = @("en-us")
    architectures = @("X64")
    isSilentInstall = $false
    installerParameters = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG"
    genericDocUrl = $documentationUrl
    errorDetails = @()
    packageType = "exe"
}
$packageId = $x64Packages[0].packageId
if ([string]::IsNullOrWhiteSpace($packageId)) {
    throw "The existing X64 package does not have a packageId; cannot safely patch it."
}

Write-Host "Updating only the existing X64 package for $ReleaseTag."
$null = Invoke-StoreApi -Method PATCH -Path "$packagesPath/$packageId" -Body $package
$null = Invoke-StoreApi -Method POST -Path "$packagesPath/commit"

Write-Host "Waiting for Microsoft Store package validation."
$ready = $false
for ($attempt = 1; $attempt -le 90; $attempt++) {
    Start-Sleep -Seconds 10
    $status = Invoke-StoreApi -Method GET -Path "/submission/v1/product/$ProductId/status"
    if (-not [string]::IsNullOrWhiteSpace([string]$status.responseData.ongoingSubmissionId)) {
        throw "Microsoft reports an already active submission ($($status.responseData.ongoingSubmissionId)); refusing to create a competing submission."
    }
    if ($status.responseData.isReady -eq $true) {
        $ready = $true
        break
    }
    if ($status.isSuccess -eq $false) {
        $details = ($status.errors | ForEach-Object { "$($_.code): $($_.message)" }) -join "; "
        throw "Microsoft Store package validation failed. $details"
    }
    Write-Host "Package validation is still running ($attempt/90)."
}
if (-not $ready) {
    throw "Microsoft Store package validation did not become ready within 15 minutes. No submission was created."
}

Write-Host "Submitting the validated Windows package for Microsoft certification."
$submission = Invoke-StoreApi -Method POST -Path "/submission/v1/product/$ProductId/submit"
$submissionId = $submission.responseData.submissionId
if ([string]::IsNullOrWhiteSpace($submissionId)) {
    throw "Microsoft Store API did not return a submission ID. Check Partner Center before retrying."
}

[pscustomobject]@{
    releaseTag = $ReleaseTag
    packageUrl = $packageUrl
    productId = $ProductId
    submissionId = $submissionId
    status = "Submitted for Microsoft certification"
} | ConvertTo-Json -Depth 5 | Write-Output
