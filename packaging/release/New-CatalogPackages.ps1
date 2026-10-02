param(
    [Parameter(Mandatory = $true)][string] $ReleaseTag,
    [Parameter(Mandatory = $true)][string] $AssetsDirectory,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ReleaseTag -notmatch '^v([1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Catalog packages require a stable v1.0.0-style release tag.'
}
$version = $ReleaseTag.Substring(1)
$assetsPath = (Resolve-Path -LiteralPath $AssetsDirectory).Path
$hashes = @{}

foreach ($rid in @('win-x64', 'win-arm64')) {
    $archiveName = "Shnapp-$rid.zip"
    $archivePath = Join-Path $assetsPath $archiveName
    $checksumPath = "$archivePath.sha256"
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Missing $archiveName or its checksum."
    }

    $line = (Get-Content -LiteralPath $checksumPath -Raw).TrimEnd("`r", "`n")
    $checksumPattern = '^([a-fA-F0-9]{64})  ' + [regex]::Escape($archiveName) + '$'
    if ($line -notmatch $checksumPattern) {
        throw "Invalid checksum file for $archiveName."
    }
    $expectedHash = $Matches[1].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expectedHash -ne $actualHash) {
        throw "SHA-256 mismatch for $archiveName."
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
        if (@($entries | Group-Object | Where-Object Count -gt 1).Count -gt 0) {
            throw "$archiveName contains duplicate ZIP entries."
        }
        foreach ($entry in $entries) {
            if ($entry.StartsWith('/') -or $entry.Contains([char]92) -or $entry.Contains(':') -or
                @($entry.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -gt 0) {
                throw "$archiveName contains an unsafe ZIP entry: $entry"
            }
        }

        $versionRoot = "versions/$ReleaseTag/"
        if ('current-version.txt' -in $entries) {
            $pointer = $zip.GetEntry('current-version.txt')
            $pointerStream = [System.IO.StreamReader]::new($pointer.Open(), [System.Text.Encoding]::ASCII)
            try { $pointerText = $pointerStream.ReadToEnd() }
            finally { $pointerStream.Dispose() }
            if ($pointerText -cne "$ReleaseTag`n`n") {
                throw "$archiveName has an invalid current-version.txt."
            }
            $requiredFiles = @(
                'Shnapp.exe', 'current-version.txt', 'LICENSE', 'THIRD_PARTY_NOTICES.txt',
                "${versionRoot}Shnapp.exe", "${versionRoot}Shnapp.pri",
                "${versionRoot}LICENSE", "${versionRoot}THIRD_PARTY_NOTICES.txt"
            )
            foreach ($entry in $entries) {
                if ($entry -notin @('Shnapp.exe', 'current-version.txt', 'LICENSE',
                    'THIRD_PARTY_NOTICES.txt', 'versions/', $versionRoot) -and
                    -not $entry.StartsWith($versionRoot, [System.StringComparison]::Ordinal)) {
                    throw "$archiveName contains a file outside its versioned payload: $entry"
                }
            }
        }
        elseif ($ReleaseTag -in @('v1.0.0', 'v1.0.2')) {
            # Published releases before the launcher was introduced remain valid
            # inputs for Chocolatey re-submission.
            $requiredFiles = @('Shnapp.exe', 'Shnapp.pri', 'LICENSE', 'THIRD_PARTY_NOTICES.txt')
        }
        else {
            throw "$archiveName does not contain the portable launcher layout."
        }
        foreach ($required in $requiredFiles) {
            if ($required -cnotin $entries) {
                throw "$archiveName does not contain $required."
            }
        }
    }
    finally {
        $zip.Dispose()
    }
    $hashes[$rid] = $actualHash
}

$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    if (@(Get-ChildItem -LiteralPath $outputPath -Force).Count -ne 0) {
        throw "Output directory is not empty: $outputPath"
    }
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$packagingRoot = Split-Path -Parent $PSScriptRoot
$templates = @(
    @{ Source = 'winget/Zettersten.Shnapp.yaml.in'; Destination = "winget/manifests/z/Zettersten/Shnapp/$version/Zettersten.Shnapp.yaml" },
    @{ Source = 'winget/Zettersten.Shnapp.installer.yaml.in'; Destination = "winget/manifests/z/Zettersten/Shnapp/$version/Zettersten.Shnapp.installer.yaml" },
    @{ Source = 'winget/Zettersten.Shnapp.locale.en-US.yaml.in'; Destination = "winget/manifests/z/Zettersten/Shnapp/$version/Zettersten.Shnapp.locale.en-US.yaml" },
    @{ Source = 'scoop/shnapp.json.in'; Destination = 'scoop/bucket/shnapp.json' },
    @{ Source = 'chocolatey/shnapp.nuspec.in'; Destination = 'chocolatey/shnapp.nuspec' },
    @{ Source = 'chocolatey/tools/chocolateyInstall.ps1.in'; Destination = 'chocolatey/tools/chocolateyInstall.ps1' }
)

foreach ($template in $templates) {
    $sourcePath = Join-Path $packagingRoot $template.Source
    $destinationPath = Join-Path $outputPath $template.Destination
    $text = [System.IO.File]::ReadAllText($sourcePath)
    $text = $text.Replace('__VERSION__', $version)
    $text = $text.Replace('__SHA256_X64__', $hashes['win-x64'])
    $text = $text.Replace('__SHA256_ARM64__', $hashes['win-arm64'])
    if ($text -match '__[A-Z0-9_]+__') {
        throw "Unresolved template marker in $sourcePath."
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
    [System.IO.File]::WriteAllText($destinationPath, $text, [System.Text.UTF8Encoding]::new($false))
}

$licensePath = Join-Path (Split-Path -Parent $packagingRoot) 'LICENSE'
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw 'Repository LICENSE is missing.'
}
Copy-Item -LiteralPath $licensePath -Destination (Join-Path $outputPath 'chocolatey/tools/LICENSE')

$scoop = Get-Content -LiteralPath (Join-Path $outputPath 'scoop/bucket/shnapp.json') -Raw | ConvertFrom-Json
if ($scoop.version -ne $version) { throw 'Generated Scoop version mismatch.' }
[xml] $nuspec = Get-Content -LiteralPath (Join-Path $outputPath 'chocolatey/shnapp.nuspec') -Raw
if ($nuspec.package.metadata.version -ne $version) { throw 'Generated Chocolatey version mismatch.' }
Write-Host "Generated WinGet, Scoop, and Chocolatey candidates for $ReleaseTag in $outputPath"
