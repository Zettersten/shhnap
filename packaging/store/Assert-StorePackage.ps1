param(
    [Parameter(Mandatory = $true)][string] $PackagePath,
    [Parameter(Mandatory = $true)][string] $UploadPath,
    [Parameter(Mandatory = $true)][string] $ReleaseTag,
    [Parameter(Mandatory = $true)][string] $IdentityName,
    [Parameter(Mandatory = $true)][string] $IdentityPublisher,
    [Parameter(Mandatory = $true)][string] $PublisherDisplayName,
    [Parameter(Mandatory = $true)][ValidateSet('x64', 'arm64')][string] $ProcessorArchitecture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ReleaseTag -notmatch '^v([1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Provide a stable v1.0.0-style release tag.'
}
$expectedVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$packageFile = (Resolve-Path -LiteralPath $PackagePath).Path
$uploadFile = (Resolve-Path -LiteralPath $UploadPath).Path

function Assert-ExactValue {
    param([string] $Label, [string] $Actual, [string] $Expected)
    if ($Actual -cne $Expected) {
        throw "$Label mismatch: expected '$Expected', found '$Actual'."
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = [System.IO.Compression.ZipFile]::OpenRead($packageFile)
try {
    $manifestEntry = $package.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) { throw 'The MSIX has no AppxManifest.xml.' }
    if ($null -eq $package.GetEntry('Shnapp.exe')) { throw 'The MSIX has no Shnapp.exe.' }

    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { [xml] $manifest = $reader.ReadToEnd() }
    finally { $reader.Dispose() }

    $namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $manifest.SelectSingleNode('/appx:Package/appx:Identity', $namespaces)
    $displayName = $manifest.SelectSingleNode('/appx:Package/appx:Properties/appx:PublisherDisplayName', $namespaces)
    if ($null -eq $identity -or $null -eq $displayName) {
        throw 'The MSIX manifest has no package identity or publisher display name.'
    }

    Assert-ExactValue 'Package identity name' ($identity.GetAttribute('Name')) $IdentityName
    Assert-ExactValue 'Package identity publisher' ($identity.GetAttribute('Publisher')) $IdentityPublisher
    Assert-ExactValue 'Package version' ($identity.GetAttribute('Version')) $expectedVersion
    Assert-ExactValue 'Package architecture' ($identity.GetAttribute('ProcessorArchitecture').ToLowerInvariant()) ($ProcessorArchitecture.ToLowerInvariant())
    Assert-ExactValue 'Publisher display name' $displayName.InnerText $PublisherDisplayName
}
finally { $package.Dispose() }

$upload = [System.IO.Compression.ZipFile]::OpenRead($uploadFile)
try {
    $embeddedPackages = @($upload.Entries | Where-Object { [System.IO.Path]::GetExtension($_.FullName) -ieq '.msix' })
    if ($embeddedPackages.Count -ne 1) {
        throw "Expected one MSIX inside the upload file; found $($embeddedPackages.Count)."
    }
    $symbols = @($upload.Entries | Where-Object { [System.IO.Path]::GetExtension($_.FullName) -ieq '.appxsym' })
    if ($symbols.Count -lt 1) { throw 'The upload file has no public-symbol .appxsym file.' }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $embeddedStream = $embeddedPackages[0].Open()
    try { $embeddedHash = [BitConverter]::ToString($sha256.ComputeHash($embeddedStream)).Replace('-', '') }
    finally {
        $embeddedStream.Dispose()
        $sha256.Dispose()
    }
    $packageHash = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash
    Assert-ExactValue 'MSIX embedded in upload file' $embeddedHash $packageHash
}
finally { $upload.Dispose() }

Write-Host "Validated Store $ProcessorArchitecture package $expectedVersion, Partner Center identity, matching upload MSIX, and public symbols."
