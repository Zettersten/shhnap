param(
    [Parameter(Mandatory = $true)][string] $DepsPath,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$deps = Get-Content -LiteralPath $DepsPath -Raw | ConvertFrom-Json
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$notices = Join-Path $output 'THIRD_PARTY_NOTICES'
New-Item -ItemType Directory -Path $notices -Force | Out-Null

$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else {
    Join-Path $env:USERPROFILE '.nuget\packages'
}
$packageRoot = [System.IO.Path]::GetFullPath($packageRoot)
$index = [System.Collections.Generic.List[string]]::new()
$index.Add('Third-party notices for Shnapp')
$index.Add('')
$index.Add('Shnapp source and first-party artwork are covered by the adjacent LICENSE file.')
$index.Add('The self-contained distribution also includes code from the components below.')
$index.Add('Each notice is copied from the exact NuGet package used by this build, except')
$index.Add('the Win2D source license, whose package contains no license file.')
$index.Add('')

foreach ($library in $deps.libraries.PSObject.Properties.Name) {
    if ($library -notmatch '^([^/]+)/([^/]+)$') { continue }
    $name = $Matches[1]
    $version = $Matches[2]
    $kind = $deps.libraries.$library.type
    if ($kind -notin @('package', 'runtimepack')) { continue }
    if ($name -eq 'Shnapp' -or $name -eq 'Shnapp.Core' -or
        ($name.StartsWith('runtimepack.') -and -not $name.StartsWith('runtimepack.Microsoft.NETCore.App.Runtime.'))) {
        continue
    }

    $packageName = $name -replace '^runtimepack\.', ''
    $packagePath = Join-Path $packageRoot (Join-Path $packageName.ToLowerInvariant() $version)
    if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) {
        throw "Missing restored NuGet package $name/$version at $packagePath"
    }

    $entryDirName = "$packageName-$version"
    $entryDir = Join-Path $notices $entryDirName
    $sourceFiles = @(Get-ChildItem -LiteralPath $packagePath -File |
        Where-Object { $_.Name -match '^(license|third.?party.?notices?|notice)([.\-_]|$)' -and $_.Extension -in @('.txt', '.md', '') })
    if ($packageName -eq 'Microsoft.Graphics.Win2D' -and $sourceFiles.Count -eq 0) {
        $sourceFiles = @(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'Win2D-LICENSE.txt'))
    }
    if ($sourceFiles.Count -eq 0) {
        throw "No license or notice file found for $name/$version; review its NuGet package before distributing."
    }

    New-Item -ItemType Directory -Path $entryDir -Force | Out-Null
    $index.Add("$packageName $version")
    $index.Add("  Package: https://www.nuget.org/packages/$packageName/$version")
    if ($packageName -eq 'Microsoft.Graphics.Win2D') {
        $index.Add('  Source license: https://github.com/microsoft/Win2D/blob/winappsdk/main/LICENSE.txt')
        $index.Add('  NuGet package license URL is obsolete; review the package terms before release.')
    }
    foreach ($file in $sourceFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $entryDir $file.Name) -Force
        $index.Add("  Notice: THIRD_PARTY_NOTICES/$entryDirName/$($file.Name)")
    }

    if (-not @($sourceFiles | Where-Object { $_.Name -match '^license([.\-_]|$)' }).Count) {
        $nuspecPath = Join-Path $packagePath "$($packageName.ToLowerInvariant()).nuspec"
        if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf)) {
            throw "No license file or nuspec found for $name/$version."
        }
        [xml] $nuspec = Get-Content -LiteralPath $nuspecPath -Raw
        $licenseNode = $nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
        if ($null -ne $licenseNode -and $licenseNode.InnerText -eq 'MIT') {
            $copyrightNode = $nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='copyright']")
            $holder = if ($null -ne $copyrightNode) { $copyrightNode.InnerText } else { $packageName }
            $mitText = @"
MIT License

$holder

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@
            [System.IO.File]::WriteAllText((Join-Path $entryDir 'LICENSE-MIT.txt'), $mitText,
                [System.Text.UTF8Encoding]::new($false))
            $index.Add("  License (NuGet MIT expression): THIRD_PARTY_NOTICES/$entryDirName/LICENSE-MIT.txt")
        }
    }
    $index.Add('')
}

[System.IO.File]::WriteAllLines(
    (Join-Path $output 'THIRD_PARTY_NOTICES.txt'),
    $index,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Added $($index.Count) third-party notice index lines to $output"
