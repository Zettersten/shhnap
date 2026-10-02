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

# Keep the CI variables independent from the identity assigned in Partner Center.
# A typo in a repository variable must fail before it can produce a misleadingly
# valid package and upload pair.
Assert-ExactValue 'Reserved Store identity name' $IdentityName '24664Nenvy.Shnapp'
Assert-ExactValue 'Reserved Store identity publisher' $IdentityPublisher 'CN=B431E658-A1AD-472F-8DC9-270D1AFEB32C'
Assert-ExactValue 'Reserved Store publisher display name' $PublisherDisplayName 'Nenvy'

Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-Sha256 {
    param([System.IO.Stream] $Stream)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha256.ComputeHash($Stream)).Replace('-', '') }
    finally { $sha256.Dispose() }
}

function Assert-StoreMsix {
    param([string] $Path, [string] $Label)

    try { $package = [System.IO.Compression.ZipFile]::OpenRead($Path) }
    catch { throw "$Label is not a readable MSIX archive: $($_.Exception.Message)" }
    try {
        $manifestEntry = $package.GetEntry('AppxManifest.xml')
        if ($null -eq $manifestEntry) { throw "$Label has no AppxManifest.xml." }
        $executableEntry = $package.GetEntry('Shnapp.exe')
        if ($null -eq $executableEntry -or $executableEntry.Length -eq 0) {
            throw "$Label has no Shnapp.exe executable."
        }

        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try { [xml] $manifest = $reader.ReadToEnd() }
        catch { throw "$Label has a malformed AppxManifest.xml: $($_.Exception.Message)" }
        finally { $reader.Dispose() }

        $namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespaces.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
        $identity = $manifest.SelectSingleNode('/appx:Package/appx:Identity', $namespaces)
        $displayName = $manifest.SelectSingleNode('/appx:Package/appx:Properties/appx:PublisherDisplayName', $namespaces)
        if ($null -eq $identity -or $null -eq $displayName) {
            throw "$Label manifest has no package identity or publisher display name."
        }

        Assert-ExactValue "$Label identity name" ($identity.GetAttribute('Name')) $IdentityName
        Assert-ExactValue "$Label identity publisher" ($identity.GetAttribute('Publisher')) $IdentityPublisher
        Assert-ExactValue "$Label version" ($identity.GetAttribute('Version')) $expectedVersion
        Assert-ExactValue "$Label architecture" ($identity.GetAttribute('ProcessorArchitecture').ToLowerInvariant()) ($ProcessorArchitecture.ToLowerInvariant())
        Assert-ExactValue "$Label publisher display name" $displayName.InnerText $PublisherDisplayName

        $fileHashes = [System.Collections.Generic.SortedDictionary[string,string]]::new([System.StringComparer]::Ordinal)
        foreach ($entry in $package.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $entryStream = $entry.Open()
            try { $fileHashes.Add($entry.FullName, (Get-Sha256 $entryStream)) }
            finally { $entryStream.Dispose() }
        }
        return ,$fileHashes
    }
    finally { $package.Dispose() }
}

$packageFileHashes = Assert-StoreMsix $packageFile 'Standalone MSIX'

try { $upload = [System.IO.Compression.ZipFile]::OpenRead($uploadFile) }
catch { throw "The upload file is not a readable archive: $($_.Exception.Message)" }
try {
    $embeddedPackages = @($upload.Entries | Where-Object { [System.IO.Path]::GetExtension($_.FullName) -ieq '.msix' })
    if ($embeddedPackages.Count -ne 1) {
        throw "Expected one MSIX inside the upload file; found $($embeddedPackages.Count)."
    }
    if ($embeddedPackages[0].Length -eq 0 -or $embeddedPackages[0].Length -gt 25GB) {
        throw 'The MSIX inside the upload file is empty or exceeds the Store size limit.'
    }
    $symbols = @($upload.Entries | Where-Object { [System.IO.Path]::GetExtension($_.FullName) -ieq '.appxsym' -and $_.Length -gt 0 })
    if ($symbols.Count -lt 1) { throw 'The upload file has no nonempty public-symbol .appxsym file.' }

    # StoreUpload can repackage an MSIX, changing ZIP bytes without changing the app.
    # Inspect the actual inner package rather than comparing two container hashes.
    $embeddedFile = [System.IO.Path]::GetTempFileName()
    try {
        $embeddedStream = $embeddedPackages[0].Open()
        try {
            $destination = [System.IO.File]::Create($embeddedFile)
            try { $embeddedStream.CopyTo($destination) }
            finally { $destination.Dispose() }
        }
        finally { $embeddedStream.Dispose() }
        $embeddedFileHashes = Assert-StoreMsix $embeddedFile 'Upload MSIX'
        Assert-ExactValue 'MSIX file count inside upload' "$($embeddedFileHashes.Count)" "$($packageFileHashes.Count)"
        foreach ($entryName in $packageFileHashes.Keys) {
            if (-not $embeddedFileHashes.ContainsKey($entryName)) {
                throw "Upload MSIX is missing $entryName."
            }
            Assert-ExactValue "Upload MSIX file $entryName" $embeddedFileHashes[$entryName] $packageFileHashes[$entryName]
        }
    }
    finally { [System.IO.File]::Delete($embeddedFile) }
}
finally { $upload.Dispose() }

Write-Host "Validated Store $ProcessorArchitecture package $expectedVersion, both MSIX identities and all payload files, and public symbols."
