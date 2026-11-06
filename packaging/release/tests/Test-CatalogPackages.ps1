Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$generator = Join-Path $repository 'packaging/release/New-CatalogPackages.ps1'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "shnapp-catalog-test-$([guid]::NewGuid().ToString('N'))"
$assets = Join-Path $scratch 'assets'
$tag = 'v3.0.0'
[void][System.IO.Directory]::CreateDirectory($assets)

function Write-TestArchive([string] $rid, [bool] $includeUnexpectedResource) {
    $name = "Shnapp-$rid.zip"
    $path = Join-Path $assets $name
    $root = "versions/$tag/"
    $entries = [ordered]@{
        'Shnapp.exe' = 'launcher'
        'current-version.txt' = "$tag`n`n"
        'LICENSE' = 'license'
        'THIRD_PARTY_NOTICES.txt' = 'notices'
        "${root}Shnapp.exe" = 'app'
        "${root}Shnapp.pri" = 'pri'
        "${root}Shnapp.dll" = 'app assembly'
        "${root}Shnapp.Core.dll" = 'core assembly'
        "${root}Shnapp.deps.json" = '{}'
        "${root}Shnapp.runtimeconfig.json" = '{}'
        "${root}Microsoft.ui.xaml.dll" = 'WinUI assembly'
        "${root}WinRT.Host.dll" = 'WinRT host'
        "${root}WinRT.Host.dll.mui" = 'WinRT host resource'
        "${root}LICENSE" = 'license'
        "${root}THIRD_PARTY_NOTICES.txt" = 'notices'
        "${root}THIRD_PARTY_NOTICES/dependency.txt" = 'dependency license'
    }
    if ($includeUnexpectedResource) {
        $entries["${root}Unexpected.dll.mui"] = 'unexpected resource'
    }

    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    $zip = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $entries.Keys) {
            $entry = $zip.CreateEntry($entryName)
            $writer = [System.IO.StreamWriter]::new($entry.Open(), [System.Text.Encoding]::ASCII)
            try { $writer.Write($entries[$entryName]) }
            finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }

    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$path.sha256", "$hash  $name`n")
}

try {
    Write-TestArchive 'win-x64' $false
    Write-TestArchive 'win-arm64' $false
    $candidates = Join-Path $scratch 'candidates'
    & $generator -ReleaseTag $tag -AssetsDirectory $assets -OutputDirectory $candidates
    if (-not (Test-Path -LiteralPath (Join-Path $candidates 'winget/manifests/z/Zettersten/Shnapp/3.0.0/Zettersten.Shnapp.installer.yaml')) -or
        -not (Test-Path -LiteralPath (Join-Path $candidates 'scoop/bucket/shnapp.json')) -or
        -not (Test-Path -LiteralPath (Join-Path $candidates 'chocolatey/shnapp.nuspec'))) {
        throw 'Catalog candidates were not generated for both archives.'
    }

    Write-TestArchive 'win-arm64' $true
    $rejected = $false
    try {
        & $generator -ReleaseTag $tag -AssetsDirectory $assets -OutputDirectory (Join-Path $scratch 'rejected')
    }
    catch {
        if ($_.Exception.Message -notlike '*unexpected file*Unexpected.dll.mui*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Catalog generator accepted an unrelated root .mui file.' }
    Write-Host 'Catalogs accept the WinRT host resource and reject unrelated root .mui files.'
}
finally {
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedScratch = [System.IO.Path]::GetFullPath($scratch)
    if ($resolvedScratch.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($resolvedScratch).StartsWith('shnapp-catalog-test-')) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
