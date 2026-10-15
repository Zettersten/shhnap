param(
    [Parameter(Mandatory = $true)]
    [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
    [Parameter(Mandatory = $true)][string] $ExpectedPublisher,
    [Parameter(Mandatory = $true)][string] $ExpectedProfileEku
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ExpectedPublisher) -or
    $ExpectedPublisher -match '[\r\n]' -or
    $ExpectedProfileEku -cnotmatch '^1\.3\.6\.1\.4\.1\.311\.97\.(?!1\.0$)[0-9]+(?:\.[0-9]+)+$') {
    throw 'An exact publisher and Artifact Signing certificate profile EKU are required.'
}

$publisher = $ExpectedPublisher.Trim()
$commonName = $Certificate.GetNameInfo(
    [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
$organizationPattern = '(?:^|,\s*)O=' + [regex]::Escape($publisher) + '(?:,|$)'
if ($commonName -cne $publisher -or $Certificate.Subject -cnotmatch $organizationPattern) {
    throw "The release signer subject is not the expected publisher: $publisher."
}

$ekuExtensions = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
if ($ekuExtensions.Count -ne 1) {
    throw 'The release signer has no unambiguous extended key usage extension.'
}
$eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$ekuExtensions[0]
$ekus = @($eku.EnhancedKeyUsages | ForEach-Object Value)
foreach ($requiredOid in @(
    '1.3.6.1.5.5.7.3.3',        # Code signing
    '1.3.6.1.4.1.311.97.1.0', # Artifact Signing Public Trust
    $ExpectedProfileEku       # Stable across the profile's daily certificate rotation
)) {
    if ($requiredOid -cnotin $ekus) {
        throw "The release signer is missing required certificate EKU $requiredOid."
    }
}
