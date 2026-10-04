param(
    [Parameter(Mandatory = $true)][string] $Path,
    [Parameter(Mandatory = $true)][string] $SignToolPath,
    [Parameter(Mandatory = $true)][string] $CertificateThumbprint,
    [switch] $VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($CertificateThumbprint -cnotmatch '^[0-9A-Fa-f]{40}$') {
    throw 'A code-signing certificate thumbprint is required.'
}
if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
    throw "SignTool is missing: $SignToolPath"
}
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Executable is missing: $Path"
}
$file = (Resolve-Path -LiteralPath $Path).Path
$thumbprint = $CertificateThumbprint.ToUpperInvariant()

if (-not $VerifyOnly) {
    & $SignToolPath sign /fd SHA256 /sha1 $thumbprint `
        /tr http://timestamp.digicert.com /td SHA256 $file
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool could not sign $file."
    }
}
& $SignToolPath verify /pa /tw $file
if ($LASTEXITCODE -ne 0) {
    throw "SignTool rejected the Authenticode signature on $file."
}
$signature = Get-AuthenticodeSignature -LiteralPath $file
if ($signature.Status -ne 'Valid' -or
    $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne $thumbprint -or
    $null -eq $signature.TimeStamperCertificate) {
    throw "The Authenticode signature on $file is invalid, is not time-stamped, or uses another signer."
}
Write-Host "Verified release signature on $file."
