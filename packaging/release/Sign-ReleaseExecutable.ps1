param(
    [Parameter(Mandatory = $true)][string] $Path,
    [Parameter(Mandatory = $true)][string] $SignToolPath,
    [Parameter(Mandatory = $true)][string] $ExpectedPublisher,
    [Parameter(Mandatory = $true)][string] $ExpectedProfileEku,
    [string] $DlibPath,
    [string] $MetadataPath,
    [switch] $VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
    throw "SignTool is missing: $SignToolPath"
}
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Executable is missing: $Path"
}
$file = (Resolve-Path -LiteralPath $Path).Path
if ([System.IO.Path]::GetExtension($file) -cnotin @('.exe', '.dll')) {
    throw "Release signing requires an EXE or DLL file: $file"
}

if (-not $VerifyOnly) {
    if (-not (Test-Path -LiteralPath $DlibPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $MetadataPath -PathType Leaf)) {
        throw 'Artifact Signing requires the x64 Dlib and signing metadata file.'
    }
    & $SignToolPath sign /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 `
        /dlib $DlibPath /dmdf $MetadataPath $file
    if ($LASTEXITCODE -ne 0) {
        throw "Artifact Signing could not sign $file."
    }
}

& $SignToolPath verify /pa /tw $file
if ($LASTEXITCODE -ne 0) {
    throw "SignTool rejected the Authenticode signature on $file."
}
$signature = Get-AuthenticodeSignature -LiteralPath $file
if ($signature.Status -ne 'Valid' -or
    $null -eq $signature.SignerCertificate -or
    $null -eq $signature.TimeStamperCertificate) {
    throw "The Authenticode signature on $file is invalid or is not time-stamped."
}
& (Join-Path $PSScriptRoot 'Assert-ReleaseSignerCertificate.ps1') `
    -Certificate $signature.SignerCertificate `
    -ExpectedPublisher $ExpectedPublisher -ExpectedProfileEku $ExpectedProfileEku
Write-Host "Verified Artifact Signing signature on $file."
