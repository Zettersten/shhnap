param(
    [Parameter(Mandatory = $true)][string] $ManifestPath,
    [Parameter(Mandatory = $true)][string] $ReleaseTag,
    [Parameter(Mandatory = $true)][string] $IdentityName,
    [Parameter(Mandatory = $true)][string] $IdentityPublisher,
    [Parameter(Mandatory = $true)][string] $PublisherDisplayName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ReleaseTag -notmatch '^v([1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Store packages require a stable v1.0.0-style release tag.'
}
$parts = @($Matches[1], $Matches[2], $Matches[3])
if (@($parts | Where-Object { [long]::Parse($_) -gt 65535 }).Count -ne 0) {
    throw 'Each MSIX version component must be at most 65535.'
}
if ($IdentityName -eq 'B26188CE-092A-4B82-800F-5B908190A781' -or
    $IdentityPublisher -eq 'CN=AppPublisher' -or
    $PublisherDisplayName -eq 'AppPublisher') {
    throw 'Use exact Partner Center Product identity values, not the development placeholders.'
}

$resolvedPath = (Resolve-Path -LiteralPath $ManifestPath).Path
[xml] $manifest = Get-Content -LiteralPath $resolvedPath -Raw
$namespace = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespace.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$identity = $manifest.SelectSingleNode('/appx:Package/appx:Identity', $namespace)
$publisher = $manifest.SelectSingleNode('/appx:Package/appx:Properties/appx:PublisherDisplayName', $namespace)
if ($null -eq $identity -or $null -eq $publisher) {
    throw 'The package manifest is missing required Store identity nodes.'
}

$identity.SetAttribute('Name', $IdentityName)
$identity.SetAttribute('Publisher', $IdentityPublisher)
$identity.SetAttribute('Version', "$($parts[0]).$($parts[1]).$($parts[2]).0")
$publisher.InnerText = $PublisherDisplayName
$manifest.Save($resolvedPath)
Write-Host "Store package version: $($identity.GetAttribute('Version'))"
