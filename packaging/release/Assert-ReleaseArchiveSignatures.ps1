param(
    [Parameter(Mandatory = $true)][string] $ArchivePath,
    [Parameter(Mandatory = $true)][string[]] $EntryPaths,
    [Parameter(Mandatory = $true)][string] $SignToolPath,
    [Parameter(Mandatory = $true)][string] $ExpectedPublisher,
    [Parameter(Mandatory = $true)][string] $ExpectedProfileEku
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
    throw "Release archive is missing: $ArchivePath"
}
if ($EntryPaths.Count -eq 0 -or @($EntryPaths | Select-Object -Unique).Count -ne $EntryPaths.Count) {
    throw 'Provide distinct executable paths to verify in the archive.'
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "shnapp-signature-check-$([guid]::NewGuid().ToString('N'))"
[void][System.IO.Directory]::CreateDirectory($scratch)
$archive = $null
try {
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
    for ($index = 0; $index -lt $EntryPaths.Count; $index++) {
        $entryPath = $EntryPaths[$index]
        if ($entryPath -cnotmatch '^(?:[A-Za-z0-9_. -]+/)*[A-Za-z0-9_. -]+\.(?:exe|dll)$' -or
            @($entryPath.Split('/') | Where-Object { $_ -cin @('.', '..') }).Count -ne 0) {
            throw "Invalid executable or DLL path in release archive: $entryPath"
        }
        $entry = $archive.GetEntry($entryPath)
        if ($null -eq $entry -or $entry.Length -eq 0) {
            throw "Release archive is missing executable or DLL $entryPath."
        }
        $extension = [System.IO.Path]::GetExtension($entryPath)
        $extracted = Join-Path $scratch "$index$extension"
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $extracted)
        $ownedBinary = [System.IO.Path]::GetFileName($entryPath) -imatch '^Shnapp.*\.(?:exe|dll)$'
        if ($ownedBinary) {
            & (Join-Path $PSScriptRoot 'Sign-ReleaseExecutable.ps1') `
                -Path $extracted -SignToolPath $SignToolPath `
                -ExpectedPublisher $ExpectedPublisher -ExpectedProfileEku $ExpectedProfileEku -VerifyOnly
        }
        else {
            $signature = Get-AuthenticodeSignature -LiteralPath $extracted
            if ($signature.Status -ne 'Valid' -or
                $null -eq $signature.SignerCertificate -or
                $null -eq $signature.TimeStamperCertificate) {
                throw "Release vendor binary is not trusted and time-stamped: $entryPath"
            }
        }
    }
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ($resolvedScratch.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith('shnapp-signature-check-')) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
