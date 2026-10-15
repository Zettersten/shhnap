Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Release signing requires a Windows runner.'
}
if ([string]::IsNullOrWhiteSpace($env:GITHUB_ENV) -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'Release signing must run in GitHub Actions with GITHUB_ENV and RUNNER_TEMP.'
}

$settings = [ordered]@{
    ARTIFACT_SIGNING_ENDPOINT = $env:ARTIFACT_SIGNING_ENDPOINT
    ARTIFACT_SIGNING_ACCOUNT_NAME = $env:ARTIFACT_SIGNING_ACCOUNT_NAME
    ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME = $env:ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME
    ARTIFACT_SIGNING_EXPECTED_PUBLISHER = $env:ARTIFACT_SIGNING_EXPECTED_PUBLISHER
    ARTIFACT_SIGNING_PROFILE_EKU = $env:ARTIFACT_SIGNING_PROFILE_EKU
}
foreach ($setting in $settings.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($setting.Value) -or $setting.Value -match '[\r\n]') {
        throw "The release-signing environment needs a single-line $($setting.Key) value."
    }
}

$endpoint = $null
if (-not [Uri]::TryCreate($settings.ARTIFACT_SIGNING_ENDPOINT, [UriKind]::Absolute, [ref]$endpoint) -or
    $endpoint.Scheme -cne 'https' -or
    $endpoint.Host -cnotmatch '^[a-z0-9]+\.codesigning\.azure\.net$' -or
    $endpoint.AbsolutePath -cne '/' -or
    $endpoint.Port -ne 443 -or
    $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment) {
    throw 'ARTIFACT_SIGNING_ENDPOINT must be an HTTPS Artifact Signing regional endpoint.'
}
if ($settings.ARTIFACT_SIGNING_PROFILE_EKU -cnotmatch '^1\.3\.6\.1\.4\.1\.311\.97\.(?!1\.0$)[0-9]+(?:\.[0-9]+)+$') {
    throw 'ARTIFACT_SIGNING_PROFILE_EKU must identify the specific Public Trust certificate profile.'
}

$sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$signTool = Get-ChildItem -LiteralPath $sdkBin -Filter signtool.exe -Recurse -File |
    Where-Object { $_.Directory.Name -eq 'x64' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signTool -or [version]$signTool.VersionInfo.ProductVersion -lt [version]'10.0.22621.755') {
    throw 'Release signing needs Windows SDK x64 SignTool 10.0.22621.755 or newer.'
}
if (-not @(dotnet --list-runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 8\.' }).Count) {
    throw 'Artifact Signing requires the x64 .NET 8 runtime on the Windows runner.'
}
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Artifact Signing requires Azure CLI authentication from azure/login before this step.'
}

# The fixed package hash comes from Microsoft's NuGet catalog for version 1.0.146.
$clientVersion = '1.0.146'
$packageHashBase64 = '/G0hG/VDIS6psW23b5t1V/ORIgn0PVAOtGj9w+hkPmAuI4pempfwlW50E2GtHscnz9+5Qz3hbC4mcb7ej2Yn1A=='
$packageName = "microsoft.artifactsigning.client.$clientVersion.nupkg"
$packageUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.artifactsigning.client/$clientVersion/$packageName"
$packagePath = Join-Path $env:RUNNER_TEMP $packageName
$clientDirectory = Join-Path $env:RUNNER_TEMP "artifact-signing-client-$clientVersion"
try {
    Invoke-WebRequest -Uri $packageUrl -OutFile $packagePath -TimeoutSec 120
    $expectedHash = [Convert]::ToHexString([Convert]::FromBase64String($packageHashBase64))
    if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA512).Hash -cne $expectedHash) {
        throw 'The Artifact Signing client package hash differs from the pinned Microsoft NuGet release.'
    }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $clientDirectory)
}
finally {
    if (Test-Path -LiteralPath $packagePath -PathType Leaf) {
        Remove-Item -LiteralPath $packagePath -Force
    }
}
$dlibPath = Join-Path $clientDirectory 'bin/x64/Azure.CodeSigning.Dlib.dll'
if (-not (Test-Path -LiteralPath $dlibPath -PathType Leaf)) {
    throw 'The pinned Artifact Signing client package is missing its x64 Dlib.'
}

$metadataPath = Join-Path $env:RUNNER_TEMP 'shnapp-artifact-signing.json'
$metadata = [ordered]@{
    Endpoint = $endpoint.AbsoluteUri.TrimEnd('/')
    CodeSigningAccountName = $settings.ARTIFACT_SIGNING_ACCOUNT_NAME
    CertificateProfileName = $settings.ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME
    ExcludeCredentials = @(
        'EnvironmentCredential', 'ManagedIdentityCredential', 'WorkloadIdentityCredential',
        'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential',
        'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential'
    )
}
[System.IO.File]::WriteAllText($metadataPath,
    (ConvertTo-Json -InputObject $metadata -Depth 4), [System.Text.UTF8Encoding]::new($false))

@(
    "RELEASE_SIGNTOOL_PATH=$($signTool.FullName)"
    "RELEASE_SIGNING_DLIB_PATH=$dlibPath"
    "RELEASE_SIGNING_METADATA_PATH=$metadataPath"
    "RELEASE_SIGNING_EXPECTED_PUBLISHER=$($settings.ARTIFACT_SIGNING_EXPECTED_PUBLISHER)"
    "RELEASE_SIGNING_PROFILE_EKU=$($settings.ARTIFACT_SIGNING_PROFILE_EKU)"
) | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8NoBOM
Write-Host "Configured Azure Artifact Signing profile $($settings.ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME)."
